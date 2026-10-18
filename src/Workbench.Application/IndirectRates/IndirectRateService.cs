using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IndirectRates;

/// <summary>Pool setup, rates (append-only, audited), the rate calculation, allocation runs and the true-up.</summary>
public sealed class IndirectRateService(IWorkbenchDbContextFactory dbFactory, AllocationEngine engine, AuditRecorder audit, TimeProvider clock) : IIndirectRateService
{
    public const string RateEntityType = nameof(IndirectRate);
    public const string PoolEntityType = nameof(IndirectPoolSetup);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ---- Pool setup ----

    public async Task<IReadOnlyList<PoolSetupDto>> ListPoolSetupsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var setups = (await db.Set<IndirectPoolSetup>().AsNoTracking().Include(s => s.Accounts).ToListAsync(cancellationToken))
            .OrderBy(s => s.CascadeOrder).ThenBy(s => s.Pool).ToList();
        var map = await PoolAccountMap.LoadAsync(db, setups.Where(s => s.IsActive).ToList(), cancellationToken);
        return setups.Select(s => ToDto(s, map)).ToList();
    }

    public async Task<PoolSetupDto> SavePoolSetupAsync(CostPool pool, SavePoolSetupRequest request, CancellationToken cancellationToken = default)
    {
        var name = string.IsNullOrWhiteSpace(request.Name) ? throw new ArgumentException("Give the pool a name.") : request.Name.Trim();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, cancellationToken);
        Account RequireExpense(Guid id, string what) =>
            accounts.TryGetValue(id, out var a) && a.Type == AccountType.Expense && a.IsActive ? a : throw new ArgumentException($"Choose an active expense account for {what}.");
        RequireExpense(request.AppliedAccountId, "applied burden");
        RequireExpense(request.AbsorbedAccountId, "the applied (contra) account");
        if (request.AppliedAccountId == request.AbsorbedAccountId)
        {
            throw new ArgumentException("The applied-burden and contra accounts must differ.");
        }

        foreach (var id in request.AccountIds ?? [])
        {
            RequireExpense(id, "the pool");
        }

        var domainPool = pool.ToDomain();
        var setup = await db.Set<IndirectPoolSetup>().Include(s => s.Accounts).FirstOrDefaultAsync(s => s.Pool == domainPool, cancellationToken);
        var before = setup is null ? null : Snapshot(setup);
        if (setup is null)
        {
            setup = new IndirectPoolSetup { Pool = domainPool, Name = name };
            db.Set<IndirectPoolSetup>().Add(setup);
        }

        setup.Name = name;
        setup.Base = (AllocationBase)(int)request.Base;
        setup.CascadeOrder = request.CascadeOrder;
        setup.IsActive = request.IsActive;
        setup.CostClasses = string.Join(',', (request.CostClasses ?? []).Select(c => c.ToDomain()).Distinct());
        setup.IncludeMappedIndirectLabor = request.IncludeMappedIndirectLabor;
        setup.AppliedAccountId = request.AppliedAccountId;
        setup.AbsorbedAccountId = request.AbsorbedAccountId;
        setup.UpdatedBy = audit.Actor;
        setup.UpdatedAtUtc = UtcNow;
        var wanted = (request.AccountIds ?? []).Distinct().ToHashSet();
        foreach (var removed in setup.Accounts.Where(a => !wanted.Contains(a.AccountId)).ToList())
        {
            setup.Accounts.Remove(removed);
            db.Set<IndirectPoolAccount>().Remove(removed);
        }

        foreach (var id in wanted.Where(id => setup.Accounts.All(a => a.AccountId != id)))
        {
            var link = new IndirectPoolAccount { PoolSetupId = setup.Id, AccountId = id };
            setup.Accounts.Add(link);
            db.Set<IndirectPoolAccount>().Add(link);
        }

        audit.Record(db, PoolEntityType, setup.Id, before is null ? "Created" : "Updated", before, Snapshot(setup));
        await db.SaveChangesAsync(cancellationToken);
        var all = await db.Set<IndirectPoolSetup>().AsNoTracking().Include(s => s.Accounts).Where(s => s.IsActive).ToListAsync(cancellationToken);
        return ToDto(setup, await PoolAccountMap.LoadAsync(db, all, cancellationToken));
    }

    public async Task<int> InstallDefaultPoolsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var created = await DefaultPools.EnsureAsync(db, audit.Actor, UtcNow, cancellationToken);
        if (created > 0)
        {
            audit.Record(db, PoolEntityType, Guid.Empty, "DefaultsInstalled", after: new { Created = created });
        }

        await db.SaveChangesAsync(cancellationToken);
        return created;
    }

    // ---- Rates ----

    public async Task<IReadOnlyList<IndirectRateDto>> ListRatesAsync(int? fiscalYear = null, bool includeHistory = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rates = await db.Set<IndirectRate>().AsNoTracking()
            .Where(r => (fiscalYear == null || r.FiscalYear == fiscalYear) && (includeHistory || r.SupersededAtUtc == null))
            .ToListAsync(cancellationToken);
        return rates.OrderByDescending(r => r.FiscalYear).ThenBy(r => r.Basis).ThenBy(r => r.Pool).ThenByDescending(r => r.CreatedAtUtc).Select(ToDto).ToList();
    }

    public async Task<IndirectRateDto> SaveRateAsync(SaveIndirectRateRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Rate < 0 || request.Rate > 5)
        {
            throw new ArgumentException("Enter the rate as a fraction between 0 and 5 (0.32 for 32%).");
        }

        if (request.FiscalYear is < 2000 or > 2200)
        {
            throw new ArgumentException("Choose a fiscal year.");
        }

        var rate = decimal.Round(request.Rate, 6, MidpointRounding.AwayFromZero);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pool = request.Pool.ToDomain();
        var basis = (IndirectRateBasis)(int)request.Basis;
        var current = await db.Set<IndirectRate>()
            .Where(r => r.Pool == pool && r.FiscalYear == request.FiscalYear && r.Basis == basis && r.SupersededAtUtc == null)
            .ToListAsync(cancellationToken);
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim();
        if (current.Count > 0 && reason is null)
        {
            throw new ArgumentException($"FY{request.FiscalYear} already has a {request.Basis.ToString().ToLowerInvariant()} rate for this pool; give a reason for the change.");
        }

        var year = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Year == request.FiscalYear, cancellationToken);
        var now = UtcNow;
        var entity = new IndirectRate
        {
            Pool = pool,
            FiscalYear = request.FiscalYear,
            Basis = basis,
            Rate = rate,
            EffectiveFrom = request.EffectiveFrom ?? year?.Start ?? DateOnly.FromDateTime(now),
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            ApprovedBy = request.Approve ? audit.Actor : null,
            ApprovedAtUtc = request.Approve ? now : null,
            CreatedBy = audit.Actor,
            CreatedAtUtc = now,
        };
        db.Set<IndirectRate>().Add(entity);
        foreach (var old in current)
        {
            old.SupersededAtUtc = now;
            old.SupersededById = entity.Id;
            audit.Record(db, RateEntityType, old.Id, "Superseded", Snapshot(old), new { SupersededBy = entity.Id, entity.Rate }, reason);
        }

        audit.Record(db, RateEntityType, entity.Id, "Created", current.Count == 0 ? null : Snapshot(current[0]), Snapshot(entity), reason);
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    public async Task<IndirectRateDto> ApproveRateAsync(Guid rateId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rate = await db.Set<IndirectRate>().FirstOrDefaultAsync(r => r.Id == rateId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Indirect rate", rateId);
        if (!rate.IsCurrent)
        {
            throw new ArgumentException("That rate was superseded; approve the rate in force instead.");
        }

        if (rate.ApprovedAtUtc is null)
        {
            rate.ApprovedBy = audit.Actor;
            rate.ApprovedAtUtc = UtcNow;
            audit.Record(db, RateEntityType, rate.Id, "Approved", after: Snapshot(rate));
            await db.SaveChangesAsync(cancellationToken);
        }

        return ToDto(rate);
    }

    public async Task WithdrawRateAsync(Guid rateId, WithdrawIndirectRateRequest request, CancellationToken cancellationToken = default)
    {
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? throw new ArgumentException("A reason is required.") : request.Reason.Trim();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rate = await db.Set<IndirectRate>().FirstOrDefaultAsync(r => r.Id == rateId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Indirect rate", rateId);
        if (!rate.IsCurrent)
        {
            throw new ArgumentException("That rate is already superseded.");
        }

        rate.SupersededAtUtc = UtcNow;
        audit.Record(db, RateEntityType, rate.Id, "Withdrawn", Snapshot(rate), null, reason);
        await db.SaveChangesAsync(cancellationToken);
    }

    // ---- Reports and runs ----

    public async Task<RateCalculationDto> GetRateCalculationAsync(Guid fromPeriodId, Guid toPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.CalculateRatesAsync(db, fromPeriodId, toPeriodId, cancellationToken);
    }

    public async Task<IReadOnlyList<AllocationRunSummaryDto>> ListAllocationRunsAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.ListRunsAsync(db, fiscalYear, cancellationToken);
    }

    public async Task<AllocationPreviewDto> PreviewAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.PreviewAsync(db, fiscalPeriodId, cancellationToken);
    }

    public async Task<AllocationPreviewDto> PostAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.PostAsync(db, fiscalPeriodId, cancellationToken);
    }

    public async Task<AllocationPreviewDto> ReverseAllocationAsync(Guid fiscalPeriodId, ReverseAllocationRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.ReverseAsync(db, fiscalPeriodId, request.Reason, cancellationToken);
    }

    public async Task<TrueUpDto> GetTrueUpAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.TrueUpAsync(db, fiscalYear, cancellationToken);
    }

    public async Task<TrueUpDto> PostTrueUpAsync(int fiscalYear, PostTrueUpRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await engine.PostTrueUpAsync(db, fiscalYear, request.Date, cancellationToken);
    }

    // ---- Mapping ----

    private static PoolSetupDto ToDto(IndirectPoolSetup s, PoolAccountMap map)
    {
        string Label(Guid id) => map.Accounts.TryGetValue(id, out var a) ? $"{a.Number} {a.Name}" : "?";
        return new PoolSetupDto(s.Id, s.Pool.ToContract(), s.Name, (AllocationBaseKind)(int)s.Base, s.CascadeOrder, s.IsActive,
            s.CostClassList.Select(c => c.ToContract()).ToList(), s.IncludeMappedIndirectLabor, s.Accounts.Select(a => a.AccountId).ToList(),
            s.IsActive ? map.Labels(s.Pool) : [], s.AppliedAccountId, Label(s.AppliedAccountId), s.AbsorbedAccountId, Label(s.AbsorbedAccountId));
    }

    internal static IndirectRateDto ToDto(IndirectRate r) => new(r.Id, r.Pool.ToContract(), r.FiscalYear, (RateBasis)(int)r.Basis, r.Rate, r.EffectiveFrom,
        r.Note, r.ApprovedBy, r.ApprovedAtUtc, r.CreatedBy, r.CreatedAtUtc);

    private static object Snapshot(IndirectRate r) => new { Pool = r.Pool.ToString(), r.FiscalYear, Basis = r.Basis.ToString(), r.Rate, r.EffectiveFrom, r.Note, r.ApprovedBy };

    private static object Snapshot(IndirectPoolSetup s) => new
    {
        Pool = s.Pool.ToString(), s.Name, Base = s.Base.ToString(), s.CascadeOrder, s.IsActive, s.CostClasses, s.IncludeMappedIndirectLabor,
        s.AppliedAccountId, s.AbsorbedAccountId, Accounts = s.Accounts.Select(a => a.AccountId).ToList(),
    };
}

/// <summary>The default fringe → overhead → G&amp;A cascade and its applied-burden accounts (added to an existing chart when missing).</summary>
public static class DefaultPools
{
    private sealed record Def(IndirectPool Pool, string Name, AllocationBase Base, int Order, CostClassification Class, string Applied, string AppliedName, string Absorbed, string AbsorbedName, string AbsorbedParent);

    private static readonly Def[] Defs =
    [
        new(IndirectPool.Fringe, "Fringe", AllocationBase.DirectLabor, 1, CostClassification.Fringe, "5910", "Applied fringe", "6090", "Fringe applied", "6000"),
        new(IndirectPool.Overhead, "Overhead", AllocationBase.DirectLaborPlusFringe, 2, CostClassification.Overhead, "5920", "Applied overhead", "6190", "Overhead applied", "6100"),
        new(IndirectPool.GeneralAndAdministrative, "G&A", AllocationBase.TotalCostInput, 3, CostClassification.GeneralAndAdministrative, "5930", "Applied G&A", "6290", "G&A applied", "6200"),
    ];

    /// <summary>Adds missing pool setups (and their accounts) to <paramref name="db"/> without saving; returns the setups added.</summary>
    public static async Task<int> EnsureAsync(IWorkbenchDbContext db, string actor, DateTime utcNow, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().ToDictionaryAsync(a => a.Number, ct);
        Account Ensure(string number, string name, string? parent)
        {
            if (!accounts.TryGetValue(number, out var account))
            {
                account = new Account
                {
                    Number = number,
                    Name = name,
                    Type = AccountType.Expense,
                    NormalBalance = NormalBalance.Debit,
                    ParentId = parent is null ? null : accounts.GetValueOrDefault(parent)?.Id,
                    CostClass = parent is null || number == "5900" ? CostClassification.None : CostClassification.AppliedBurden,
                    CreatedAtUtc = utcNow,
                };
                db.Set<Account>().Add(account);
                accounts[number] = account;
            }

            return account;
        }

        var existing = (await db.Set<IndirectPoolSetup>().Select(s => s.Pool).ToListAsync(ct)).ToHashSet();
        var created = 0;
        Ensure("5900", "Applied indirect costs", null);
        foreach (var def in Defs.Where(d => !existing.Contains(d.Pool)))
        {
            var applied = Ensure(def.Applied, def.AppliedName, "5900");
            var absorbed = Ensure(def.Absorbed, def.AbsorbedName, def.AbsorbedParent);
            db.Set<IndirectPoolSetup>().Add(new IndirectPoolSetup
            {
                Pool = def.Pool,
                Name = def.Name,
                Base = def.Base,
                CascadeOrder = def.Order,
                CostClasses = def.Class.ToString(),
                AppliedAccountId = applied.Id,
                AbsorbedAccountId = absorbed.Id,
                UpdatedBy = actor,
                UpdatedAtUtc = utcNow,
            });
            created++;
        }

        return created;
    }
}

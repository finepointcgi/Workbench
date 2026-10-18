using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>Posting rules and pools, the unposted list and the backlog, for Finance. The work is done by <see cref="PostingEngine"/>.</summary>
public sealed class PostingService(IWorkbenchDbContextFactory dbFactory, PostingEngine engine, AuditRecorder audit, TimeProvider clock) : IPostingService
{
    /// <summary>Every rule slot the posting engine reads, in display order.</summary>
    private static IEnumerable<(PostingRuleKind Kind, string Key, string Label)> Slots()
    {
        yield return (PostingRuleKind.DirectLabor, "", "Direct labor (Direct charge codes)");
        foreach (var pool in Enum.GetValues<IndirectPool>())
        {
            yield return (PostingRuleKind.IndirectLabor, pool.ToString(), $"Indirect labor - {AccountingMapping.Label(pool.ToContract())} pool");
        }

        yield return (PostingRuleKind.LeaveLabor, "", "Leave and holidays (fringe)");
        foreach (var type in Enum.GetValues<OdcType>())
        {
            yield return (PostingRuleKind.Odc, type.ToString(), $"ODC - {type}");
        }

        yield return (PostingRuleKind.UnallowableOdc, "", "Unallowable - default");
        foreach (var type in Enum.GetValues<OdcType>())
        {
            yield return (PostingRuleKind.UnallowableOdc, type.ToString(), $"Unallowable - {type} (optional)");
        }

        yield return (PostingRuleKind.AccruedPayroll, "", "Accrued payroll (labor credit)");
        yield return (PostingRuleKind.AccountsPayable, "", "Accounts payable (vendor ODC credit)");
        yield return (PostingRuleKind.AccruedReimbursement, "", "Accrued employee reimbursements (expense credit)");
    }

    public async Task<PostingSetupDto> GetSetupAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, cancellationToken);
        var rules = await db.Set<PostingRule>().AsNoTracking().ToDictionaryAsync(r => (r.Kind, r.Key), r => r.AccountId, cancellationToken);
        var ruleDtos = Slots().Select(s =>
        {
            var account = rules.TryGetValue((s.Kind, s.Key), out var id) ? accounts.GetValueOrDefault(id) : null;
            return new PostingRuleDto(s.Kind.ToContract(), s.Key, s.Label, account?.Id, account?.Number, account?.Name);
        }).ToList();

        var context = await PostingContext.LoadAsync(db, false, cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var pools = await db.Set<WbsCostPool>().AsNoTracking().ToDictionaryAsync(p => p.WbsElementId, p => p.Pool, cancellationToken);
        var wbs = context.Wbs.Values.Where(w => w.Kind != ChargeKind.Direct).OrderBy(w => w.Code, StringComparer.Ordinal)
            .Select(w => ToDto(w, projects, pools, context)).ToList();
        return new PostingSetupDto(ruleDtos, wbs);
    }

    public async Task<PostingRuleDto> SaveRuleAsync(SavePostingRuleRequest request, CancellationToken cancellationToken = default)
    {
        var kind = request.Kind.ToDomain();
        var key = request.Key?.Trim() ?? "";
        var slot = Slots().FirstOrDefault(s => s.Kind == kind && s.Key == key);
        if (slot.Label is null)
        {
            throw new ArgumentException($"'{key}' isn't a valid key for {request.Kind} rules.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rule = await db.Set<PostingRule>().FirstOrDefaultAsync(r => r.Kind == kind && r.Key == key, cancellationToken);
        var before = rule is null ? null : new { rule.AccountId };

        // An empty account clears an optional override (unallowable by ODC type).
        if (request.AccountId == Guid.Empty)
        {
            if (kind != PostingRuleKind.UnallowableOdc || key.Length == 0)
            {
                throw new ArgumentException("Choose an account; this rule is required.");
            }

            if (rule is not null)
            {
                db.Set<PostingRule>().Remove(rule);
                audit.Record(db, nameof(PostingRule), rule.Id, "Cleared", before, null);
                await db.SaveChangesAsync(cancellationToken);
            }

            return new PostingRuleDto(request.Kind, key, slot.Label, null, null, null);
        }

        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AccountId, cancellationToken)
            ?? throw new ArgumentException("The account doesn't exist.");
        ValidateAccount(kind, account);
        if (await db.Set<Account>().AnyAsync(a => a.ParentId == account.Id, cancellationToken))
        {
            throw new ArgumentException($"{account.Number} is a summary account; choose one of its detail accounts.");
        }

        if (rule is null)
        {
            rule = new PostingRule { Kind = kind, Key = key, AccountId = account.Id };
            db.Set<PostingRule>().Add(rule);
        }

        rule.AccountId = account.Id;
        rule.UpdatedBy = audit.Actor;
        rule.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        audit.Record(db, nameof(PostingRule), rule.Id, before is null ? "Created" : "Updated", before, new { Kind = kind.ToString(), Key = key, AccountId = account.Id, account.Number });
        await db.SaveChangesAsync(cancellationToken);
        return new PostingRuleDto(request.Kind, key, slot.Label, account.Id, account.Number, account.Name);
    }

    public async Task<WbsPoolDto> SetWbsPoolAsync(Guid wbsElementId, SetWbsPoolRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsElementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        if (wbs.Kind == ChargeKind.Direct)
        {
            throw new ArgumentException($"{wbs.Code} is a direct charge code; only indirect and leave elements belong to a pool.");
        }

        var existing = await db.Set<WbsCostPool>().FirstOrDefaultAsync(p => p.WbsElementId == wbsElementId, cancellationToken);
        var before = existing is null ? null : new { Pool = existing.Pool.ToString() };
        if (request.Pool is null)
        {
            if (existing is not null)
            {
                db.Set<WbsCostPool>().Remove(existing);
            }
        }
        else
        {
            existing ??= db.Set<WbsCostPool>().Add(new WbsCostPool { WbsElementId = wbsElementId }).Entity;
            existing.Pool = request.Pool.Value.ToDomain();
            existing.UpdatedBy = audit.Actor;
            existing.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        }

        audit.Record(db, nameof(WbsCostPool), wbsElementId, "PoolChanged", before, request.Pool is null ? null : new { Pool = request.Pool.ToString() });
        await db.SaveChangesAsync(cancellationToken);

        var context = await PostingContext.LoadAsync(db, false, cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var pools = await db.Set<WbsCostPool>().AsNoTracking().ToDictionaryAsync(p => p.WbsElementId, p => p.Pool, cancellationToken);
        return ToDto(wbs, projects, pools, context);
    }

    public Task<IReadOnlyList<UnpostedItemDto>> ListUnpostedAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        engine.ListUnpostedAsync(fiscalPeriodId, cancellationToken);

    public async Task<PostingResultDto> PostBacklogAsync(CancellationToken cancellationToken = default) =>
        ToDto(await engine.PostBacklogAsync(cancellationToken));

    public async Task<PostingResultDto> PostTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        ToDto(await engine.PostTimesheetAsync(timesheetId, false, cancellationToken));

    public async Task<PostingResultDto> PostOdcAsync(Guid odcId, CancellationToken cancellationToken = default) =>
        ToDto(await engine.PostOdcAsync(odcId, false, cancellationToken));

    private static PostingResultDto ToDto(PostingOutcome o) => new(o.Posted, o.Reversed, o.AlreadyPosted, o.Problems);

    /// <summary>Costs go to expense accounts, credits to liabilities, and unallowable costs only to unallowable accounts (and vice versa).</summary>
    private static void ValidateAccount(PostingRuleKind kind, Account account)
    {
        if (!account.IsActive)
        {
            throw new ArgumentException($"{account.Number} {account.Name} is inactive.");
        }

        var isCredit = kind is PostingRuleKind.AccruedPayroll or PostingRuleKind.AccountsPayable or PostingRuleKind.AccruedReimbursement;
        if (isCredit && account.Type != AccountType.Liability)
        {
            throw new ArgumentException($"Map {kind} to a liability account; {account.Number} is {account.Type}.");
        }

        if (!isCredit && account.Type != AccountType.Expense)
        {
            throw new ArgumentException($"Map costs to an expense account; {account.Number} is {account.Type}.");
        }

        if (kind == PostingRuleKind.UnallowableOdc && account.CostClass != CostClassification.Unallowable)
        {
            throw new ArgumentException($"Unallowable costs must stay segregated: choose an account classified Unallowable ({account.Number} is {account.CostClass}).");
        }

        if (kind != PostingRuleKind.UnallowableOdc && account.CostClass == CostClassification.Unallowable)
        {
            throw new ArgumentException($"{account.Number} is an unallowable account; use it only for unallowable costs.");
        }
    }

    private static WbsPoolDto ToDto(WbsElement w, Dictionary<Guid, string> projects, Dictionary<Guid, IndirectPool> pools, PostingContext context)
    {
        var (pool, inherited, isExplicit) = context.EffectivePool(w);
        return new WbsPoolDto(w.Id, projects.GetValueOrDefault(w.ProjectId) ?? "", w.Code, w.Name, w.Kind.ToString(),
            pools.TryGetValue(w.Id, out var own) ? own.ToContract() : null, pool.ToContract(), inherited && isExplicit);
    }
}

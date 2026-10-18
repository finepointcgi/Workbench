using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Billing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.IndirectRates;

/// <summary>The rates applied to one fiscal year: the basis actually used and each pool's rate (0 for <see cref="Missing"/> pools).</summary>
internal sealed record RateSet(int FiscalYear, IndirectRateBasis Basis, IReadOnlyDictionary<IndirectPool, decimal> Rates, IReadOnlyList<IndirectPool> Missing)
{
    public decimal Of(IndirectPool pool) => Rates.GetValueOrDefault(pool);

    public AppliedRates ToContract() => new((RateBasis)(int)Basis, FiscalYear, Of(IndirectPool.Fringe), Of(IndirectPool.Overhead), Of(IndirectPool.GeneralAndAdministrative));
}

/// <summary>Active pool setups (in cascade order), the rates in force and the fiscal calendar, loaded once per operation.</summary>
internal sealed class RateBook
{
    private List<IndirectRate> _rates = [];

    public IReadOnlyList<IndirectPoolSetup> Setups { get; private set; } = [];
    public IReadOnlyList<PoolStep> Steps { get; private set; } = [];
    public IReadOnlyList<FiscalYear> Years { get; private set; } = [];

    public bool HasRates => Steps.Count > 0 && _rates.Count > 0;

    public static async Task<RateBook> LoadAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var setups = (await db.Set<IndirectPoolSetup>().AsNoTracking().Include(s => s.Accounts).Where(s => s.IsActive).ToListAsync(ct))
            .OrderBy(s => s.CascadeOrder).ThenBy(s => s.Pool).ToList();
        return new RateBook
        {
            Setups = setups,
            Steps = setups.Select(s => new PoolStep(s.Pool, s.Base)).ToList(),
            _rates = await db.Set<IndirectRate>().AsNoTracking().Where(r => r.SupersededAtUtc == null).ToListAsync(ct),
            Years = (await db.Set<FiscalYear>().AsNoTracking().ToListAsync(ct)).OrderBy(y => y.Start).ToList(),
        };
    }

    public FiscalYear? YearOf(DateOnly date) => Years.FirstOrDefault(y => y.Start <= date && date <= y.End);

    public decimal? Rate(IndirectPool pool, int fiscalYear, IndirectRateBasis basis) =>
        _rates.Where(r => r.Pool == pool && r.FiscalYear == fiscalYear && r.Basis == basis)
            .OrderByDescending(r => r.EffectiveFrom).ThenByDescending(r => r.CreatedAtUtc)
            .Select(r => (decimal?)r.Rate).FirstOrDefault();

    /// <summary>
    /// The requested basis when every active pool has a rate on it for the year; otherwise the provisional rates
    /// (pools without one count as 0 and are listed in <see cref="RateSet.Missing"/>).
    /// </summary>
    public RateSet For(int fiscalYear, IndirectRateBasis requested)
    {
        if (requested != IndirectRateBasis.Provisional && Steps.Count > 0 && Steps.All(s => Rate(s.Pool, fiscalYear, requested) is not null))
        {
            return new RateSet(fiscalYear, requested, Steps.ToDictionary(s => s.Pool, s => Rate(s.Pool, fiscalYear, requested)!.Value), []);
        }

        var rates = new Dictionary<IndirectPool, decimal>();
        var missing = new List<IndirectPool>();
        foreach (var step in Steps)
        {
            if (Rate(step.Pool, fiscalYear, IndirectRateBasis.Provisional) is { } r)
            {
                rates[step.Pool] = r;
            }
            else
            {
                missing.Add(step.Pool);
            }
        }

        return new RateSet(fiscalYear, IndirectRateBasis.Provisional, rates, missing);
    }

    public Burden Apply(RateSet set, decimal labor, decimal odc) => BurdenCascade.Apply(Steps, labor, odc, set.Of);

    public IndirectPoolSetup? Setup(IndirectPool pool) => Setups.FirstOrDefault(s => s.Pool == pool);

    public static string Label(IndirectPool pool) => pool == IndirectPool.GeneralAndAdministrative ? "G&A" : pool.ToString();

    public static string MissingNote(RateSet set) =>
        $"FY{set.FiscalYear} has no provisional {string.Join(", ", set.Missing.Select(Label))} rate; that burden counts as 0.";
}

/// <summary>Approved direct cost on one WBS element and date: labor at cost, allowable ODCs and unallowable ODCs (never burdened).</summary>
internal sealed record DirectCost(Guid ProjectId, Guid WbsElementId, DateOnly Date, decimal Hours, decimal Labor, decimal Odc, decimal Unallowable);

internal static class DirectCosts
{
    /// <summary>
    /// Approved time on direct WBS elements (hours × the cost rate effective on the work date, per entry to the cent, like
    /// the GL and the financials) and approved ODCs, dated <paramref name="start"/> through <paramref name="end"/>.
    /// </summary>
    public static async Task<List<DirectCost>> LoadAsync(IWorkbenchDbContext db, Guid? projectId, DateOnly start, DateOnly end, ICollection<string> notes, CancellationToken ct)
    {
        var wbs = db.Set<WbsElement>().AsNoTracking().Where(w => w.Kind == ChargeKind.Direct && (projectId == null || w.ProjectId == projectId));
        var labor = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                join w in wbs on e.WbsElementId equals w.Id
                where t.Status == TimesheetStatus.Approved && e.WorkDate >= start && e.WorkDate <= end && e.Hours != 0
                select new { w.ProjectId, e.WbsElementId, t.EmployeeId, e.WorkDate, e.Hours })
            .ToListAsync(ct);
        var employeeIds = labor.Select(l => l.EmployeeId).Distinct().ToList();
        var rates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            rates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var result = new List<DirectCost>();
        var missing = 0m;
        foreach (var l in labor)
        {
            var rate = rates.Find(l.EmployeeId, l.WorkDate);
            if (rate is null)
            {
                missing += l.Hours;
            }

            result.Add(new DirectCost(l.ProjectId, l.WbsElementId, l.WorkDate, l.Hours, Money.Round(l.Hours * (rate ?? 0)), 0, 0));
        }

        if (missing != 0)
        {
            notes.Add($"{missing:N1} approved hours have no cost rate on their work date and are costed at $0.");
        }

        var odcs = await (
                from o in db.Set<OdcEntry>().AsNoTracking()
                join w in wbs on o.WbsElementId equals w.Id
                where o.Status == OdcStatus.Approved && o.IncurredOn >= start && o.IncurredOn <= end
                select new { w.ProjectId, o.WbsElementId, o.IncurredOn, o.Amount, o.IsUnallowable })
            .ToListAsync(ct);
        result.AddRange(odcs.Select(o => new DirectCost(o.ProjectId, o.WbsElementId, o.IncurredOn, 0,
            0, o.IsUnallowable ? 0 : Money.Round(o.Amount), o.IsUnallowable ? Money.Round(o.Amount) : 0)));
        return result;
    }
}

/// <summary>Which GL accounts make up each pool, and the direct-cost base accounts.</summary>
internal sealed class PoolAccountMap
{
    public required Dictionary<IndirectPool, HashSet<Guid>> Pools { get; init; }
    public required HashSet<Guid> DirectLabor { get; init; }
    public required HashSet<Guid> DirectOdc { get; init; }
    public required Dictionary<Guid, Account> Accounts { get; init; }

    public static async Task<PoolAccountMap> LoadAsync(IWorkbenchDbContext db, IReadOnlyList<IndirectPoolSetup> setups, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var rules = await db.Set<PostingRule>().AsNoTracking().ToListAsync(ct);
        return Build(setups, accounts, rules);
    }

    public static PoolAccountMap Build(IReadOnlyList<IndirectPoolSetup> setups, Dictionary<Guid, Account> accounts, IReadOnlyList<PostingRule> rules)
    {
        var children = accounts.Values.Where(a => a.ParentId is not null).ToLookup(a => a.ParentId!.Value);
        IEnumerable<Guid> WithDescendants(Guid id)
        {
            yield return id;
            foreach (var child in children[id])
            {
                foreach (var d in WithDescendants(child.Id))
                {
                    yield return d;
                }
            }
        }

        static bool Poolable(Account a) => a.Type == AccountType.Expense && a.CostClass is not (CostClassification.Unallowable or CostClassification.AppliedBurden
            or CostClassification.DirectLabor or CostClassification.DirectOdc);

        var taken = new HashSet<Guid>();
        var pools = new Dictionary<IndirectPool, HashSet<Guid>>();
        foreach (var setup in setups.OrderBy(s => s.CascadeOrder))
        {
            var classes = setup.CostClassList;
            var ids = accounts.Values.Where(a => classes.Contains(a.CostClass)).Select(a => a.Id)
                .Concat(setup.Accounts.SelectMany(a => WithDescendants(a.AccountId)));
            if (setup.IncludeMappedIndirectLabor)
            {
                ids = ids.Concat(rules.Where(r => r.Kind == PostingRuleKind.IndirectLabor && r.Key == setup.Pool.ToString()).Select(r => r.AccountId));
                if (setup.Pool == IndirectPool.Fringe)
                {
                    ids = ids.Concat(rules.Where(r => r.Kind == PostingRuleKind.LeaveLabor).Select(r => r.AccountId));
                }
            }

            // An account counts in the first pool (in cascade order) that claims it.
            pools[setup.Pool] = ids.Where(id => accounts.TryGetValue(id, out var a) && Poolable(a) && taken.Add(id)).ToHashSet();
        }

        return new PoolAccountMap
        {
            Pools = pools,
            Accounts = accounts,
            DirectLabor = accounts.Values.Where(a => a.CostClass == CostClassification.DirectLabor).Select(a => a.Id).ToHashSet(),
            DirectOdc = accounts.Values.Where(a => a.CostClass == CostClassification.DirectOdc).Select(a => a.Id).ToHashSet(),
        };
    }

    public IReadOnlyList<string> Labels(IndirectPool pool) =>
        Pools.TryGetValue(pool, out var ids)
            ? ids.Select(id => Accounts[id]).OrderBy(a => a.Number, StringComparer.Ordinal).Select(a => $"{a.Number} {a.Name}").ToList()
            : [];
}

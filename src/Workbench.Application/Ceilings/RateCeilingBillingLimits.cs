using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.IndirectRates;
using Workbench.Contracts.Billing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Ceilings;

/// <summary>
/// The contract's rate ceilings in billing: indirect rates above a cap are billed at the cap (per fiscal year, through the
/// same cascade the indirect-rates module uses), fee rates above a fee rate cap at the cap, and fee stops at a fee ceiling.
/// </summary>
public sealed class RateCeilingBillingLimits : IBillingLimits
{
    private static readonly RateCeilingKind[] IndirectKinds = [RateCeilingKind.Fringe, RateCeilingKind.Overhead, RateCeilingKind.GeneralAndAdministrative];

    public async Task<BurdenedCostResult> AdjustCostAsync(
        IWorkbenchDbContext db, Guid projectId, DateOnly from, DateOnly to, RateBasis basis, BurdenedCostResult cost, CancellationToken ct)
    {
        var contractId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.ContractId).FirstOrDefaultAsync(ct);
        if (contractId is null)
        {
            return cost;
        }

        var caps = await db.Set<RateCeiling>().AsNoTracking()
            .Where(r => r.ContractId == contractId && r.IsActive && IndirectKinds.Contains(r.Kind) && r.MaxRate != null)
            .ToListAsync(ct);
        if (caps.Count == 0 || !cost.Rates.Any(r => Exceeds(r, caps)))
        {
            return cost;
        }

        // Re-burden the same approved direct cost with each fiscal year's rates capped.
        var book = await RateBook.LoadAsync(db, ct);
        var direct = await DirectCosts.LoadAsync(db, projectId, from, to, [], ct);
        var wbsIds = direct.Select(c => c.WbsElementId).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var sets = new Dictionary<int, RateSet>();
        RateSet? SetFor(DateOnly date)
        {
            if (book.YearOf(date) is not { } year)
            {
                return null;
            }

            if (!sets.TryGetValue(year.Year, out var set))
            {
                var found = book.For(year.Year, (IndirectRateBasis)(int)basis);
                sets[year.Year] = set = found with { Rates = found.Rates.ToDictionary(kv => kv.Key, kv => Capped(kv.Key, year.Year, kv.Value, caps)) };
            }

            return set;
        }

        var lines = new List<BurdenedCostLine>();
        foreach (var element in direct.GroupBy(c => c.WbsElementId))
        {
            var w = wbs[element.Key];
            var burden = Burden.Zero;
            foreach (var year in element.GroupBy(c => SetFor(c.Date)))
            {
                if (year.Key is { } set)
                {
                    burden = burden.Add(book.Apply(set, year.Sum(c => c.Labor), year.Sum(c => c.Odc)));
                }
            }

            lines.Add(new BurdenedCostLine(w.ProjectId, w.Id, w.Code, w.ClinId,
                element.Sum(c => c.Hours), element.Sum(c => c.Labor), burden.Fringe, burden.Overhead, burden.GeneralAndAdministrative,
                element.Sum(c => c.Odc), element.Sum(c => c.Unallowable)));
        }

        return new BurdenedCostResult(
            lines.OrderBy(l => l.WbsCode, StringComparer.Ordinal).ToList(),
            sets.Values.OrderBy(s => s.FiscalYear).Select(s => s.ToContract()).ToList());
    }

    public async Task<IReadOnlySet<int>> FeeCapYearsAsync(IWorkbenchDbContext db, Guid contractId, Guid clinId, CancellationToken ct) =>
        (await FeeCaps(db, contractId, clinId).Where(r => r.FiscalYear != null).Select(r => r.FiscalYear!.Value).Distinct().ToListAsync(ct)).ToHashSet();

    public async Task<FeeLimit> FeeLimitAsync(
        IWorkbenchDbContext db, Guid contractId, Guid clinId, int? fiscalYear, decimal feeRate, FeeToDate prior, CancellationToken ct)
    {
        // Caps for every year, plus the ones for the fiscal year of the cost.
        var caps = await FeeCaps(db, contractId, clinId).Where(r => r.FiscalYear == null || r.FiscalYear == fiscalYear).ToListAsync(ct);
        if (caps.Count == 0)
        {
            return new FeeLimit(feeRate, null, null);
        }

        var notes = new List<string>();
        var rate = feeRate;
        foreach (var cap in caps.Where(c => c.Kind == RateCeilingKind.FeeRate).OrderBy(c => c.MaxRate))
        {
            if (cap.MaxRate < rate)
            {
                rate = cap.MaxRate!.Value;
                notes.Add($"fee rate capped at {rate:P2}{Year(cap)}");
            }
        }

        decimal? max = null;
        foreach (var cap in caps.Where(c => c.Kind == RateCeilingKind.FeeAmount && c.MaxAmount != null))
        {
            var billed = (cap.ClinId, cap.FiscalYear) switch
            {
                (null, null) => prior.Total,
                (null, { } y) => prior.OfYear(y),
                (_, null) => prior.Of(clinId),
                (_, { } y) => prior.OfYear(clinId, y),
            };
            var left = Math.Max(0, cap.MaxAmount!.Value - billed);
            if (max is null || left < max)
            {
                max = left;
            }

            notes.Add($"fee ceiling of {cap.MaxAmount:C}{(cap.ClinId is null ? " on the contract" : "")}{Year(cap)} ({billed:C} billed before, {left:C} left)");
        }

        var number = await db.Set<Clin>().AsNoTracking().Where(c => c.Id == clinId).Select(c => c.Number).FirstOrDefaultAsync(ct);
        return new FeeLimit(rate, max, $"CLIN {number} fee{(fiscalYear is { } fy ? $" on FY{fy} cost" : "")} limited by the contract's rate ceilings: {string.Join("; ", notes)}.");
    }

    private static IQueryable<RateCeiling> FeeCaps(IWorkbenchDbContext db, Guid contractId, Guid clinId) =>
        db.Set<RateCeiling>().AsNoTracking()
            .Where(r => r.ContractId == contractId && r.IsActive && (r.ClinId == null || r.ClinId == clinId)
                && (r.Kind == RateCeilingKind.FeeRate || r.Kind == RateCeilingKind.FeeAmount));

    private static string Year(RateCeiling cap) => cap.FiscalYear is { } y ? $" for FY{y}" : "";

    private static bool Exceeds(AppliedRates r, IReadOnlyList<RateCeiling> caps) =>
        Capped(IndirectPool.Fringe, r.FiscalYear, r.Fringe, caps) < r.Fringe
        || Capped(IndirectPool.Overhead, r.FiscalYear, r.Overhead, caps) < r.Overhead
        || Capped(IndirectPool.GeneralAndAdministrative, r.FiscalYear, r.GeneralAndAdministrative, caps) < r.GeneralAndAdministrative;

    private static decimal Capped(IndirectPool pool, int fiscalYear, decimal rate, IReadOnlyList<RateCeiling> caps)
    {
        var kind = pool switch
        {
            IndirectPool.Fringe => RateCeilingKind.Fringe,
            IndirectPool.Overhead => RateCeilingKind.Overhead,
            _ => RateCeilingKind.GeneralAndAdministrative,
        };
        var applicable = caps.Where(c => c.Kind == kind && (c.FiscalYear is null || c.FiscalYear == fiscalYear)).Select(c => c.MaxRate!.Value).ToList();
        return applicable.Count == 0 ? rate : Math.Min(rate, applicable.Min());
    }
}

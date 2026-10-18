using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// Allowable cost for cost-plus billing. Uses the indirect-rates module's <see cref="IBurdenedCostService"/> when one is
/// registered; without it, falls back to direct cost only (approved labor at the employee's cost rate plus approved,
/// allowable ODCs) and says so in <see cref="RateNote"/>.
/// </summary>
public sealed class BillingCostSource(IEnumerable<IBurdenedCostService> providers, IEnumerable<IBillingLimits>? limits = null)
{
    public const string NotConfiguredNote = "Indirect rates not configured: direct cost only (no fringe, overhead or G&A).";

    private readonly IBurdenedCostService? _provider = providers.LastOrDefault();

    public bool HasIndirectRates => _provider is not null;

    public async Task<BurdenedCostResult> GetProjectCostAsync(IWorkbenchDbContext db, Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken ct)
    {
        if (_provider is null)
        {
            return await DirectCostAsync(db, projectId, from, to, ct);
        }

        var cost = await _provider.GetProjectCostAsync(projectId, from, to, basis, ct);
        foreach (var limit in limits ?? [])
        {
            cost = await limit.AdjustCostAsync(db, projectId, from, to, basis, cost, ct); // Contract rate ceilings (ceilings module).
        }

        return cost;
    }

    public static string RateNote(BurdenedCostResult? result, bool hasIndirectRates)
    {
        if (!hasIndirectRates)
        {
            return NotConfiguredNote;
        }

        var rates = result?.Rates ?? [];
        return rates.Count == 0
            ? "Burdened cost (no rates reported)"
            : string.Join("; ", rates.OrderBy(r => r.FiscalYear).Select(r =>
                $"{r.Basis} FY{r.FiscalYear}: fringe {r.Fringe:P2}, overhead {r.Overhead:P2}, G&A {r.GeneralAndAdministrative:P2}"));
    }

    /// <summary>Direct labor at cost plus allowable ODCs, per direct WBS element, from approved records only.</summary>
    internal static async Task<BurdenedCostResult> DirectCostAsync(IWorkbenchDbContext db, Guid projectId, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking()
            .Where(w => w.ProjectId == projectId && w.Kind == ChargeKind.Direct).ToDictionaryAsync(w => w.Id, ct);
        var ids = wbs.Keys.ToList();
        var hours = await (
                from e in db.Set<TimeEntry>().AsNoTracking()
                join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                where t.Status == TimesheetStatus.Approved && ids.Contains(e.WbsElementId) && e.WorkDate >= start && e.WorkDate <= end
                select new { e.WbsElementId, t.EmployeeId, e.WorkDate, e.Hours })
            .ToListAsync(ct);
        var employeeIds = hours.Select(h => h.EmployeeId).Distinct().ToList();
        var costRates = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => employeeIds.Contains(r.EmployeeId)).ToListAsync(ct))
        {
            costRates.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        var odcs = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => ids.Contains(o.WbsElementId) && o.Status == OdcStatus.Approved && o.IncurredOn >= start && o.IncurredOn <= end)
            .Select(o => new { o.WbsElementId, o.Amount, o.IsUnallowable })
            .ToListAsync(ct);

        var lines = new List<BurdenedCostLine>();
        foreach (var id in hours.Select(h => h.WbsElementId).Concat(odcs.Select(o => o.WbsElementId)).Distinct())
        {
            var element = wbs[id];
            var h = hours.Where(x => x.WbsElementId == id).ToList();
            var labor = Money.Round(h.Sum(x => x.Hours * (costRates.Find(x.EmployeeId, x.WorkDate) ?? 0m)));
            var o = odcs.Where(x => x.WbsElementId == id).ToList();
            lines.Add(new BurdenedCostLine(projectId, id, element.Code, element.ClinId, h.Sum(x => x.Hours), labor, 0, 0, 0,
                o.Where(x => !x.IsUnallowable).Sum(x => x.Amount), o.Where(x => x.IsUnallowable).Sum(x => x.Amount)));
        }

        return new BurdenedCostResult(lines.OrderBy(l => l.WbsCode).ToList(), []);
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>
/// Keeps labor cost in step with pay. Labor is costed from <see cref="EmployeeCostRate"/> on each work date (the GL
/// posting, project financials, cost reports, burdened cost, pricing and the payroll reconciliation all read it), so a
/// pay rate change writes the matching cost rate (the hourly pay, or the salary over 2,080 hours) from the same date,
/// and a change dated before labor that's already posted adjusts that labor (<see cref="PostingEngine.AdjustTimesheetLaborAsync"/>).
/// <para>
/// A first pay profile doesn't replace a cost rate that's already on file for its date (one imported from Costpoint, say);
/// every later pay change does.
/// </para>
/// </summary>
public sealed class LaborRateChanges(IWorkbenchDbContextFactory dbFactory, PostingEngine posting, ILogger<LaborRateChanges> logger)
{
    /// <summary>
    /// Adds (or corrects, for the same date) the cost rate matching <paramref name="rate"/> to <paramref name="db"/>,
    /// unsaved. Returns false when nothing changed. <paramref name="initial"/> is a first pay profile: it only fills a gap.
    /// </summary>
    public static async Task<bool> SyncCostRateAsync(IWorkbenchDbContext db, EmployeePayRate rate, bool initial, CancellationToken ct)
    {
        if (rate.EquivalentHourlyRate is not { } hourly)
        {
            return false;
        }

        var existing = await db.Set<EmployeeCostRate>().Where(r => r.EmployeeId == rate.EmployeeId).ToListAsync(ct);
        if (initial && existing.Any(r => r.EffectiveFrom <= rate.EffectiveFrom))
        {
            return false;
        }

        var same = existing.FirstOrDefault(r => r.EffectiveFrom == rate.EffectiveFrom);
        if (same is not null)
        {
            if (same.HourlyCost == hourly)
            {
                return false;
            }

            same.HourlyCost = hourly;
            return true;
        }

        var inEffect = existing.Where(r => r.EffectiveFrom <= rate.EffectiveFrom).MaxBy(r => r.EffectiveFrom);
        if (inEffect?.HourlyCost == hourly && existing.All(r => r.EffectiveFrom <= rate.EffectiveFrom))
        {
            return false;
        }

        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = rate.EmployeeId, EffectiveFrom = rate.EffectiveFrom, HourlyCost = hourly });
        return true;
    }

    /// <summary>
    /// Re-costs the employee's approved, posted timesheets with hours on or after <paramref name="effectiveFrom"/> and
    /// posts the differences. Call after the rate change is saved. Failures are logged and returned, never thrown: the
    /// rate change stands and the timesheet stays as posted.
    /// </summary>
    public async Task<IReadOnlyList<LaborAdjustmentOutcome>> AdjustPostedLaborAsync(Guid employeeId, DateOnly effectiveFrom, string reason, CancellationToken ct = default)
    {
        List<Guid> sheets;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            sheets = await db.Set<Timesheet>().AsNoTracking()
                .Where(t => t.EmployeeId == employeeId && t.Status == TimesheetStatus.Approved && t.PeriodEnd >= effectiveFrom)
                .OrderBy(t => t.PeriodStart).Select(t => t.Id).ToListAsync(ct);
        }

        var outcomes = new List<LaborAdjustmentOutcome>();
        foreach (var id in sheets)
        {
            try
            {
                var outcome = await posting.AdjustTimesheetLaborAsync(id, effectiveFrom, reason, ct);
                if (outcome.Problem is not null)
                {
                    logger.LogWarning("Couldn't adjust labor of timesheet {TimesheetId} for a rate change: {Problem}", id, outcome.Problem);
                }

                if (outcome.Adjusted || outcome.Problem is not null)
                {
                    outcomes.Add(outcome);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or DbUpdateException)
            {
                logger.LogError(ex, "Adjusting labor of timesheet {TimesheetId} for a rate change failed", id);
                outcomes.Add(new LaborAdjustmentOutcome(id, null, 0, [], [], false, ex.Message));
            }
        }

        return outcomes;
    }
}

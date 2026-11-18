using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>
/// Keeps labor cost in step with pay. Labor is costed from <see cref="EmployeeCostRate"/> on each work date (the GL
/// posting, project financials, cost reports, burdened cost, pricing and the payroll reconciliation all read it), so a
/// pay rate change writes the matching cost rate (the hourly pay, or the salary over 2,080 hours) from the same date,
/// and a change dated before labor that's already posted adjusts that labor (<see cref="PostingEngine.AdjustTimesheetLaborAsync"/>).
/// <para>
/// The pay-rate side (a first pay profile or a pay change writing the matching cost rate) is the workforce module's <c>PayRateCostSync</c>; what stays here is the re-costing of posted labor, which a cost rate change in core needs too.
/// </para>
/// </summary>
public sealed class LaborRateChanges(IWorkbenchDbContextFactory dbFactory, PostingEngine posting, ILogger<LaborRateChanges> logger)
{
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

using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.TotalTime;

/// <summary>
/// The audit trail of a timesheet's labor cost: each time an approved revision is posted, its method, hours, uncompensated
/// hours, effective rate and cost are recorded; a revised timesheet's row ("Recomputed") carries the previous figures as before.
/// </summary>
public static class LaborCostAudit
{
    public const string EntityType = "TimesheetLaborCost";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public sealed record Snapshot(
        int Revision, string Method, bool IsExempt, decimal StandardHours, decimal WorkedHours, decimal UncompensatedHours,
        decimal? PeriodSalary, decimal LaborCost, decimal? EffectiveRate, bool EffectiveRateApplied);

    public static Snapshot Of(int revision, TimesheetCost cost) => new(revision, cost.Method.ToString(), cost.IsExempt, cost.StandardHours, cost.WorkedHours,
        cost.UncompensatedHours, cost.PeriodSalary, cost.LaborCost, cost.EffectiveRate, cost.EffectiveRateApplied);

    /// <summary>Adds the audit row to <paramref name="db"/> (saved with the caller's SaveChanges).</summary>
    public static async Task RecordAsync(IWorkbenchDbContext db, Guid timesheetId, int revision, TimesheetCost cost, string actor, DateTime utcNow, CancellationToken ct)
    {
        var previous = (await db.Set<AuditEvent>().AsNoTracking().Where(a => a.EntityType == EntityType && a.EntityId == timesheetId).ToListAsync(ct))
            .OrderBy(a => a.OccurredAtUtc).ThenBy(a => a.Id).LastOrDefault();
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = EntityType,
            EntityId = timesheetId,
            Action = previous is null ? "Computed" : "Recomputed",
            BeforeJson = previous?.AfterJson,
            AfterJson = JsonSerializer.Serialize(Of(revision, cost), Json),
            Actor = actor,
            OccurredAtUtc = utcNow,
            Reason = previous is null ? $"Labor cost posted ({cost.Method})" : $"Timesheet revision {revision} posted; labor cost recomputed ({cost.Method})",
        });
    }
}

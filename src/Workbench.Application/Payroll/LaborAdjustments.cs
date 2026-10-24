using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts.Payroll;
using Workbench.Domain.Accounting;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Payroll;

/// <summary>Reads <see cref="LaborCostAdjustment"/>s for the payroll profile and compensation screens.</summary>
public static class LaborAdjustments
{
    public static async Task<IReadOnlyList<LaborAdjustmentDto>> ListAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        var rows = (await db.Set<LaborCostAdjustment>().AsNoTracking().Where(a => a.EmployeeId == employeeId).ToListAsync(ct))
            .OrderByDescending(a => a.CreatedAtUtc).ThenByDescending(a => a.Id).ToList();
        return await MapAsync(db, rows, ct);
    }

    /// <summary>The outcomes of one rate change: adjustments made, plus timesheets that couldn't be adjusted.</summary>
    public static async Task<IReadOnlyList<LaborAdjustmentDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<LaborAdjustmentOutcome> outcomes, CancellationToken ct)
    {
        var ids = outcomes.Where(o => o.AdjustmentId is not null).Select(o => o.AdjustmentId!.Value).ToList();
        var rows = await db.Set<LaborCostAdjustment>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        var made = (await MapAsync(db, rows, ct)).ToDictionary(d => d.Id!.Value);
        var sheetIds = outcomes.Select(o => o.TimesheetId).ToList();
        var weeks = await db.Set<Timesheet>().AsNoTracking().Where(t => sheetIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PeriodStart, ct);
        return outcomes.Select(o => o.AdjustmentId is { } id && made.TryGetValue(id, out var dto)
                ? dto
                : new LaborAdjustmentDto(null, o.TimesheetId, weeks.GetValueOrDefault(o.TimesheetId), default, 0, "", [], [], false, null, null, o.Problem))
            .OrderBy(d => d.WeekOf).ToList();
    }

    private static async Task<IReadOnlyList<LaborAdjustmentDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<LaborCostAdjustment> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var sheetIds = rows.Select(r => r.TimesheetId).Distinct().ToList();
        var weeks = await db.Set<Timesheet>().AsNoTracking().Where(t => sheetIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.PeriodStart, ct);
        var entries = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.Source == JournalSource.LaborAdjustment && e.SourceId != null && ids.Contains(e.SourceId.Value))
            .Select(e => new { SourceId = e.SourceId!.Value, e.Number, e.FiscalPeriodId, e.Status, e.Sequence }).ToListAsync(ct);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name, ct);
        return rows.Select(r =>
        {
            var mine = entries.Where(e => e.SourceId == r.Id).OrderBy(e => e.Sequence).ToList();
            return new LaborAdjustmentDto(r.Id, r.TimesheetId, weeks.GetValueOrDefault(r.TimesheetId), r.RateEffectiveFrom, r.Amount, r.WorkPeriods,
                mine.Select(e => periods.GetValueOrDefault(e.FiscalPeriodId) ?? "").ToList(),
                mine.Select(e => e.Status == JournalStatus.Reversed ? $"{e.Number} (reversed)" : e.Number).ToList(),
                r.Redirected, r.Reason, r.CreatedAtUtc);
        }).ToList();
    }
}

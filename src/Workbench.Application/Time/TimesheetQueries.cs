using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>Loading, mapping and access rules shared by the timekeeping services.</summary>
internal static class TimesheetQueries
{
    public const string AuditEntityType = nameof(Timesheet);

    public static async Task<Timesheet> LoadAsync(IWorkbenchDbContext db, Guid timesheetId, CancellationToken cancellationToken) =>
        await db.Set<Timesheet>().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Timesheet", timesheetId);

    public static async Task<Employee?> FindByEmailAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email == normalized, cancellationToken);
    }

    /// <summary>
    /// DCAA: timesheets are approved by the employee's supervisor, or by an admin when the employee has no supervisor.
    /// Nobody approves their own timesheet.
    /// </summary>
    public static async Task<bool> CanApproveAsync(IWorkbenchDbContext db, Employee owner, string actorEmail, ITimekeepingRoles roles, CancellationToken cancellationToken)
    {
        if (string.Equals(owner.Email, actorEmail, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (owner.SupervisorId is { } supervisorId)
        {
            var supervisor = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == supervisorId, cancellationToken);
            return supervisor is not null && string.Equals(supervisor.Email, actorEmail, StringComparison.OrdinalIgnoreCase);
        }

        return roles.IsAdmin(actorEmail);
    }

    public static decimal Total(this Timesheet sheet) => sheet.Entries.Sum(e => e.Hours);

    public static TimesheetDto ToDto(this Timesheet t, Employee employee) =>
        new(t.Id, t.EmployeeId, employee.DisplayName, employee.Email, t.PeriodStart, t.PeriodEnd, (TimesheetState)(int)t.Status, t.Revision,
            t.Total(), TimekeepingClock.StandardHours(employee, t.PeriodStart, t.PeriodEnd), t.CertifiedAtUtc, t.SubmittedAtUtc,
            t.FirstSubmittedAtUtc, t.ReviewedBy, t.ApprovedAtUtc, t.RejectedAtUtc, t.RejectionReason, t.UpdatedAtUtc);

    /// <summary>A compact snapshot for audit before/after JSON.</summary>
    public static object Snapshot(this Timesheet t) => new
    {
        Status = t.Status.ToString(),
        t.Revision,
        TotalHours = t.Total(),
        t.CertifiedAtUtc,
        t.ReviewedBy,
        t.RejectionReason,
        Entries = t.Entries.OrderBy(e => e.WorkDate).Select(e => new { e.Id, e.WbsElementId, e.LaborCategoryId, e.WorkDate, e.Hours }),
    };

    public static async Task<TimesheetDetailDto> BuildDetailAsync(
        IWorkbenchDbContext db,
        Timesheet sheet,
        Employee employee,
        TimekeepingClock clock,
        IProjectService? projects,
        CancellationToken cancellationToken)
    {
        var revisions = await db.Set<TimeEntryRevision>().AsNoTracking().Where(r => r.TimesheetId == sheet.Id).ToListAsync(cancellationToken);
        var wbsIds = sheet.Entries.Select(e => e.WbsElementId)
            .Concat(revisions.SelectMany(r => new[] { r.OldWbsElementId, r.NewWbsElementId }).OfType<Guid>())
            .ToHashSet();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var projectIds = wbs.Values.Select(w => w.ProjectId).ToHashSet();
        var projectCodes = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(l => l.Id, l => l.Code, cancellationToken);

        var entries = sheet.Entries
            .OrderBy(e => wbs.TryGetValue(e.WbsElementId, out var w) ? w.Code : "").ThenBy(e => e.WorkDate)
            .Select(e =>
            {
                var w = wbs[e.WbsElementId];
                return new TimeEntryDto(e.Id, e.WbsElementId, w.Code, w.Name, projectCodes.GetValueOrDefault(w.ProjectId, ""), e.LaborCategoryId,
                    categories.GetValueOrDefault(e.LaborCategoryId, ""), e.WorkDate, e.Hours, e.Comment, e.EnteredAtUtc, e.UpdatedAtUtc);
            })
            .ToList();

        string? Code(Guid? id) => id is { } v && wbs.TryGetValue(v, out var w) ? w.Code : null;
        string? Category(Guid? id) => id is { } v ? categories.GetValueOrDefault(v) : null;
        var revisionDtos = revisions.OrderBy(r => r.ChangedAtUtc)
            .Select(r => new TimeEntryRevisionDto(r.Id, r.TimeEntryId, r.WorkDate, r.TimesheetRevision, r.OldHours, r.NewHours,
                Code(r.OldWbsElementId), Code(r.NewWbsElementId), Category(r.OldLaborCategoryId), Category(r.NewLaborCategoryId),
                r.ChangedBy, r.ChangedAtUtc, r.Reason))
            .ToList();

        var openPeriods = await OpenPeriodsAsync(db, sheet.PeriodStart, sheet.PeriodEnd, cancellationToken);
        var dayRecords = await db.Set<TimeDayRecord>().AsNoTracking()
            .Where(d => d.EmployeeId == employee.Id && d.WorkDate >= sheet.PeriodStart && d.WorkDate <= sheet.PeriodEnd)
            .ToDictionaryAsync(d => d.WorkDate, cancellationToken);
        var today = clock.Today;
        var days = new List<TimesheetDayDto>();
        for (var d = sheet.PeriodStart; d <= sheet.PeriodEnd; d = d.AddDays(1))
        {
            var first = dayRecords.TryGetValue(d, out var record) ? record.FirstEnteredAtUtc : (DateTime?)null;
            days.Add(new TimesheetDayDto(d, d > today, openPeriods(d), employee.IsActiveOn(d), first, first is { } f && clock.IsLate(d, f)));
        }

        var chargeCodes = projects is null ? [] : await ChargeCodesAsync(db, employee, days, projects, cancellationToken);
        var audit = await db.Set<AuditEvent>().AsNoTracking()
            .Where(a => a.EntityType == AuditEntityType && a.EntityId == sheet.Id)
            .ToListAsync(cancellationToken);
        var auditDtos = audit.OrderBy(a => a.OccurredAtUtc)
            .Select(a => new AuditEventDto(a.Id, a.EntityType, a.EntityId, a.Action, a.BeforeJson, a.AfterJson, a.Actor, a.OccurredAtUtc, a.Reason))
            .ToList();

        return new TimesheetDetailDto(sheet.ToDto(employee), entries, revisionDtos, days, chargeCodes, auditDtos);
    }

    /// <summary>A predicate telling whether a date lies inside an open fiscal period.</summary>
    public static async Task<Func<DateOnly, bool>> OpenPeriodsAsync(IWorkbenchDbContext db, DateOnly start, DateOnly end, CancellationToken cancellationToken)
    {
        var periods = await db.Set<FiscalPeriod>().AsNoTracking()
            .Where(p => p.Start <= end && p.End >= start && p.Status == PeriodStatus.Open)
            .ToListAsync(cancellationToken);
        return date => periods.Any(p => p.Start <= date && p.End >= date);
    }

    private static async Task<IReadOnlyList<TimesheetChargeCodeDto>> ChargeCodesAsync(
        IWorkbenchDbContext db, Employee employee, IReadOnlyList<TimesheetDayDto> days, IProjectService projects, CancellationToken cancellationToken)
    {
        var byCode = new Dictionary<Guid, (ChargeCodeDto Code, List<DateOnly> Dates)>();
        foreach (var day in days.Where(d => d.IsEmployed))
        {
            foreach (var code in await projects.ListChargeCodesAsync(employee.Id, day.Date, cancellationToken))
            {
                if (!byCode.TryGetValue(code.WbsElementId, out var existing))
                {
                    byCode[code.WbsElementId] = existing = (code, []);
                }

                existing.Dates.Add(day.Date);
            }
        }

        // "Recently used" = charged in the last eight weeks, so the grid can offer them first.
        var since = days[0].Date.AddDays(-56);
        var recent = await db.Set<TimeEntry>().AsNoTracking()
            .Where(e => e.WorkDate >= since && db.Set<Timesheet>().Any(t => t.Id == e.TimesheetId && t.EmployeeId == employee.Id))
            .Select(e => e.WbsElementId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var recentSet = recent.ToHashSet();

        return byCode.Values
            .Select(v => new TimesheetChargeCodeDto(v.Code, v.Dates, recentSet.Contains(v.Code.WbsElementId)))
            .OrderBy(c => c.ChargeCode.Kind).ThenBy(c => c.ChargeCode.Code, StringComparer.Ordinal)
            .ToList();
    }
}

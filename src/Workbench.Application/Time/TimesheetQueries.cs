using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>Loading, mapping and access rules shared by the timekeeping services.</summary>
internal static class TimesheetQueries
{
    public const string AuditEntityType = nameof(Timesheet);

    public static async Task<Timesheet> LoadAsync(IWorkbenchDbContext db, Guid timesheetId, CancellationToken cancellationToken) =>
        await db.Set<Timesheet>().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Timesheet", timesheetId);

    /// <summary>The employee for a sign-in email, honoring an administrator's explicit link (see <see cref="Identity.EmployeeLinks"/>).</summary>
    public static Task<Employee?> FindByEmailAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken) =>
        Identity.EmployeeLinks.FindForUserAsync(db, email, cancellationToken);

    /// <summary>
    /// DCAA: timesheets are approved by the employee's supervisor, or by an Admin when the employee has no supervisor.
    /// Nobody approves their own timesheet. Timekeeper and Finance can view timesheets but never approve them as such.
    /// </summary>
    public static async Task<bool> CanApproveAsync(IWorkbenchDbContext db, Employee owner, string actorEmail, PermissionSet actorPermissions, CancellationToken cancellationToken)
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

        return actorPermissions.Has(Permission.ApprovalsActAsAdmin);
    }

    /// <summary>
    /// Who approves the employee's timesheets: their supervisor, or everyone who approves as a last resort (administrators) when they have none.
    /// The employee is never their own approver.
    /// </summary>
    public static async Task<IReadOnlyList<string>> ApproverEmailsAsync(IWorkbenchDbContext db, Employee owner, CancellationToken cancellationToken)
    {
        var emails = owner.SupervisorId is { } supervisorId
            ? await db.Set<Employee>().AsNoTracking().Where(e => e.Id == supervisorId).Select(e => e.Email).ToListAsync(cancellationToken)
            : await Identity.RoleStore.EmailsWithPermissionAsync(db, Permission.ApprovalsActAsAdmin, cancellationToken);
        return emails.Where(e => !string.Equals(e, owner.Email, StringComparison.OrdinalIgnoreCase)).ToList();
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

        return new TimesheetDetailDto(sheet.ToDto(employee), entries, revisionDtos, days, chargeCodes, auditDtos,
            await NotOpenYetAsync(db, employee, sheet.PeriodEnd, chargeCodes, cancellationToken),
            await CeilingRoomAsync(db, chargeCodes.Select(c => c.ChargeCode.WbsElementId).Concat(entries.Select(e => e.WbsElementId)).Distinct().ToList(), clock.UtcNow, cancellationToken));
    }

    /// <summary>
    /// The room left under an hours ceiling for each charge code that has one (the tightest, with any override): what the hours ceilings
    /// consume already counts every entry, drafts included, so this is exactly how many more hours can be saved and submitted.
    /// </summary>
    private static async Task<IReadOnlyList<ChargeCeilingRoomDto>> CeilingRoomAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> wbsIds, DateTime utcNow, CancellationToken ct)
    {
        if (wbsIds.Count == 0)
        {
            return [];
        }

        var states = await Ceilings.CeilingEngine.ForWbsAsync(db, wbsIds, utcNow, ct);
        var room = new List<ChargeCeilingRoomDto>();
        foreach (var wbs in wbsIds)
        {
            var left = states.Where(s => s.Wbs.Contains(wbs) && s.Limit(Domain.Ceilings.CeilingMeasure.Hours) is not null && s.EffectiveLimit(Domain.Ceilings.CeilingMeasure.Hours) is not null)
                .Select(s => s.EffectiveLimit(Domain.Ceilings.CeilingMeasure.Hours)!.Value - s.Consumed(Domain.Ceilings.CeilingMeasure.Hours))
                .DefaultIfEmpty(decimal.MaxValue).Min();
            if (left != decimal.MaxValue)
            {
                room.Add(new ChargeCeilingRoomDto(wbs, Math.Max(0, left)));
            }
        }

        return room;
    }

    /// <summary>
    /// The codes someone is staffed or assigned on that aren't in the picker, with why: the project is still in Planning (its plan
    /// isn't approved), the project starts later, or their own dates start later. Otherwise a new project's code is simply
    /// missing, with nothing to say where it went.
    /// </summary>
    private static async Task<IReadOnlyList<UpcomingChargeCodeDto>> NotOpenYetAsync(
        IWorkbenchDbContext db, Employee employee, DateOnly periodEnd, IReadOnlyList<TimesheetChargeCodeDto> chargeCodes, CancellationToken cancellationToken)
    {
        var have = chargeCodes.Select(c => c.ChargeCode.WbsElementId).ToHashSet();
        var staffed = await db.Set<Domain.Resourcing.StaffingAssignment>().AsNoTracking()
            .Where(a => a.EmployeeId == employee.Id && a.WbsElementId != null && a.To >= periodEnd.AddDays(-6))
            .Select(a => new { Wbs = a.WbsElementId!.Value, a.From })
            .ToListAsync(cancellationToken);
        var assigned = await db.Set<ChargeAssignment>().AsNoTracking()
            .Where(a => a.EmployeeId == employee.Id && (a.To == null || a.To >= periodEnd.AddDays(-6)))
            .Select(a => new { Wbs = a.WbsElementId, From = a.From ?? DateOnly.MinValue })
            .ToListAsync(cancellationToken);
        var starts = staffed.Concat(assigned).GroupBy(x => x.Wbs).ToDictionary(g => g.Key, g => g.Min(x => x.From));
        var ids = starts.Keys.Where(id => !have.Contains(id)).ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await (
                from w in db.Set<WbsElement>().AsNoTracking()
                join p in db.Set<Project>().AsNoTracking() on w.ProjectId equals p.Id
                where ids.Contains(w.Id) && w.IsOpen && w.IsChargeable && p.Status != ProjectStatus.Closed
                select new { w.Id, w.Code, w.Name, ProjectCode = p.Code, p.Status, p.Start })
            .ToListAsync(cancellationToken);
        var result = new List<UpcomingChargeCodeDto>();
        foreach (var r in rows.OrderBy(r => r.Code, StringComparer.Ordinal))
        {
            var from = starts[r.Id];
            var reason = r.Status == ProjectStatus.Planning ? $"{r.ProjectCode} opens when Finance approves its plan."
                : r.Start is { } start && start > periodEnd ? $"{r.ProjectCode} starts {start:MMM d}."
                : from > periodEnd ? $"You're on it from {from:MMM d}."
                : null;
            if (reason is not null)
            {
                result.Add(new UpcomingChargeCodeDto(r.Id, r.Code, r.Name, r.ProjectCode, reason));
            }
        }

        return result.Take(6).ToList();
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
        var employed = days.Where(d => d.IsEmployed).Select(d => d.Date).ToList();
        var codesByDate = employed.Count == 0 ? new Dictionary<DateOnly, IReadOnlyList<ChargeCodeDto>>() : await projects.ListChargeCodesForDatesAsync(employee.Id, employed, cancellationToken);
        foreach (var day in days.Where(d => d.IsEmployed))
        {
            foreach (var code in codesByDate[day.Date])
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
        // From the employee's timesheets (indexed by employee and period) rather than everyone's entries since then.
        var recent = await (
                from t in db.Set<Timesheet>().AsNoTracking()
                join e in db.Set<TimeEntry>().AsNoTracking() on t.Id equals e.TimesheetId
                where t.EmployeeId == employee.Id && t.PeriodEnd >= since && e.WorkDate >= since
                select e.WbsElementId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var recentSet = recent.ToHashSet();

        return byCode.Values
            .Select(v => new TimesheetChargeCodeDto(v.Code, v.Dates, recentSet.Contains(v.Code.WbsElementId)))
            .OrderBy(c => c.ChargeCode.Kind).ThenBy(c => c.ChargeCode.Code, StringComparer.Ordinal)
            .ToList();
    }
}

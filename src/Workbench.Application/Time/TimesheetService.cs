using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// The employee's own timesheets, enforcing DCAA timekeeping expectations: employees record their own time daily,
/// only to authorized charge codes, account for all hours, certify on submit, and every change after submission
/// leaves an audit trail with a reason.
/// </summary>
public sealed class TimesheetService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    AuditRecorder audit,
    IProjectService projects,
    TimekeepingClock clock,
    AccessControl access,
    ITimesheetNotifier notifier,
    ApprovalEngine? approvals = null) : ITimesheetService
{
    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock.Provider);

    public const decimal MaxHoursPerDay = 24m;

    public async Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);

        // A timesheet keeps the period it was opened with, even if the organization's cadence changed since.
        var sheet = await db.Set<Timesheet>().Include(t => t.Entries)
            .FirstOrDefaultAsync(t => t.EmployeeId == me.Id && t.PeriodStart <= date && t.PeriodEnd >= date, cancellationToken);
        if (sheet is null)
        {
            var (start, end) = await clock.PeriodForAsync(db, date, cancellationToken);

            // DCAA: no time for future dates, so there's no reason to open a timesheet for a period that hasn't started.
            if (start > clock.Today)
            {
                throw new ArgumentException($"The timesheet period starting {start:yyyy-MM-dd} hasn't started yet.");
            }

            var now = clock.UtcNow;
            sheet = new Timesheet { EmployeeId = me.Id, PeriodStart = start, PeriodEnd = end, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Set<Timesheet>().Add(sheet);
            await db.SaveChangesAsync(cancellationToken);
        }

        return await TimesheetQueries.BuildDetailAsync(db, sheet, me, clock, projects, cancellationToken);
    }

    public async Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var sheets = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.EmployeeId == me.Id).ToListAsync(cancellationToken);
        return sheets.OrderByDescending(t => t.PeriodStart).Select(t => t.ToDto(me)).ToList();
    }

    public async Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await TimesheetQueries.LoadAsync(db, timesheetId, cancellationToken);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, cancellationToken);
        var isOwner = string.Equals(owner.Email, currentUser.Email, StringComparison.OrdinalIgnoreCase);
        var roles = await access.GetOrgRolesAsync(cancellationToken);
        if (!isOwner && !roles.CanViewAllTimesheets() && !await TimesheetQueries.CanApproveAsync(db, owner, currentUser.Email, roles, cancellationToken)
            && !await _approvals.IsParticipantAsync(db, ApprovalDocumentTypes.Timesheet, sheet.Id, currentUser.Email, cancellationToken))
        {
            throw new UnauthorizedAccessException("Only the employee, their approver, timekeepers, Finance and administrators can view this timesheet.");
        }

        return await TimesheetQueries.BuildDetailAsync(db, sheet, owner, clock, isOwner ? projects : null, cancellationToken);
    }

    public async Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await TimesheetQueries.LoadAsync(db, timesheetId, cancellationToken);
        var me = await OwnerAsync(db, sheet, "enter time on", cancellationToken);

        // DCAA: once submitted the timesheet is locked; approved ones must be reopened with a reason first.
        if (sheet.Status is TimesheetStatus.Submitted or TimesheetStatus.Approved)
        {
            throw new ArgumentException($"This timesheet is {sheet.Status.ToString().ToLowerInvariant()} and locked. Reopen it with a reason to make a correction.");
        }

        if (request.Day is { } day && (day < sheet.PeriodStart || day > sheet.PeriodEnd))
        {
            throw new ArgumentException($"{day:yyyy-MM-dd} isn't in this timesheet period.");
        }

        var inScope = sheet.Entries.Where(e => request.Day is null || e.WorkDate == request.Day).ToList();
        var unmatched = inScope.ToList();
        var pairs = new List<(TimeEntry? Existing, TimeEntryInput Input)>();
        foreach (var input in request.Entries ?? [])
        {
            ValidateInput(sheet, request.Day, input);
            TimeEntry? existing;
            if (input.EntryId is { } entryId)
            {
                existing = unmatched.FirstOrDefault(e => e.Id == entryId) ?? throw WorkbenchNotFoundException.For("Time entry", entryId);
                if (existing.WorkDate != input.WorkDate)
                {
                    throw new ArgumentException("An entry can't move to another date; remove it and add a new one.");
                }
            }
            else
            {
                existing = unmatched.FirstOrDefault(e => e.WbsElementId == input.WbsElementId && e.WorkDate == input.WorkDate);
            }

            if (existing is not null)
            {
                unmatched.Remove(existing);
            }

            if (existing is null && input.Hours == 0)
            {
                continue;
            }

            pairs.Add((existing, input));
        }

        if (pairs.Where(p => p.Input.Hours > 0).GroupBy(p => (p.Input.WbsElementId, p.Input.WorkDate)).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("Each charge code can appear only once per day.");
        }

        // Resolve labor categories and classify changes.
        var codeCache = new Dictionary<DateOnly, IReadOnlyList<ChargeCodeDto>>();
        async Task<IReadOnlyList<ChargeCodeDto>> CodesOn(DateOnly date) =>
            codeCache.TryGetValue(date, out var c) ? c : codeCache[date] = await projects.ListChargeCodesAsync(me.Id, date, cancellationToken);

        var additions = new List<(TimeEntryInput Input, Guid LaborCategoryId)>();
        var changes = new List<(TimeEntry Entry, TimeEntryInput Input, Guid LaborCategoryId)>();
        var commentOnly = new List<(TimeEntry Entry, string? Comment)>();
        var deletions = unmatched.ToList();
        foreach (var (existing, input) in pairs)
        {
            if (existing is not null && input.Hours == 0)
            {
                deletions.Add(existing);
                continue;
            }

            var codes = await CodesOn(input.WorkDate);
            var code = codes.FirstOrDefault(c => c.WbsElementId == input.WbsElementId);
            var laborCategoryId = input.LaborCategoryId ?? (existing?.WbsElementId == input.WbsElementId ? existing.LaborCategoryId : code?.LaborCategoryId);
            var changed = existing is null || existing.Hours != input.Hours || existing.WbsElementId != input.WbsElementId || existing.LaborCategoryId != laborCategoryId;
            if (changed)
            {
                // DCAA: time may only be charged to codes the employee is authorized for on that date
                // (IProjectService.ListChargeCodesAsync is the single source of truth).
                if (code is null)
                {
                    throw new ArgumentException($"You can't charge that code on {input.WorkDate:yyyy-MM-dd}: it isn't one of your open charge codes for that date.");
                }

                if (laborCategoryId is not { } lcat || !await db.Set<LaborCategory>().AnyAsync(l => l.Id == lcat && l.IsActive, cancellationToken))
                {
                    throw new ArgumentException("The labor category isn't active.");
                }

                if (existing is null)
                {
                    additions.Add((input, lcat));
                }
                else
                {
                    changes.Add((existing, input, lcat));
                }
            }
            else if (existing!.Comment != Normalize(input.Comment))
            {
                commentOnly.Add((existing, Normalize(input.Comment)));
            }
        }

        // Every date whose hours change must be valid: not in the future, inside employment and an open fiscal period.
        var isOpen = await TimesheetQueries.OpenPeriodsAsync(db, sheet.PeriodStart, sheet.PeriodEnd, cancellationToken);
        var touchedDates = additions.Select(a => a.Input.WorkDate)
            .Concat(changes.Select(c => c.Input.WorkDate))
            .Concat(deletions.Select(d => d.WorkDate))
            .ToHashSet();
        foreach (var date in touchedDates)
        {
            ValidateDate(me, date, isOpen, checkEmployment: additions.Any(a => a.Input.WorkDate == date) || changes.Any(c => c.Input.WorkDate == date));
        }

        // DCAA: at most 24 hours in a day, across every charge code.
        var finalHours = sheet.Entries.Where(e => !deletions.Contains(e))
            .Select(e => (e.WorkDate, Hours: changes.FirstOrDefault(c => c.Entry == e) is { Entry: not null } c ? c.Input.Hours : e.Hours))
            .Concat(additions.Select(a => (a.Input.WorkDate, a.Input.Hours)));
        if (finalHours.GroupBy(h => h.WorkDate).FirstOrDefault(g => g.Sum(h => h.Hours) > MaxHoursPerDay) is { } overDay)
        {
            throw new ArgumentException($"{overDay.Key:ddd yyyy-MM-dd} would have {overDay.Sum(h => h.Hours):0.##} hours; a day can't exceed {MaxHoursPerDay:0} hours.");
        }

        // DCAA: after the first submission, no change is applied silently. Each one writes a revision with a reason.
        var tracked = sheet.FirstSubmittedAtUtc is not null;
        var reason = request.Reason?.Trim();
        if (tracked && additions.Count + changes.Count + deletions.Count > 0 && string.IsNullOrEmpty(reason))
        {
            throw new ArgumentException("This timesheet was already submitted, so a reason is required for every change.");
        }

        var now = clock.UtcNow;
        void Revise(TimeEntry? before, Guid entryId, DateOnly date, decimal? newHours, Guid? newWbs, Guid? newCategory)
        {
            if (!tracked)
            {
                return;
            }

            db.Set<TimeEntryRevision>().Add(new TimeEntryRevision
            {
                TimeEntryId = entryId,
                TimesheetId = sheet.Id,
                WorkDate = date,
                TimesheetRevision = sheet.Revision,
                OldHours = before?.Hours,
                NewHours = newHours,
                OldWbsElementId = before?.WbsElementId,
                NewWbsElementId = newWbs,
                OldLaborCategoryId = before?.LaborCategoryId,
                NewLaborCategoryId = newCategory,
                ChangedBy = currentUser.Email,
                ChangedAtUtc = now,
                Reason = reason!,
            });
        }

        foreach (var entry in deletions)
        {
            Revise(entry, entry.Id, entry.WorkDate, null, null, null);
            sheet.Entries.Remove(entry);
            db.Set<TimeEntry>().Remove(entry);
        }

        foreach (var (entry, input, lcat) in changes)
        {
            var before = new TimeEntry { Hours = entry.Hours, WbsElementId = entry.WbsElementId, LaborCategoryId = entry.LaborCategoryId };
            entry.Hours = input.Hours;
            entry.WbsElementId = input.WbsElementId;
            entry.LaborCategoryId = lcat;
            entry.Comment = Normalize(input.Comment);
            entry.UpdatedAtUtc = now;
            Revise(before, entry.Id, entry.WorkDate, entry.Hours, entry.WbsElementId, entry.LaborCategoryId);
        }

        foreach (var (input, lcat) in additions)
        {
            var entry = new TimeEntry
            {
                TimesheetId = sheet.Id,
                WbsElementId = input.WbsElementId,
                LaborCategoryId = lcat,
                WorkDate = input.WorkDate,
                Hours = input.Hours,
                Comment = Normalize(input.Comment),
                EnteredAtUtc = now,
                UpdatedAtUtc = now,
            };
            sheet.Entries.Add(entry);
            db.Set<TimeEntry>().Add(entry);
            Revise(null, entry.Id, entry.WorkDate, entry.Hours, entry.WbsElementId, entry.LaborCategoryId);
        }

        foreach (var (entry, comment) in commentOnly)
        {
            entry.Comment = comment;
            entry.UpdatedAtUtc = now;
        }

        // Daily-entry compliance: remember when each day's hours were first recorded, even if later changed.
        var newDays = additions.Select(a => a.Input.WorkDate).Distinct().ToList();
        if (newDays.Count > 0)
        {
            var known = await db.Set<TimeDayRecord>().Where(d => d.EmployeeId == me.Id && newDays.Contains(d.WorkDate)).Select(d => d.WorkDate).ToListAsync(cancellationToken);
            db.Set<TimeDayRecord>().AddRange(newDays.Except(known).Select(d => new TimeDayRecord { EmployeeId = me.Id, TimesheetId = sheet.Id, WorkDate = d, FirstEnteredAtUtc = now }));
        }

        sheet.UpdatedAtUtc = now;
        await db.SaveChangesAsync(cancellationToken);
        return await TimesheetQueries.BuildDetailAsync(db, sheet, me, clock, projects, cancellationToken);
    }

    public async Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await TimesheetQueries.LoadAsync(db, timesheetId, cancellationToken);
        var me = await OwnerAsync(db, sheet, "submit", cancellationToken);
        if (sheet.Status is not (TimesheetStatus.Draft or TimesheetStatus.Rejected))
        {
            throw new ArgumentException($"This timesheet is already {sheet.Status.ToString().ToLowerInvariant()}.");
        }

        // DCAA: the employee certifies the timesheet is complete and accurate.
        if (request is not { Certify: true })
        {
            throw new ArgumentException("You must certify the timesheet to submit it.");
        }

        // Re-check every entry in case a period closed or a charge code ended since it was entered.
        var isOpen = await TimesheetQueries.OpenPeriodsAsync(db, sheet.PeriodStart, sheet.PeriodEnd, cancellationToken);
        foreach (var group in sheet.Entries.GroupBy(e => e.WorkDate))
        {
            ValidateDate(me, group.Key, isOpen, checkEmployment: true);
            var codes = await projects.ListChargeCodesAsync(me.Id, group.Key, cancellationToken);
            if (group.FirstOrDefault(e => codes.All(c => c.WbsElementId != e.WbsElementId)) is { } invalid)
            {
                throw new ArgumentException($"An entry on {invalid.WorkDate:yyyy-MM-dd} uses a charge code you can no longer charge. Move those hours first.");
            }
        }

        var before = sheet.Snapshot();
        var now = clock.UtcNow;
        sheet.Status = TimesheetStatus.Submitted;
        sheet.CertifiedAtUtc = now;
        sheet.CertificationText = TimesheetCertification.Text;
        sheet.SubmittedAtUtc = now;
        sheet.FirstSubmittedAtUtc ??= now;
        sheet.RejectionReason = null;
        sheet.RejectedAtUtc = null;
        sheet.ReviewedBy = null;
        sheet.UpdatedAtUtc = now;

        // DCAA total time accounting: all hours worked, including indirect and leave. Short timesheets warn but submit.
        var warnings = new List<string>();
        var standard = TimekeepingClock.StandardHours(me, sheet.PeriodStart, sheet.PeriodEnd);
        if (sheet.Total() < standard)
        {
            warnings.Add($"This timesheet has {sheet.Total():0.##} hours, below your standard {standard:0.##} for the period. "
                + "Make sure every hour worked, including indirect and leave time, is recorded.");
        }

        audit.Record(db, TimesheetQueries.AuditEntityType, sheet.Id, "Submitted", before, sheet.Snapshot(), warnings.Count > 0 ? string.Join(" ", warnings) : null);

        // The workflow (Setup › Approval workflows; by default the supervisor, or the administrators without one) routes it.
        var route = await _approvals.StartAsync(db, await ApprovalSubjects.TimesheetAsync(db, sheet, cancellationToken), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);

        var approvers = route.NotifyEmails;
        await notifier.NotifyAsync(new TimesheetNotification(TimesheetEvent.Submitted, sheet.Id, me.DisplayName, me.Email, approvers,
            sheet.PeriodStart, sheet.Total(), me.DisplayName, null), cancellationToken);
        return new SubmitTimesheetResult(await TimesheetQueries.BuildDetailAsync(db, sheet, me, clock, projects, cancellationToken), warnings);
    }

    public async Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default)
    {
        var why = reason.Required("Reason");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await TimesheetQueries.LoadAsync(db, timesheetId, cancellationToken);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, cancellationToken);
        var isOwner = string.Equals(owner.Email, currentUser.Email, StringComparison.OrdinalIgnoreCase);
        if (!isOwner && !await TimesheetQueries.CanApproveAsync(db, owner, currentUser.Email, await access.GetOrgRolesAsync(cancellationToken), cancellationToken)
            && !await _approvals.IsParticipantAsync(db, ApprovalDocumentTypes.Timesheet, sheet.Id, currentUser.Email, cancellationToken))
        {
            throw new UnauthorizedAccessException("Only the employee or their approver can reopen this timesheet.");
        }

        if (sheet.Status is not (TimesheetStatus.Submitted or TimesheetStatus.Approved))
        {
            throw new ArgumentException("Only submitted or approved timesheets can be reopened.");
        }

        var before = sheet.Snapshot();
        var wasApproved = sheet.Status == TimesheetStatus.Approved;

        // DCAA: a correction to an approved timesheet is a new revision that must be certified and approved again.
        if (wasApproved)
        {
            sheet.Revision++;
        }

        sheet.Status = TimesheetStatus.Draft;
        sheet.CertifiedAtUtc = null;
        sheet.ApprovedAtUtc = null;
        sheet.ApprovedById = null;
        sheet.ReviewedBy = null;
        sheet.UpdatedAtUtc = clock.UtcNow;
        audit.Record(db, TimesheetQueries.AuditEntityType, sheet.Id, wasApproved ? "Reopened" : "Recalled", before, sheet.Snapshot(), why);
        await _approvals.CancelAsync(db, ApprovalDocumentTypes.Timesheet, sheet.Id, currentUser.Email, why, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        // The employee and their approvers hear about it, except whoever reopened it.
        var recipients = (await TimesheetQueries.ApproverEmailsAsync(db, owner, cancellationToken)).Prepend(owner.Email)
            .Where(e => !string.Equals(e, currentUser.Email, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var actor = isOwner ? owner : await TimesheetQueries.FindByEmailAsync(db, currentUser.Email, cancellationToken);
        await notifier.NotifyAsync(new TimesheetNotification(TimesheetEvent.Reopened, sheet.Id, owner.DisplayName, owner.Email, recipients,
            sheet.PeriodStart, sheet.Total(), actor?.DisplayName ?? currentUser.Email, why), cancellationToken);
        return await TimesheetQueries.BuildDetailAsync(db, sheet, owner, clock, isOwner ? projects : null, cancellationToken);
    }

    private async Task<Employee> MeAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        await TimesheetQueries.FindByEmailAsync(db, currentUser.Email, cancellationToken)
            ?? throw new UnauthorizedAccessException("You don't have an employee record, so you can't record time.");

    /// <summary>DCAA: employees record and submit their own time. Supervisors approve or reject but never edit hours.</summary>
    private async Task<Employee> OwnerAsync(IWorkbenchDbContext db, Timesheet sheet, string action, CancellationToken cancellationToken)
    {
        var me = await TimesheetQueries.FindByEmailAsync(db, currentUser.Email, cancellationToken);
        if (me is null || me.Id != sheet.EmployeeId)
        {
            throw new UnauthorizedAccessException($"Only the employee can {action} their own timesheet. Supervisors can approve or reject it but not change hours.");
        }

        return me;
    }

    private static void ValidateInput(Timesheet sheet, DateOnly? day, TimeEntryInput input)
    {
        if (input.WorkDate < sheet.PeriodStart || input.WorkDate > sheet.PeriodEnd)
        {
            throw new ArgumentException($"{input.WorkDate:yyyy-MM-dd} isn't in this timesheet period.");
        }

        if (day is { } d && input.WorkDate != d)
        {
            throw new ArgumentException($"Only entries for {d:yyyy-MM-dd} can be saved with this request.");
        }

        if (input.Hours < 0 || input.Hours > MaxHoursPerDay)
        {
            throw new ArgumentException($"Hours must be between 0 and {MaxHoursPerDay:0}.");
        }

        if (decimal.Round(input.Hours, 2) != input.Hours)
        {
            throw new ArgumentException("Hours can have at most two decimal places.");
        }

        if (input.Comment?.Length > 1000)
        {
            throw new ArgumentException("Comments can be at most 1000 characters.");
        }
    }

    private void ValidateDate(Employee employee, DateOnly date, Func<DateOnly, bool> isOpenPeriod, bool checkEmployment)
    {
        // DCAA: time is recorded as it is worked, never in advance.
        if (date > clock.Today)
        {
            throw new ArgumentException($"You can't record time for {date:yyyy-MM-dd} because it's in the future.");
        }

        // Closed accounting periods reject new time and changes.
        if (!isOpenPeriod(date))
        {
            throw new ArgumentException($"{date:yyyy-MM-dd} isn't in an open fiscal period.");
        }

        if (checkEmployment && !employee.IsActiveOn(date))
        {
            throw new ArgumentException($"{date:yyyy-MM-dd} is outside your employment dates.");
        }
    }

    private static string? Normalize(string? comment) => string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
}

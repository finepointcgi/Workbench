using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// Timesheet approval. DCAA expects an independent review: whoever the timesheet workflow routes it to (by default the
/// employee's supervisor, or an Admin when there is none; see Setup › Approval workflows) approves or rejects with a
/// reason, never edits the hours, and never approves their own timesheet. Timekeeper, Finance and Admin see every
/// employee's team status; approving still follows the workflow.
/// </summary>
public sealed class TimesheetApprovalService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    AuditRecorder audit,
    TimekeepingClock clock,
    AccessControl access,
    ITimesheetNotifier notifier,
    ApprovalEngine? approvals = null) : ITimesheetApprovalService
{
    private readonly ApprovalEngine _approvals = approvals ?? new ApprovalEngine(clock.Provider);

    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ReportsAsync(db, cancellationToken)).Count > 0
            || (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.Timesheet, currentUser.Email, cancellationToken)).Count > 0;
    }

    public async Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = (await _approvals.ActionableAsync(db, ApprovalDocumentTypes.Timesheet, currentUser.Email, cancellationToken))
            .Select(x => x.Instance.DocumentId).ToHashSet();

        // Timesheets submitted before workflows existed have no trip yet: route them now, in memory, to see if they're mine.
        var routed = await _approvals.RoutedDocumentIdsAsync(db, ApprovalDocumentTypes.Timesheet, cancellationToken);
        foreach (var legacy in await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.Status == TimesheetStatus.Submitted && !routed.Contains(t.Id)).ToListAsync(cancellationToken))
        {
            if (await _approvals.PreviewCanDecideAsync(db, await ApprovalSubjects.TimesheetAsync(db, legacy, cancellationToken), currentUser.Email, cancellationToken))
            {
                ids.Add(legacy.Id);
            }
        }

        var sheets = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.Status == TimesheetStatus.Submitted && ids.Contains(t.Id))
            .ToListAsync(cancellationToken);
        var employeeIds = sheets.Select(t => t.EmployeeId).Distinct().ToList();
        var owners = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        return sheets.OrderBy(t => t.PeriodStart).ThenBy(t => owners[t.EmployeeId].DisplayName)
            .Select(t => t.ToDto(owners[t.EmployeeId])).ToList();
    }

    public Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        ReviewAsync(timesheetId, approve: true, reason: null, cancellationToken);

    public Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        ReviewAsync(timesheetId, approve: false, reason.Required("Reason"), cancellationToken);

    public async Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (start, end) = await clock.PeriodForAsync(db, date, cancellationToken);
        var everyone = (await access.GetOrgRolesAsync(cancellationToken)).CanViewAllTimesheets();
        var team = everyone ? await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken) : await ReportsAsync(db, cancellationToken);
        var reports = team.Where(e => e.HireDate <= end && (e.TerminationDate is null || e.TerminationDate >= start)).ToList();
        var ids = reports.Select(e => e.Id).ToList();
        var sheets = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.PeriodStart == start && ids.Contains(t.EmployeeId))
            .ToDictionaryAsync(t => t.EmployeeId, cancellationToken);
        var ended = end < clock.Today;
        var members = reports.OrderBy(e => e.DisplayName).Select(e =>
        {
            var sheet = sheets.GetValueOrDefault(e.Id);
            var status = sheet is null ? (TimesheetState?)null : (TimesheetState)(int)sheet.Status;
            var missing = ended && status is not (TimesheetState.Submitted or TimesheetState.Approved);
            return new TeamMemberStatusDto(e.Id, e.DisplayName, sheet?.Id, status, sheet?.Total() ?? 0m, TimekeepingClock.StandardHours(e, start, end), missing);
        }).ToList();
        return new TeamStatusDto(start, end, members);
    }

    private async Task<TimesheetDto> ReviewAsync(Guid timesheetId, bool approve, string? reason, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await TimesheetQueries.LoadAsync(db, timesheetId, cancellationToken);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, cancellationToken);
        if (string.Equals(owner.Email, currentUser.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("You can't approve or reject your own timesheet.");
        }

        const string notYours = "Only the employee's supervisor (or an administrator when there's no supervisor) can review this timesheet.";
        if (sheet.Status != TimesheetStatus.Submitted)
        {
            if (!await TimesheetQueries.CanApproveAsync(db, owner, currentUser.Email, await access.GetOrgRolesAsync(cancellationToken), cancellationToken)
                && !await _approvals.IsParticipantAsync(db, ApprovalDocumentTypes.Timesheet, sheet.Id, currentUser.Email, cancellationToken))
            {
                throw new UnauthorizedAccessException(notYours);
            }

            throw new ArgumentException($"Only submitted timesheets can be reviewed; this one is {sheet.Status.ToString().ToLowerInvariant()}.");
        }

        var check = await _approvals.CheckAsync(db, ApprovalDocumentTypes.Timesheet, sheet.Id, currentUser.Email,
            ct => ApprovalSubjects.TimesheetAsync(db, sheet, ct), cancellationToken);
        if (!check.CanDecide)
        {
            throw new UnauthorizedAccessException(notYours);
        }

        var outcome = await _approvals.DecideAsync(db, check, approve ? ApprovalDecisionKind.Approve : ApprovalDecisionKind.Reject, currentUser.Email, reason, cancellationToken);
        if (approve && !outcome.IsComplete)
        {
            // A later step (Setup › Approval workflows) still has to approve: the timesheet stays submitted.
            audit.Record(db, TimesheetQueries.AuditEntityType, sheet.Id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName)) });
            await db.SaveChangesAsync(cancellationToken);
            await notifier.NotifyAsync(new TimesheetNotification(TimesheetEvent.Submitted, sheet.Id, owner.DisplayName, owner.Email, outcome.NotifyEmails,
                sheet.PeriodStart, sheet.Total(), owner.DisplayName, null), cancellationToken);
            return sheet.ToDto(owner);
        }

        var before = sheet.Snapshot();
        var now = clock.UtcNow;
        var reviewer = await TimesheetQueries.FindByEmailAsync(db, currentUser.Email, cancellationToken);
        sheet.ReviewedBy = currentUser.Email;
        sheet.UpdatedAtUtc = now;
        if (approve)
        {
            sheet.Status = TimesheetStatus.Approved;
            sheet.ApprovedAtUtc = now;
            sheet.ApprovedById = reviewer?.Id;
        }
        else
        {
            sheet.Status = TimesheetStatus.Rejected;
            sheet.RejectedAtUtc = now;
            sheet.RejectionReason = reason;
            sheet.CertifiedAtUtc = null;
        }

        audit.Record(db, TimesheetQueries.AuditEntityType, sheet.Id, approve ? "Approved" : "Rejected", before, sheet.Snapshot(), reason);
        await db.SaveChangesAsync(cancellationToken);
        await notifier.NotifyAsync(new TimesheetNotification(approve ? TimesheetEvent.Approved : TimesheetEvent.Rejected, sheet.Id, owner.DisplayName, owner.Email,
            [owner.Email], sheet.PeriodStart, sheet.Total(), reviewer?.DisplayName ?? currentUser.Email, reason), cancellationToken);
        return sheet.ToDto(owner);
    }

    /// <summary>Employees whose timesheets the current user approves: direct reports, plus unsupervised employees for admins.</summary>
    private async Task<List<Employee>> ReportsAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var email = currentUser.Email.Trim().ToLowerInvariant();
        var me = await TimesheetQueries.FindByEmailAsync(db, email, cancellationToken);
        var isAdmin = (await access.GetOrgRolesAsync(cancellationToken)).IsAdmin();
        var myId = me?.Id;
        return await db.Set<Employee>().AsNoTracking()
            .Where(e => e.Email != email && ((myId != null && e.SupervisorId == myId) || (isAdmin && e.SupervisorId == null)))
            .ToListAsync(cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>
/// Supervisor approval. DCAA expects an independent review: the employee's supervisor (or an admin when there is none)
/// approves or rejects with a reason, never edits the hours, and never approves their own timesheet.
/// </summary>
public sealed class TimesheetApprovalService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    AuditRecorder audit,
    TimekeepingClock clock,
    ITimekeepingRoles roles,
    ITimesheetNotifier notifier) : ITimesheetApprovalService
{
    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ReportsAsync(db, cancellationToken)).Count > 0;
    }

    public async Task<IReadOnlyList<TimesheetDto>> ListPendingAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reports = (await ReportsAsync(db, cancellationToken)).ToDictionary(e => e.Id);
        var ids = reports.Keys.ToList();
        var sheets = await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries)
            .Where(t => t.Status == TimesheetStatus.Submitted && ids.Contains(t.EmployeeId))
            .ToListAsync(cancellationToken);
        return sheets.OrderBy(t => t.PeriodStart).ThenBy(t => reports[t.EmployeeId].DisplayName)
            .Select(t => t.ToDto(reports[t.EmployeeId])).ToList();
    }

    public Task<TimesheetDto> ApproveAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        ReviewAsync(timesheetId, approve: true, reason: null, cancellationToken);

    public Task<TimesheetDto> RejectAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) =>
        ReviewAsync(timesheetId, approve: false, reason.Required("Reason"), cancellationToken);

    public async Task<TeamStatusDto> GetTeamStatusAsync(DateOnly date, CancellationToken cancellationToken = default)
    {
        var (start, end) = clock.PeriodFor(date);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reports = (await ReportsAsync(db, cancellationToken)).Where(e => e.HireDate <= end && (e.TerminationDate is null || e.TerminationDate >= start)).ToList();
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

        if (!await TimesheetQueries.CanApproveAsync(db, owner, currentUser.Email, roles, cancellationToken))
        {
            throw new UnauthorizedAccessException("Only the employee's supervisor (or an admin when there's no supervisor) can review this timesheet.");
        }

        if (sheet.Status != TimesheetStatus.Submitted)
        {
            throw new ArgumentException($"Only submitted timesheets can be reviewed; this one is {sheet.Status.ToString().ToLowerInvariant()}.");
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
        await notifier.NotifyAsync(new TimesheetNotification(approve ? TimesheetEvent.Approved : TimesheetEvent.Rejected, sheet.Id, owner.Email, owner.Email,
            sheet.PeriodStart, reason), cancellationToken);
        return sheet.ToDto(owner);
    }

    /// <summary>Employees whose timesheets the current user approves: direct reports, plus unsupervised employees for admins.</summary>
    private async Task<List<Employee>> ReportsAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var email = currentUser.Email.Trim().ToLowerInvariant();
        var me = await TimesheetQueries.FindByEmailAsync(db, email, cancellationToken);
        var isAdmin = roles.IsAdmin(email);
        var myId = me?.Id;
        return await db.Set<Employee>().AsNoTracking()
            .Where(e => e.Email != email && ((myId != null && e.SupervisorId == myId) || (isAdmin && e.SupervisorId == null)))
            .ToListAsync(cancellationToken);
    }
}

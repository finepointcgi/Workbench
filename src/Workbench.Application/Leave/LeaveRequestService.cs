using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Leave;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Leave;

public static class LeaveNotificationKinds
{
    public const string Requested = "leave.requested";
    public const string Approved = "leave.approved";
    public const string Rejected = "leave.rejected";
    public const string Cancelled = "leave.cancelled";
}

/// <summary>
/// Leave requests. The employee requests days; the supervisor approves or rejects (an administrator, for employees
/// without a supervisor, the same rule as timesheets). Approved days pre-fill the employee's timesheet; the leave is
/// only taken once those hours are on an approved timesheet.
/// </summary>
public sealed class LeaveRequestService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    AuditRecorder audit,
    INotificationPublisher notifications,
    TimeProvider clock) : ILeaveRequestService
{
    public const string AuditEntityType = nameof(LeaveRequest);
    public const decimal MaxHoursPerDay = 24m;

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<LeaveRequestDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var requests = await db.Set<LeaveRequest>().AsNoTracking().Include(r => r.Days).Where(r => r.EmployeeId == me.Id).ToListAsync(cancellationToken);
        return await MapAsync(db, requests.OrderByDescending(r => r.StartDate).ToList(), cancellationToken);
    }

    public async Task<LeaveRequestDto> CreateAsync(CreateLeaveRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var policy = await db.Set<LeavePolicy>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PolicyId && p.IsActive, cancellationToken)
            ?? throw new ArgumentException("Choose an active leave type.");
        if (policy.Type == LeaveType.Holiday)
        {
            throw new ArgumentException("Holidays come from the holiday calendar; they aren't requested.");
        }

        if (request.EndDate < request.StartDate)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        if (request.EndDate.DayNumber - request.StartDate.DayNumber > 92)
        {
            throw new ArgumentException("A request can cover at most three months.");
        }

        var perDay = request.HoursPerDay ?? LeaveEngine.StandardDay(me);
        if (perDay <= 0 || perDay > MaxHoursPerDay || decimal.Round(perDay, 2) != perDay)
        {
            throw new ArgumentException($"Hours per day must be more than 0 and at most {MaxHoursPerDay:0}, with up to two decimals.");
        }

        var days = await LeaveEngine.WorkingDaysAsync(db, request.StartDate, request.EndDate, cancellationToken);
        days = days.Where(me.IsActiveOn).ToList();
        if (days.Count == 0)
        {
            throw new ArgumentException("Those dates have no working days (weekends and holidays are skipped).");
        }

        var overlapping = await (
                from d in db.Set<LeaveRequestDay>()
                join r in db.Set<LeaveRequest>() on d.RequestId equals r.Id
                where r.EmployeeId == me.Id && (r.Status == LeaveRequestStatus.Pending || r.Status == LeaveRequestStatus.Approved)
                    && d.Date >= request.StartDate && d.Date <= request.EndDate
                select d.Date)
            .ToListAsync(cancellationToken);
        if (overlapping.Count > 0)
        {
            throw new ArgumentException($"You already requested leave on {overlapping.Min():yyyy-MM-dd}.");
        }

        var hours = perDay * days.Count;
        var warnings = new List<string>();
        var available = await AvailableAsync(db, me.Id, policy.Id, cancellationToken);
        if (hours > available)
        {
            // Pending requests count too: they would take the balance down if approved.
            if (!policy.AllowNegativeBalance)
            {
                throw new ArgumentException($"You have {available:0.##} h of {policy.Name} available (after other requests), less than the {hours:0.##} h requested.");
            }

            warnings.Add($"This request is {hours - available:0.##} h more than your available {policy.Name}; your balance will go negative.");
        }

        var leave = new LeaveRequest
        {
            EmployeeId = me.Id,
            PolicyId = policy.Id,
            StartDate = days.Min(),
            EndDate = days.Max(),
            Hours = hours,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAtUtc = UtcNow,
        };
        if (leave.Comment?.Length > 1000)
        {
            throw new ArgumentException("Comments can be at most 1000 characters.");
        }

        leave.Days.AddRange(days.Select(d => new LeaveRequestDay { RequestId = leave.Id, Date = d, Hours = perDay }));
        db.Set<LeaveRequest>().Add(leave);
        audit.Record(db, AuditEntityType, leave.Id, "Requested", after: Snapshot(leave));
        await db.SaveChangesAsync(cancellationToken);

        var approvers = await TimesheetQueries.ApproverEmailsAsync(db, me, cancellationToken);
        await notifications.PublishAsync(approvers, LeaveNotificationKinds.Requested,
            $"{me.DisplayName} requested {policy.Name}", $"{Range(leave)}: {hours:0.##} h", link: "leave?tab=approvals");
        return (await MapAsync(db, [leave], cancellationToken))[0] with { Warnings = warnings };
    }

    public async Task<LeaveRequestDto> CancelAsync(Guid requestId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await MeAsync(db, cancellationToken);
        var leave = await LoadAsync(db, requestId, cancellationToken);
        if (leave.EmployeeId != me.Id)
        {
            throw new WorkbenchForbiddenException("Only the employee can cancel their own leave request.");
        }

        if (leave.Status is not (LeaveRequestStatus.Pending or LeaveRequestStatus.Approved))
        {
            throw new ArgumentException($"This request is already {leave.Status.ToString().ToLowerInvariant()}.");
        }

        var wasApproved = leave.Status == LeaveRequestStatus.Approved;
        var before = Snapshot(leave);
        leave.Status = LeaveRequestStatus.Cancelled;
        audit.Record(db, AuditEntityType, leave.Id, "Cancelled", before, Snapshot(leave));
        await db.SaveChangesAsync(cancellationToken);
        if (wasApproved)
        {
            var approvers = await TimesheetQueries.ApproverEmailsAsync(db, me, cancellationToken);
            await notifications.PublishAsync(approvers, LeaveNotificationKinds.Cancelled, $"{me.DisplayName} cancelled approved leave", Range(leave), link: "leave?tab=team");
        }

        return (await MapAsync(db, [leave], cancellationToken))[0];
    }

    public async Task<bool> IsApproverAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return false;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await EmployeeLinks.FindForUserAsync(db, access.Email, cancellationToken);
        if (me is not null && await db.Set<Employee>().AnyAsync(e => e.SupervisorId == me.Id, cancellationToken))
        {
            return true;
        }

        return (await access.GetOrgRolesAsync(cancellationToken)).IsAdmin();
    }

    public async Task<IReadOnlyList<LeaveRequestDto>> ListPendingApprovalsAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var roles = await access.GetOrgRolesAsync(cancellationToken);
        var pending = await db.Set<LeaveRequest>().AsNoTracking().Include(r => r.Days).Where(r => r.Status == LeaveRequestStatus.Pending).ToListAsync(cancellationToken);
        var owners = await EmployeesAsync(db, pending.Select(r => r.EmployeeId), cancellationToken);
        var mine = new List<LeaveRequest>();
        foreach (var r in pending)
        {
            if (await TimesheetQueries.CanApproveAsync(db, owners[r.EmployeeId], access.Email, roles, cancellationToken))
            {
                mine.Add(r);
            }
        }

        return await MapAsync(db, mine.OrderBy(r => r.StartDate).ToList(), cancellationToken);
    }

    public Task<LeaveRequestDto> ApproveAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, approve: true, request?.Comment, cancellationToken);

    public Task<LeaveRequestDto> RejectAsync(Guid requestId, DecideLeaveRequest request, CancellationToken cancellationToken = default) =>
        DecideAsync(requestId, approve: false, request?.Comment, cancellationToken);

    private async Task<LeaveRequestDto> DecideAsync(Guid requestId, bool approve, string? comment, CancellationToken ct)
    {
        access.RequireSignedIn();
        await using var db = await dbFactory.CreateAsync(ct);
        var leave = await LoadAsync(db, requestId, ct);
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == leave.EmployeeId, ct);
        if (!await TimesheetQueries.CanApproveAsync(db, owner, access.Email, await access.GetOrgRolesAsync(ct), ct))
        {
            throw new WorkbenchForbiddenException(string.Equals(owner.Email, access.Email, StringComparison.OrdinalIgnoreCase)
                ? "You can't decide your own leave request."
                : "Only the employee's supervisor (or an administrator, when they have none) can decide this request.");
        }

        if (leave.Status != LeaveRequestStatus.Pending)
        {
            throw new ArgumentException($"This request is already {leave.Status.ToString().ToLowerInvariant()}.");
        }

        var note = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (!approve && note is null)
        {
            throw new ArgumentException("Give a reason for rejecting the request.");
        }

        var policy = await db.Set<LeavePolicy>().AsNoTracking().FirstAsync(p => p.Id == leave.PolicyId, ct);
        var warnings = new List<string>();
        if (approve)
        {
            var available = await AvailableAsync(db, owner.Id, policy.Id, ct, excludeRequestId: leave.Id);
            if (leave.Hours > available)
            {
                if (!policy.AllowNegativeBalance)
                {
                    throw new ArgumentException($"{owner.DisplayName} has {available:0.##} h of {policy.Name} available, less than the {leave.Hours:0.##} h requested.");
                }

                warnings.Add($"{owner.DisplayName}'s {policy.Name} balance will go negative.");
            }
        }

        var before = Snapshot(leave);
        leave.Status = approve ? LeaveRequestStatus.Approved : LeaveRequestStatus.Rejected;
        leave.DecidedBy = access.Email;
        leave.DecidedAtUtc = UtcNow;
        leave.DecisionComment = note;
        audit.Record(db, AuditEntityType, leave.Id, approve ? "Approved" : "Rejected", before, Snapshot(leave), note);
        await db.SaveChangesAsync(ct);

        var actor = await EmployeeLinks.FindForUserAsync(db, access.Email, ct);
        await notifications.PublishAsync(owner.Email, approve ? LeaveNotificationKinds.Approved : LeaveNotificationKinds.Rejected,
            $"{actor?.DisplayName ?? access.Email} {(approve ? "approved" : "rejected")} your {policy.Name} request",
            approve ? $"{Range(leave)}: {leave.Hours:0.##} h. It's pre-filled on your timesheet." : $"{Range(leave)}: {note}",
            link: approve ? $"time?date={leave.StartDate:yyyy-MM-dd}" : "leave");
        return (await MapAsync(db, [leave], ct))[0] with { Warnings = warnings };
    }

    public async Task<IReadOnlyList<TeamCalendarEntryDto>> GetTeamCalendarAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        if (to < from || to.DayNumber - from.DayNumber > 400)
        {
            throw new ArgumentException("Choose a range of at most 400 days.");
        }

        var (rangeFrom, rangeTo) = (from, to);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await EmployeeLinks.FindForUserAsync(db, access.Email, cancellationToken);
        var everyone = (await access.GetOrgRolesAsync(cancellationToken)).CanViewAllTimesheets();
        HashSet<Guid>? team = null;
        if (!everyone)
        {
            team = [];
            if (me is not null)
            {
                team.Add(me.Id);
                var related = await db.Set<Employee>().AsNoTracking()
                    .Where(e => e.SupervisorId == me.Id || e.Id == me.SupervisorId || (me.SupervisorId != null && e.SupervisorId == me.SupervisorId))
                    .Select(e => e.Id).ToListAsync(cancellationToken);
                team.UnionWith(related);
            }
        }

        var rows = await (
                from d in db.Set<LeaveRequestDay>().AsNoTracking()
                join r in db.Set<LeaveRequest>().AsNoTracking() on d.RequestId equals r.Id
                join p in db.Set<LeavePolicy>().AsNoTracking() on r.PolicyId equals p.Id
                join e in db.Set<Employee>().AsNoTracking() on r.EmployeeId equals e.Id
                where d.Date >= rangeFrom && d.Date <= rangeTo && (r.Status == LeaveRequestStatus.Approved || r.Status == LeaveRequestStatus.Pending)
                select new { d.Date, d.Hours, r.Id, r.EmployeeId, r.Status, e.DisplayName, PolicyName = p.Name })
            .ToListAsync(cancellationToken);
        var entries = rows.Where(r => team is null || team.Contains(r.EmployeeId))
            .Select(r => new TeamCalendarEntryDto(r.Date, r.Status == LeaveRequestStatus.Approved ? TeamCalendarEntryKind.Leave : TeamCalendarEntryKind.PendingLeave,
                r.EmployeeId, $"{r.DisplayName}: {r.PolicyName}{(r.Status == LeaveRequestStatus.Pending ? " (pending)" : "")}", r.Hours, r.Id))
            .ToList();
        var holidays = await db.Set<Holiday>().AsNoTracking().Where(h => h.Date >= from && h.Date <= to).ToListAsync(cancellationToken);
        entries.AddRange(holidays.Select(h => new TeamCalendarEntryDto(h.Date, TeamCalendarEntryKind.Holiday, null, h.Name, h.Hours, null)));
        return entries.OrderBy(e => e.Date).ThenBy(e => e.Kind).ThenBy(e => e.Title, StringComparer.Ordinal).ToList();
    }

    /// <summary>Balance less approved-but-untaken and pending requests (optionally not counting one request).</summary>
    private async Task<decimal> AvailableAsync(IWorkbenchDbContext db, Guid employeeId, Guid policyId, CancellationToken ct, Guid? excludeRequestId = null)
    {
        await LeaveEngine.SyncUsageAsync(db, [employeeId], audit.Actor, UtcNow, ct);
        var balance = (await LeaveEngine.BalancesAsync(db, [employeeId], null, ct)).GetValueOrDefault((employeeId, policyId));
        var planned = (await LeaveEngine.PlannedAsync(db, [employeeId], null, null, ct)).GetValueOrDefault((employeeId, policyId));
        var excluded = excludeRequestId is { } id
            ? await db.Set<LeaveRequest>().AsNoTracking().Where(r => r.Id == id && r.Status == LeaveRequestStatus.Approved).SumAsync(r => r.Hours, ct)
            : 0;
        var pending = await db.Set<LeaveRequest>().AsNoTracking()
            .Where(r => r.EmployeeId == employeeId && r.PolicyId == policyId && r.Status == LeaveRequestStatus.Pending && r.Id != excludeRequestId)
            .Select(r => r.Hours).ToListAsync(ct);
        return balance - (planned - excluded) - pending.Sum();
    }

    private async Task<Employee> MeAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        access.RequireSignedIn();
        return await EmployeeLinks.FindForUserAsync(db, access.Email, ct)
            ?? throw new UnauthorizedAccessException("You don't have an employee record, so you can't request leave.");
    }

    private static async Task<LeaveRequest> LoadAsync(IWorkbenchDbContext db, Guid requestId, CancellationToken ct) =>
        await db.Set<LeaveRequest>().Include(r => r.Days).FirstOrDefaultAsync(r => r.Id == requestId, ct)
            ?? throw WorkbenchNotFoundException.For("Leave request", requestId);

    private static async Task<Dictionary<Guid, Employee>> EmployeesAsync(IWorkbenchDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Employee>().AsNoTracking().Where(e => list.Contains(e.Id)).ToDictionaryAsync(e => e.Id, ct);
    }

    private static async Task<List<LeaveRequestDto>> MapAsync(IWorkbenchDbContext db, IReadOnlyList<LeaveRequest> requests, CancellationToken ct)
    {
        var employees = await EmployeesAsync(db, requests.Select(r => r.EmployeeId), ct);
        var policyIds = requests.Select(r => r.PolicyId).Distinct().ToList();
        var policies = await db.Set<LeavePolicy>().AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var codes = await LeavePolicyService.WbsCodesAsync(db, policies.Values.Select(p => p.WbsElementId), ct);
        return requests.Select(r => new LeaveRequestDto(
            r.Id, r.EmployeeId, employees.GetValueOrDefault(r.EmployeeId)?.DisplayName ?? "?", r.PolicyId,
            policies.GetValueOrDefault(r.PolicyId)?.Name ?? "?", policies.TryGetValue(r.PolicyId, out var p) ? codes.GetValueOrDefault(p.WbsElementId, "?") : "?",
            r.StartDate, r.EndDate, r.Hours, r.Days.OrderBy(d => d.Date).Select(d => new LeaveDayDto(d.Date, d.Hours)).ToList(),
            (LeaveRequestState)(int)r.Status, r.Comment, r.CreatedAtUtc, r.DecidedBy, r.DecidedAtUtc, r.DecisionComment, [])).ToList();
    }

    private static string Range(LeaveRequest r) => r.StartDate == r.EndDate ? $"{r.StartDate:ddd MMM d}" : $"{r.StartDate:MMM d} – {r.EndDate:MMM d}";

    private static object Snapshot(LeaveRequest r) => new { Status = r.Status.ToString(), r.StartDate, r.EndDate, r.Hours, r.DecidedBy };
}

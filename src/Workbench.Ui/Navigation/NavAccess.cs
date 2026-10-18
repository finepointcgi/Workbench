using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Leave;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Ui.Navigation;

/// <summary>
/// What the signed-in user may open, for shaping the menu and the command palette. It is cosmetic: every page and
/// the server check again.
/// </summary>
public sealed record NavAccess(
    CurrentUserInfo? User,
    bool IsTimesheetApprover = false,
    bool IsLeaveApprover = false,
    bool IsProjectManager = false,
    int ApprovalCount = 0,
    int LeavePending = 0,
    int ReceivableCount = 0)
{
    /// <summary>Nobody signed in (or the server is unreachable): Home only.</summary>
    public static NavAccess Anonymous { get; } = new((CurrentUserInfo?)null);

    public bool IsSignedIn => User is not null;

    public bool IsAdmin => User?.IsAdmin == true;

    public bool IsFinance => User?.IsFinance == true;

    public bool IsTimekeeper => User?.Roles.HasFlag(OrgRoles.Timekeeper) == true;

    public bool IsResourceManager => User?.Roles.IsResourceManager() == true;

    /// <summary>Named as the receiver on an open purchase order.</summary>
    public bool IsReceiver => ReceivableCount > 0;

    /// <summary>Anyone who approves something, or has something waiting on them.</summary>
    public bool ShowApprovals => IsSignedIn && (ApprovalCount > 0 || IsFinance || IsTimekeeper || IsTimesheetApprover || IsLeaveApprover || IsProjectManager);

    /// <summary>Supervisors, timekeepers and administrators see their team's timesheets.</summary>
    public bool ShowTeamTime => IsAdmin || IsTimekeeper || IsTimesheetApprover;

    /// <summary>
    /// Works out the user's access from whichever services are registered (tests register only some). Each lookup is
    /// independent: one failing hides only what it decides.
    /// </summary>
    public static async Task<NavAccess> LoadAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        CurrentUserInfo user;
        try
        {
            if (services.GetService<ICurrentUserContext>() is not { } context)
            {
                return Anonymous;
            }

            user = await context.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return Anonymous;
        }

        var timesheetApprover = await TryAsync(services.GetService<ITimesheetApprovalService>(), s => s.IsApproverAsync(cancellationToken), false);
        var leave = services.GetService<ILeaveRequestService>();
        var leaveApprover = await TryAsync(leave, s => s.IsApproverAsync(cancellationToken), false);
        var leavePending = leaveApprover ? await TryAsync(leave, async s => (await s.ListPendingApprovalsAsync(cancellationToken)).Count, 0) : 0;
        var receivable = user.IsFinance ? 0 : await TryAsync(services.GetService<IPurchaseOrderService>(), async s => (await s.ListReceivableAsync(cancellationToken)).Count, 0);
        var projectManager = user.EmployeeId is { } employeeId && await TryAsync(services.GetService<IProjectService>(),
            async s => (await s.ListProjectsAsync(cancellationToken)).Any(p => p.ProjectManagerId == employeeId), false);
        var approvals = await CountApprovalsAsync(services, cancellationToken);

        return new NavAccess(user, timesheetApprover, leaveApprover, projectManager, approvals, leavePending, receivable);
    }

    /// <summary>How many documents in the approvals inbox wait on the signed-in user.</summary>
    public static Task<int> CountApprovalsAsync(IServiceProvider services, CancellationToken cancellationToken = default) =>
        TryAsync(services.GetService<IApprovalInboxService>(), s => s.CountMineAsync(cancellationToken), 0);

    private static async Task<T> TryAsync<TService, T>(TService? service, Func<TService, Task<T>> read, T fallback)
        where TService : class
    {
        if (service is null)
        {
            return fallback;
        }

        try
        {
            return await read(service);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return fallback;
        }
    }

    internal static bool IsExpected(Exception ex) =>
        ex is HttpRequestException or UnauthorizedAccessException or TaskCanceledException or InvalidOperationException
            or ArgumentException or WorkbenchNotFoundException or NotSupportedException;
}

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
    int ReceivableCount = 0,
    bool OwnsBudget = false,
    bool HideGovernment = false,
    IReadOnlySet<string>? UnavailableModules = null,
    IReadOnlySet<string>? Flags = null)
{
    /// <summary>True when a module's <see cref="INavAccessFlag"/> with this key holds for the signed-in user (such as "manages an opportunity").</summary>
    public bool HasFlag(string key) => Flags?.Contains(key) == true;

    /// <summary>False for a module the license doesn't include and that is enforced; its pages are hidden. <c>UnavailableModules</c> null: every module.</summary>
    public bool HasModule(string moduleId) => UnavailableModules?.Contains(moduleId) != true;

    /// <summary>Nobody signed in (or the server is unreachable): Home only.</summary>
    public static NavAccess Anonymous { get; } = new((CurrentUserInfo?)null);

    public bool IsSignedIn => User is not null;

    /// <summary>Federal-only pages (DCAA, CPSR, CAS, CMMC, SAM.gov leads, incurred cost) show unless the organization turned them off.</summary>
    public bool ShowGovernment => !HideGovernment;

    /// <summary>True when the user holds <paramref name="permission"/> (a <see cref="Permission"/> constant).</summary>
    public bool Can(string permission) => User?.Can(permission) == true;

    /// <summary>Holds any Administration permission: sees the Admin section by that name (otherwise "Setup").</summary>
    public bool IsAdministrator => User is { } u && u.Permissions.Any(p => PermissionCatalog.Find(p)?.Area == PermissionCatalog.AreaAdministration);

    public bool IsResourceManager => Can(Permission.ResourcingManage);

    /// <summary>Manages personnel records (HR).</summary>
    public bool IsHr => Can(Permission.HrRecordsManage);

    /// <summary>Holds "Run the projects you manage" (whether or not they manage a project yet).</summary>
    public bool HasProjectManagerRole => Can(Permission.ProjectsManageOwn);

    /// <summary>Runs projects: "Run the projects you manage", or the manager of at least one project.</summary>
    public bool IsPm => IsProjectManager || HasProjectManagerRole;

    /// <summary>Changes parts, inventory, BOMs, work orders and MRP.</summary>
    public bool IsMaterials => Can(Permission.MaterialsManage);

    /// <summary>Sees the Materials section.</summary>
    public bool CanViewMaterials => Can(Permission.MaterialsView);

    /// <summary>Named as the receiver on an open purchase order.</summary>
    public bool IsReceiver => ReceivableCount > 0;

    /// <summary>Anyone who approves something, or has something waiting on them.</summary>
    public bool ShowApprovals => IsSignedIn && (ApprovalCount > 0 || Can(Permission.ApBillsManage) || Can(Permission.TimeTeam) || IsTimesheetApprover || IsLeaveApprover || IsPm);

    /// <summary>Supervisors see their team's timesheets; "Team time for everyone" sees everyone's.</summary>
    public bool ShowTeamTime => Can(Permission.TimeTeam) || IsTimesheetApprover;

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
        var receivable = user.Can(Permission.PurchaseOrdersManage) ? 0 : await TryAsync(services.GetService<IPurchaseOrderService>(), async s => (await s.ListReceivableAsync(cancellationToken)).Count, 0);
        var projectManager = user.EmployeeId is { } employeeId && await TryAsync(services.GetService<IProjectService>(),
            async s => (await s.ListProjectsAsync(cancellationToken)).Any(p => p.ProjectManagerId == employeeId), false);
        var approvals = await CountApprovalsAsync(services, cancellationToken);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var flag in services.GetServices<INavAccessFlag>())
        {
            if (await TryAsync(flag, f => f.HoldsAsync(user, services, cancellationToken), false))
            {
                flags.Add(flag.Key);
            }
        }

        var ownsBudget = await TryAsync(services.GetService<Contracts.Planning.IPlanningCycleService>(), async s => (await s.ListMineAsync(cancellationToken)).Count > 0, false);

        var hideGovernment = !await TryAsync(services.GetService<Contracts.Company.ICompanyProfileService>(), async s => (await s.GetAsync(cancellationToken)).ShowGovernmentFeatures, true);
        var unavailable = await TryAsync<Contracts.Licensing.ILicenseService, HashSet<string>?>(services.GetService<Contracts.Licensing.ILicenseService>(),
            async s => (await s.GetStatusAsync(cancellationToken)).Modules?.Where(m => !m.Available && m.WholeModule).Select(m => m.Id).ToHashSet(), null);

        return new NavAccess(user, timesheetApprover, leaveApprover, projectManager, approvals, leavePending, receivable, ownsBudget, hideGovernment, unavailable, flags);
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

/// <summary>
/// A relationship a module reports for the menu: "this user is the capture manager of an opportunity" lets them see the pages of it without
/// the permission that shows everything. Registered in <c>ConfigureServices</c>; <c>ModuleNavEntry.AlsoWhenFlag</c> names the <see cref="Key"/>.
/// Cosmetic like the rest of <see cref="NavAccess"/>: the page's services check again. A failure counts as "no".
/// </summary>
public interface INavAccessFlag
{
    string Key { get; }

    Task<bool> HoldsAsync(CurrentUserInfo user, IServiceProvider services, CancellationToken cancellationToken);
}

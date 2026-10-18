using System.Runtime.CompilerServices;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Leave;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Time;

namespace Workbench.Tests.Navigation;

/// <summary>A signed-in user and what waits on them, for rendering the menu and the palette.</summary>
public sealed record NavUser(
    OrgRoles Roles,
    Guid? EmployeeId = null,
    bool TimesheetApprover = false,
    bool LeaveApprover = false,
    int Approvals = 0,
    int Receivable = 0,
    IReadOnlyList<ProjectDto>? Projects = null,
    IReadOnlyList<SheetSummary>? Sheets = null,
    string DisplayName = "Someone",
    string Email = "someone@example.com");

public static class NavTestKit
{
    public static BunitContext NewContext(NavUser user)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<ICurrentUserContext>(new UserContext(new CurrentUserInfo(user.Email, user.DisplayName, user.Roles, user.EmployeeId)));
        ctx.Services.AddSingleton(Stub<ITimesheetApprovalService>.Create(("IsApproverAsync", _ => Task.FromResult(user.TimesheetApprover))));
        ctx.Services.AddSingleton(Stub<ILeaveRequestService>.Create(
            ("IsApproverAsync", _ => Task.FromResult(user.LeaveApprover)),
            ("ListPendingApprovalsAsync", _ => Task.FromResult<IReadOnlyList<LeaveRequestDto>>([]))));
        ctx.Services.AddSingleton(Stub<IApprovalInboxService>.Create(("CountMineAsync", _ => Task.FromResult(user.Approvals))));
        ctx.Services.AddSingleton(Stub<IPurchaseOrderService>.Create(("ListReceivableAsync", _ => Task.FromResult<IReadOnlyList<PurchaseOrderSummaryDto>>(
            Enumerable.Range(0, user.Receivable).Select(i => (PurchaseOrderSummaryDto)RuntimeHelpers.GetUninitializedObject(typeof(PurchaseOrderSummaryDto))).ToList()))));
        ctx.Services.AddSingleton(Stub<IProjectService>.Create(("ListProjectsAsync", _ => Task.FromResult(user.Projects ?? []))));
        ctx.Services.AddSingleton(Stub<ISheetService>.Create(("ListAsync", _ => Task.FromResult(user.Sheets ?? []))));
        ctx.Services.AddSingleton(Stub<IDashboardService>.Create(("ListAsync", _ => Task.FromResult<IReadOnlyList<DashboardSummary>>([]))));
        return ctx;
    }

    public static ProjectDto Project(string code, string name, Guid? managerId = null) =>
        new(Guid.NewGuid(), code, name, null, null, managerId, ProjectState.Active, null, null, null);

    public static SheetSummary Sheet(string name) => new(Guid.NewGuid(), name, null, "Engineering", 0, false, null, DateTime.UtcNow);

    private sealed class UserContext(CurrentUserInfo info) : ICurrentUserContext
    {
        public Task<CurrentUserInfo> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(info);
    }
}

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Collaboration;
using Workbench.Application.Sheets;
using Workbench.Contracts.Automation;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Time;

namespace Workbench.Application.Identity;

public static class IdentityRegistration
{
    /// <summary>
    /// Users, workspace memberships and permission checks. Needs an <see cref="ICurrentUser"/> from the host
    /// (the web host's claims user, or <see cref="AddWorkbenchSystemUser"/> for background jobs).
    /// <para>
    /// Call it after every other feature registration: <c>Decorate</c> wraps the last registration of each service,
    /// so the permission decorators end up outermost and a refused call never reaches activity recording,
    /// formulas or anything else that decorates the same services.
    /// </para>
    /// </summary>
    public static IServiceCollection AddWorkbenchIdentity(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<UserProvisioner>();
        services.AddScoped<AccessControl>();
        services.AddScoped<ISheetPermissions, SheetPermissions>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IWorkspaceMembershipService, WorkspaceMembershipService>();
        services.AddScoped<ISheetWorkspacePicker, EditableWorkspacePicker>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped<IFinancialAccessService>(sp => sp.GetRequiredService<ProjectAccess>());

        services.Decorate<ISheetService, PermissionCheckingSheetService>();
        services.Decorate<IItemService, PermissionCheckingItemService>();
        services.Decorate<IColumnService, PermissionCheckingColumnService>();
        services.Decorate<IDependencyService, PermissionCheckingDependencyService>();
        services.Decorate<IBaselineService, PermissionCheckingBaselineService>();
        services.Decorate<ICommentService, PermissionCheckingCommentService>();
        services.Decorate<IAttachmentService, PermissionCheckingAttachmentService>();
        services.Decorate<IActivityService, PermissionCheckingActivityService>();
        services.Decorate<ICostpointService, PermissionCheckingCostpointService>();
        services.Decorate<IPeopleService, PermissionCheckingPeopleService>();
        services.Decorate<IContractService, PermissionCheckingContractService>();
        services.Decorate<IProjectService, PermissionCheckingProjectService>();
        services.Decorate<IFiscalCalendarService, PermissionCheckingFiscalCalendarService>();
        services.Decorate<IAuditLogService, PermissionCheckingAuditLogService>();
        services.Decorate<ITimeReportService, PermissionCheckingTimeReportService>();
        services.Decorate<IProjectFinancialsService, PermissionCheckingProjectFinancialsService>();
        services.Decorate<IBudgetService, PermissionCheckingBudgetService>();
        services.Decorate<IFinancialExportService, PermissionCheckingFinancialExportService>();
        services.Decorate<IOdcService, PermissionCheckingOdcService>();
        services.Decorate<IRuleService, PermissionCheckingRuleService>();
        services.Decorate<IDashboardService, PermissionCheckingDashboardService>();
        services.AddWorkbenchAccountingPermissions(); // General ledger (accounting branch): Finance or Admin only on all five GL services.
        services.AddScoped<BillingAccess>(); services.AddWorkbenchBillingPermissions(); // Billing/AR: Finance or Admin; PMs read their contracts' invoices.
        return services;
    }

    /// <summary>For hosts with nobody signed in (the worker): every call runs as <see cref="SystemCurrentUser"/>.</summary>
    public static IServiceCollection AddWorkbenchSystemUser(this IServiceCollection services)
    {
        services.TryAddSingleton<ICurrentUser, SystemCurrentUser>();
        return services;
    }
}

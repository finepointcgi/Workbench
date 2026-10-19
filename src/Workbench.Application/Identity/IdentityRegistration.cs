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
        services.AddScoped<SystemExecutionContext>(); // Inactive unless a background job's scope switches it on.
        services.AddScoped<RuleAuthorExecutionContext>(); // Inactive unless the date-trigger job acts for a rule's author.
        services.AddScoped<AccessControl>();
        services.AddScoped<ISheetPermissions, SheetPermissions>();
        services.AddScoped<IUserDirectory, UserDirectory>();
        services.AddScoped<ICurrentUserContext, CurrentUserContext>();
        services.AddScoped<IWorkspaceMembershipService, WorkspaceMembershipService>();
        services.AddScoped<SheetWorkspacePreference>();
        services.AddScoped<ISheetWorkspacePicker, EditableWorkspacePicker>();
        services.AddScoped<ProjectAccess>();
        services.AddScoped<RestrictedColumnAccess>();
        services.AddScoped<AssigneeStatusPermission>();
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
        services.Decorate<IPlanSyncService, PermissionCheckingPlanSyncService>(); // Plan sheet sync: project setup (Finance, Admin, the project's PM with the role).
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
        services.AddWorkbenchContractDetailPermissions(); // Contract detail: reads Finance/Admin/PM on the contract, writes Finance/Admin.
        services.AddWorkbenchIndirectRatePermissions(); // Indirect rates: Finance or Admin; burdened cost: Finance, Admin or the project's PM.
        services.AddScoped<BillingAccess>(); services.AddWorkbenchBillingPermissions(); // Billing/AR: Finance or Admin; PMs read their contracts' invoices.
        services.AddWorkbenchPurchasingPermissions(); // Purchasing: Finance or Admin; PMs approve the AP bill lines routed to them.
        services.Decorate<Workbench.Contracts.Imports.IImportService, PermissionCheckingImportService>(); // Imports: Finance or Admin.
        services.AddWorkbenchProcurementPermissions(); // Requisitions: any employee for their projects/org; POs and match: Finance/Admin (receivers receive).
        services.AddWorkbenchAssetPermissions(); // Fixed assets: Finance or Admin only.
        services.AddWorkbenchCashPermissions(); // Cash management and bank reconciliation: Finance or Admin only.
        services.AddWorkbenchPayrollPermissions(); // Payroll: Finance or Admin only (pay rates masked unless revealed).
        services.AddWorkbenchPerDiemResourcingPermissions(); // Per diem imports Finance/Admin; allocations PMs (own projects) and resource managers (all).
        services.AddWorkbenchRevenuePermissions(); // Revenue: setup, runs and reports Finance/Admin; PMs read their projects and submit their EACs and deliveries.
        services.AddWorkbenchIncurredCostPermissions(); // Incurred cost submissions: Finance or Admin only.
        services.AddWorkbenchReportPermissions(); // Reports: signed in; row/field rules inside the reports; exports are security events.
        services.AddWorkbenchEvmPermissions(); // Earned value: Finance, Admin or the project's PM; BCR approval and period reopen Finance/Admin.
        services.AddWorkbenchCeilingPermissions(); // Ceilings and closeout: reads Finance/Admin/PMs; changes Finance/Admin (PMs may override where allowed).
        services.Decorate<Workbench.Contracts.Approvals.IApprovalWorkflowService, Approvals.PermissionCheckingApprovalWorkflowService>(); // Approval workflows: read Finance/Admin, change Admin.
        services.AddScoped<Projects.ProjectHubAccess>(); services.Decorate<Workbench.Contracts.Projects.IProjectHubService, PermissionCheckingProjectHubService>(); // Projects hub: list scoped in the service; project page for those on it; team Finance/Admin/PM.
        services.Decorate<Workbench.Contracts.Support.ISupportService, PermissionCheckingSupportService>(); // Support requests: send and read your own; admins list, read and update all.
        services.Decorate<Workbench.Contracts.Payroll.IPayrollLayoutService, PermissionCheckingPayrollLayoutService>(); // Payroll file layouts: Finance or Admin only.
        services.AddWorkbenchMigrationPermissions(); // Cut-over checklist and Costpoint reconciliation: Finance or Admin only.
        services.Decorate<Workbench.Contracts.Tasks.ITaskAssignmentService, PermissionCheckingTaskAssignmentService>(); // Tasks: Viewers read, Editors/Owners assign, assignees update their own status; workload Finance/Admin/PM.
        services.AddWorkbenchHrPermissions(); // HR: records HR/Admin (employees self-service, supervisors/Finance job fields), EEO HR/Admin/self, compensation HR+Finance.
        services.AddWorkbenchMaterialsPermissions(); // Materials: read Materials/Finance/Admin, change Materials/Admin; revaluation and WIP Finance; own shop labor anyone.
        return services;
    }

    /// <summary>
    /// For the server host, after its <see cref="ICurrentUser"/> is registered and before the automation hosting:
    /// background jobs running in a <see cref="SystemExecutionContext"/> are attributed to "Workbench System".
    /// </summary>
    public static IServiceCollection AddWorkbenchSystemExecutionHosting(this IServiceCollection services)
    {
        services.TryAddScoped<SystemExecutionContext>();
        services.Decorate<ICurrentUser, SystemAwareCurrentUser>();
        return services;
    }

    /// <summary>For hosts with nobody signed in (the worker): every call runs as <see cref="SystemCurrentUser"/>.</summary>
    public static IServiceCollection AddWorkbenchSystemUser(this IServiceCollection services)
    {
        services.TryAddSingleton<ICurrentUser, SystemCurrentUser>();
        return services;
    }
}

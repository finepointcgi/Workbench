using Microsoft.Extensions.DependencyInjection;
using Workbench.ApiClient.Security;
using Workbench.Contracts.Costpoint;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public static class DependencyInjection
{
    /// <summary>Binds the UI service contracts to HTTP clients for the Workbench server.</summary>
    public static IServiceCollection AddWorkbenchApiClient(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ISheetService, HttpSheetService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IColumnService, HttpColumnService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IItemService, HttpItemService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetViewService, HttpSheetViewService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetBulkEditService, HttpSheetBulkEditService>(client => client.BaseAddress = serverBaseAddress);
        services.AddWorkbenchSchedulingClients(serverBaseAddress);
        services.AddCollaborationClients(serverBaseAddress);
        services.AddHttpClient<ICostpointService, HttpCostpointService>(client => client.BaseAddress = serverBaseAddress);
        services.AddProjectAccountingClients(serverBaseAddress);
        services.AddIdentityClients(serverBaseAddress);
        services.AddAutomationClients(serverBaseAddress);
        services.AddDashboardClients(serverBaseAddress);
        services.AddTimekeepingClients(serverBaseAddress);
        services.AddOdcClients(serverBaseAddress);
        services.AddFinancialsClients(serverBaseAddress);
        services.AddSetupClients(serverBaseAddress);
        services.AddAccountingClients(serverBaseAddress);
        services.AddSecurityAdminClients(serverBaseAddress);
        services.AddHttpClient<Contracts.Notifications.IEmailDeliverabilityService, HttpEmailDeliverabilityService>(client => client.BaseAddress = serverBaseAddress);
        services.AddContractDetailClients(serverBaseAddress);
        services.AddIndirectRateClients(serverBaseAddress);
        services.AddBillingClients(serverBaseAddress);
        services.AddPurchasingClients(serverBaseAddress);
        services.AddHttpClient<Contracts.Tenancy.IHostAdminService, HttpHostAdminService>(client => client.BaseAddress = serverBaseAddress);
        services.AddImportClients(serverBaseAddress);
        services.AddProcurementClients(serverBaseAddress);
        services.AddAssetClients(serverBaseAddress);
        services.AddCashClients(serverBaseAddress);
        services.AddPayrollLeaveClients(serverBaseAddress);
        services.AddPerDiemResourcingClients(serverBaseAddress);
        services.AddRevenueClients(serverBaseAddress);
        services.AddIncurredCostClients(serverBaseAddress);
        services.AddReportClients(serverBaseAddress);
        services.AddEvmClients(serverBaseAddress);
        services.AddCeilingClients(serverBaseAddress);
        services.AddApprovalClients(serverBaseAddress);
        services.AddMigrationClients(serverBaseAddress);
        services.AddHttpClient<Contracts.Projects.IProjectHubService, HttpProjectHubService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.MyTodo.IMyTodoService, HttpMyTodoService>(client => client.BaseAddress = serverBaseAddress);
        services.AddSignInSettingsClients(serverBaseAddress);
        services.AddHttpClient<Contracts.Support.ISupportService, HttpSupportService>(client => client.BaseAddress = serverBaseAddress); services.AddHttpClient<Contracts.Support.IHelpStateService, HttpHelpStateService>(client => client.BaseAddress = serverBaseAddress); services.AddHttpClient<Contracts.Support.IHostSupportService, HttpHostSupportService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.Tasks.ITaskAssignmentService, HttpTaskAssignmentService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHrClients(serverBaseAddress);
        services.AddMaterialsClients(serverBaseAddress);
        services.AddCrmClients(serverBaseAddress);
        services.AddPricingClients(serverBaseAddress);
        services.AddAssistantClients(serverBaseAddress);
        services.AddPlanningClients(serverBaseAddress);
        services.AddTotalTimeClients(serverBaseAddress);
        services.AddCurrencyClients(serverBaseAddress);
        services.AddCompanyClients(serverBaseAddress);
        services.AddGovernmentBillingClients(serverBaseAddress);
        services.AddTravelClients(serverBaseAddress);
        services.AddSalesShippingClients(serverBaseAddress);
        services.AddQualityClients(serverBaseAddress);
        services.AddShopFloorClients(serverBaseAddress);
        services.AddSourcingClients(serverBaseAddress);
        services.AddBankFileClients(serverBaseAddress);
        services.AddSalesTaxClients(serverBaseAddress);
        services.AddIntegrationClients(serverBaseAddress);
        services.AddComplianceClients(serverBaseAddress);
        services.AddReceiptClients(serverBaseAddress);
        services.AddCustomizationClients(serverBaseAddress);
        services.AddHttpClient<Contracts.Identity.IRoleService, HttpRoleService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.Identity.IPermissionCatalog, HttpPermissionCatalog>(client => client.BaseAddress = serverBaseAddress);
        services.AddAutomationBuilderClients(serverBaseAddress);
        services.AddLayoutClients(serverBaseAddress);
        services.AddSheetExtrasClients(serverBaseAddress);
        return services;
    }
}

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
        services.AddCeilingClients(serverBaseAddress);
        return services;
    }
}

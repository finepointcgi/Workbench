using Microsoft.Extensions.DependencyInjection;
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
        return services;
    }
}

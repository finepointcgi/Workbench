using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.MyWork;

namespace Workbench.ApiClient;

public sealed class HttpDashboardService(HttpClient http) : IDashboardService
{
    public async Task<IReadOnlyList<DashboardSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/dashboards", cancellationToken);
        return await response.ReadAsync<List<DashboardSummary>>(cancellationToken);
    }

    public async Task<DashboardDto> GetAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/dashboards/{dashboardId}", cancellationToken);
        return await response.ReadAsync<DashboardDto>(cancellationToken);
    }

    public async Task<DashboardSummary> CreateAsync(CreateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/dashboards", request, cancellationToken);
        return await response.ReadAsync<DashboardSummary>(cancellationToken);
    }

    public async Task<DashboardSummary> UpdateAsync(Guid dashboardId, UpdateDashboardRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/dashboards/{dashboardId}", request, cancellationToken);
        return await response.ReadAsync<DashboardSummary>(cancellationToken);
    }

    public async Task DeleteAsync(Guid dashboardId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/dashboards/{dashboardId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<WidgetDto> AddWidgetAsync(Guid dashboardId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/dashboards/{dashboardId}/widgets", request, cancellationToken);
        return await response.ReadAsync<WidgetDto>(cancellationToken);
    }

    public async Task<WidgetDto> UpdateWidgetAsync(Guid dashboardId, Guid widgetId, SaveWidgetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/dashboards/{dashboardId}/widgets/{widgetId}", request, cancellationToken);
        return await response.ReadAsync<WidgetDto>(cancellationToken);
    }

    public async Task DeleteWidgetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/dashboards/{dashboardId}/widgets/{widgetId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<DashboardDto> ReorderWidgetsAsync(Guid dashboardId, ReorderWidgetsRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/dashboards/{dashboardId}/widgets/reorder", request, cancellationToken);
        return await response.ReadAsync<DashboardDto>(cancellationToken);
    }
}

public sealed class HttpWidgetDataService(HttpClient http) : IWidgetDataService
{
    public async Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/dashboards/{dashboardId}/widgets/{widgetId}/data", cancellationToken);
        return await response.ReadAsync<WidgetDataDto>(cancellationToken);
    }
}

public static class DashboardClientRegistration
{
    /// <summary>My Work and dashboards over HTTP.</summary>
    public static IServiceCollection AddDashboardClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IMyWorkService, HttpMyWorkService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IDashboardService, HttpDashboardService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IWidgetDataService, HttpWidgetDataService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.TotalTime;

namespace Workbench.ApiClient;

public sealed class HttpTotalTimeService(HttpClient http) : ITotalTimeService
{
    private const string Root = "api/v1/total-time";

    public async Task<LaborCostSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/settings", cancellationToken)).ReadAsync<LaborCostSettingsDto>(cancellationToken);

    public async Task<LaborCostSettingsDto> SetMethodAsync(SetLaborCostMethodRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/settings", request, cancellationToken)).ReadAsync<LaborCostSettingsDto>(cancellationToken);

    public async Task<TimesheetTotalTimeDto> GetTimesheetSummaryAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/timesheets/{timesheetId}", cancellationToken)).ReadAsync<TimesheetTotalTimeDto>(cancellationToken);

    public async Task<TotalTimeReportDto> GetReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/report?from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}", cancellationToken)).ReadAsync<TotalTimeReportDto>(cancellationToken);
}

public static class TotalTimeClientRegistration
{
    public static IServiceCollection AddTotalTimeClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ITotalTimeService, HttpTotalTimeService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

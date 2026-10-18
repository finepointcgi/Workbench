using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Reports;

namespace Workbench.ApiClient;

public sealed class HttpStandardReportService(HttpClient http) : IStandardReportService
{
    public async Task<IReadOnlyList<StandardReportDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/reports/standard", cancellationToken).ReadListAsync<StandardReportDto>(cancellationToken);

    public async Task<ReportResultDto> RunAsync(string reportKey, ReportParameters parameters, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/reports/standard/{Uri.EscapeDataString(reportKey)}/run", parameters, cancellationToken)).ReadAsync<ReportResultDto>(cancellationToken);

    public async Task<ReportResultDto> DrillAsync(string reportKey, ReportParameters parameters, string drillKey, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/reports/standard/{Uri.EscapeDataString(reportKey)}/drill", new { parameters, drillKey }, cancellationToken)).ReadAsync<ReportResultDto>(cancellationToken);

    public async Task<ReportFileDto> ExportAsync(string reportKey, ReportParameters parameters, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/reports/standard/{Uri.EscapeDataString(reportKey)}/export?format={format}", parameters, cancellationToken)).ReadAsync<ReportFileDto>(cancellationToken);
}

public sealed class HttpReportWriterService(HttpClient http) : IReportWriterService
{
    public async Task<IReadOnlyList<ReportDataSourceDto>> ListSourcesAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/reports/sources", cancellationToken).ReadListAsync<ReportDataSourceDto>(cancellationToken);

    public async Task<ReportResultDto> RunAsync(ReportDefinition definition, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/reports/writer/run", definition, cancellationToken)).ReadAsync<ReportResultDto>(cancellationToken);

    public async Task<ReportFileDto> ExportAsync(ReportDefinition definition, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/reports/writer/export?format={format}", definition, cancellationToken)).ReadAsync<ReportFileDto>(cancellationToken);
}

public sealed class HttpSavedReportService(HttpClient http) : ISavedReportService
{
    public async Task<IReadOnlyList<SavedReportDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/reports/saved", cancellationToken).ReadListAsync<SavedReportDto>(cancellationToken);

    public async Task<SavedReportDto> GetAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/reports/saved/{reportId}", cancellationToken)).ReadAsync<SavedReportDto>(cancellationToken);

    public async Task<SavedReportDto> CreateAsync(SaveReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/reports/saved", request, cancellationToken)).ReadAsync<SavedReportDto>(cancellationToken);

    public async Task<SavedReportDto> UpdateAsync(Guid reportId, SaveReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/reports/saved/{reportId}", request, cancellationToken)).ReadAsync<SavedReportDto>(cancellationToken);

    public async Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/reports/saved/{reportId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<ReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/reports/saved/{reportId}/run", null, cancellationToken)).ReadAsync<ReportResultDto>(cancellationToken);

    public async Task<ReportFileDto> ExportAsync(Guid reportId, ReportFileFormat format, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/reports/saved/{reportId}/export?format={format}", cancellationToken)).ReadAsync<ReportFileDto>(cancellationToken);

    public async Task<ReportScheduleDto> AddScheduleAsync(Guid reportId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/reports/saved/{reportId}/schedules", request, cancellationToken)).ReadAsync<ReportScheduleDto>(cancellationToken);

    public async Task<ReportScheduleDto> UpdateScheduleAsync(Guid scheduleId, SaveReportScheduleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/reports/schedules/{scheduleId}", request, cancellationToken)).ReadAsync<ReportScheduleDto>(cancellationToken);

    public async Task DeleteScheduleAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/reports/schedules/{scheduleId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<ReportScheduleDto> SendNowAsync(Guid scheduleId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/reports/schedules/{scheduleId}/send", null, cancellationToken)).ReadAsync<ReportScheduleDto>(cancellationToken);
}

public static class ReportClientRegistration
{
    public static IServiceCollection AddReportClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IStandardReportService, HttpStandardReportService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IReportWriterService, HttpReportWriterService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISavedReportService, HttpSavedReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

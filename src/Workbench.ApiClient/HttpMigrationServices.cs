using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Migration;
using Workbench.Contracts.Payroll;

namespace Workbench.ApiClient;

public sealed class HttpMigrationService(HttpClient http) : IMigrationService
{
    public async Task<MigrationChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/migration/checklist", cancellationToken)).ReadAsync<MigrationChecklistDto>(cancellationToken);

    public async Task<MigrationSettingsDto> SaveSettingsAsync(SaveMigrationSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/migration/settings", request, cancellationToken)).ReadAsync<MigrationSettingsDto>(cancellationToken);

    public async Task<MigrationChecklistDto> SetStepAsync(string stepKey, SetMigrationStepRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/migration/steps/{Uri.EscapeDataString(stepKey)}", request, cancellationToken)).ReadAsync<MigrationChecklistDto>(cancellationToken);
}

public sealed class HttpParallelRunService(HttpClient http) : IParallelRunService
{
    private const string Base = "api/v1/migration/parallel-run";

    public async Task<ParallelRunDto> GetAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}{(fiscalPeriodId is { } id ? $"?periodId={id}" : "")}", cancellationToken)).ReadAsync<ParallelRunDto>(cancellationToken);

    public async Task<ParallelRunDto> UploadAsync(UploadParallelReportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/upload", request, cancellationToken)).ReadAsync<ParallelRunDto>(cancellationToken);

    public async Task<IReadOnlyList<ParallelCauseDto>> ExplainAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/runs/{runId}/causes", cancellationToken).ReadListAsync<ParallelCauseDto>(cancellationToken);
}

public sealed class HttpReconciliationService(HttpClient http) : IReconciliationService
{
    private const string Base = "api/v1/migration/reconciliation";

    public async Task<IReadOnlyList<ReconReportDto>> ListReportsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/reports", cancellationToken).ReadListAsync<ReconReportDto>(cancellationToken);

    public async Task<IReadOnlyList<ReconMappingDto>> ListMappingsAsync(ReconReportKind? kind = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/mappings{(kind is { } k ? $"?kind={k}" : "")}", cancellationToken).ReadListAsync<ReconMappingDto>(cancellationToken);

    public async Task<ReconMappingDto> SaveMappingAsync(ReconMappingDto mapping, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/mappings", mapping, cancellationToken)).ReadAsync<ReconMappingDto>(cancellationToken);

    public async Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/mappings/{mappingId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<ReconFilePreviewDto> PreviewFileAsync(PreviewReconFileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/preview", request, cancellationToken)).ReadAsync<ReconFilePreviewDto>(cancellationToken);

    public async Task<ReconRunDetailDto> RunAsync(RunReconciliationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/runs", request, cancellationToken)).ReadAsync<ReconRunDetailDto>(cancellationToken);

    public async Task<IReadOnlyList<ReconRunSummaryDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/runs{(fiscalPeriodId is { } p ? $"?periodId={p}" : "")}", cancellationToken).ReadListAsync<ReconRunSummaryDto>(cancellationToken);

    public async Task<ReconRunDetailDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}", cancellationToken)).ReadAsync<ReconRunDetailDto>(cancellationToken);

    public async Task<ReconDrillDownDto> DrillDownAsync(Guid runId, string key, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}/drill?key={Uri.EscapeDataString(key)}", cancellationToken)).ReadAsync<ReconDrillDownDto>(cancellationToken);

    public async Task<ReconRunSummaryDto> SignOffAsync(Guid runId, SignOffReconciliationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/runs/{runId}/sign-off", request, cancellationToken)).ReadAsync<ReconRunSummaryDto>(cancellationToken);

    public async Task<IReadOnlyList<ReconStatusDto>> GetStatusAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/status/{fiscalPeriodId}", cancellationToken).ReadListAsync<ReconStatusDto>(cancellationToken);

    public async Task<ReconExportFile> ExportAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}/export", cancellationToken)).ReadAsync<ReconExportFile>(cancellationToken);
}

public sealed class HttpPayrollLayoutService(HttpClient http) : IPayrollLayoutService
{
    private static string Url(string provider, string suffix = "") => $"api/v1/payroll/layouts/{Uri.EscapeDataString(provider)}{suffix}";

    public async Task<PayrollLayoutDto> GetAsync(string providerKey, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url(providerKey), cancellationToken)).ReadAsync<PayrollLayoutDto>(cancellationToken);

    public async Task<PayrollLayoutDto> GetDefaultAsync(string providerKey, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url(providerKey, "/default"), cancellationToken)).ReadAsync<PayrollLayoutDto>(cancellationToken);

    public async Task<PayrollLayoutDto> SaveAsync(string providerKey, SavePayrollLayoutRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync(Url(providerKey), request, cancellationToken)).ReadAsync<PayrollLayoutDto>(cancellationToken);

    public async Task<PayrollLayoutDto> ResetAsync(string providerKey, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync(Url(providerKey), cancellationToken)).ReadAsync<PayrollLayoutDto>(cancellationToken);

    public async Task<RegisterTestResultDto> TestRegisterAsync(string providerKey, TestRegisterSampleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Url(providerKey, "/test-register"), request, cancellationToken)).ReadAsync<RegisterTestResultDto>(cancellationToken);

    public async Task<HoursTemplateTestDto> TestHoursTemplateAsync(string providerKey, TestHoursTemplateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Url(providerKey, "/test-hours"), request, cancellationToken)).ReadAsync<HoursTemplateTestDto>(cancellationToken);
}

public static class MigrationClientRegistration
{
    public static IServiceCollection AddMigrationClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IMigrationService, HttpMigrationService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IReconciliationService, HttpReconciliationService>(c =>
        {
            c.BaseAddress = serverBaseAddress;
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddHttpClient<IParallelRunService, HttpParallelRunService>(c =>
        {
            c.BaseAddress = serverBaseAddress;
            c.Timeout = TimeSpan.FromMinutes(10);
        });
        services.AddHttpClient<IPayrollLayoutService, HttpPayrollLayoutService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

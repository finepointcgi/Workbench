using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Evm;

namespace Workbench.ApiClient;

public sealed class HttpEvmService(HttpClient http) : IEvmService
{
    private static string Period(string path, Guid? periodId) => periodId is { } p ? $"{path}?periodId={p}" : path;

    public async Task<EvmBaselineDto?> GetBaselineAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"api/v1/projects/{projectId}/evm/baseline", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadAsync<EvmBaselineDto>(cancellationToken);
    }

    public async Task<EvmBaselineDto> CreateBaselineAsync(Guid projectId, CreateEvmBaselineRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/evm/baseline", request, cancellationToken)).ReadAsync<EvmBaselineDto>(cancellationToken);

    public async Task<EvmBaselineDto> UpdateWorkPackageAsync(Guid workPackageId, UpdateWorkPackageRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/evm/work-packages/{workPackageId}", request, cancellationToken)).ReadAsync<EvmBaselineDto>(cancellationToken);

    public async Task<EvmBaselineDto> SetThresholdsAsync(Guid projectId, SetEvmThresholdsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/projects/{projectId}/evm/thresholds", request, cancellationToken)).ReadAsync<EvmBaselineDto>(cancellationToken);

    public async Task<EvmBaselineDto> SetProgressAsync(Guid workPackageId, SetWorkPackageProgressRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/evm/work-packages/{workPackageId}/progress", request, cancellationToken)).ReadAsync<EvmBaselineDto>(cancellationToken);

    public async Task<EvmBaselineDto> SetMilestoneCompletionAsync(Guid milestoneId, SetMilestoneCompletionRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/evm/milestones/{milestoneId}/completion", request, cancellationToken)).ReadAsync<EvmBaselineDto>(cancellationToken);

    public async Task<IReadOnlyList<EvmPeriodRefDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/evm/periods", cancellationToken).ReadListAsync<EvmPeriodRefDto>(cancellationToken);

    public async Task<EvmStatusDto> GetStatusAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Period($"api/v1/projects/{projectId}/evm/status", fiscalPeriodId), cancellationToken)).ReadAsync<EvmStatusDto>(cancellationToken);

    public async Task<EvmSeriesDto> GetSeriesAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/projects/{projectId}/evm/series", cancellationToken)).ReadAsync<EvmSeriesDto>(cancellationToken);

    public async Task<EvmPeriodRefDto> ClosePeriodAsync(Guid projectId, Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/projects/{projectId}/evm/periods/{fiscalPeriodId}/close", null, cancellationToken)).ReadAsync<EvmPeriodRefDto>(cancellationToken);

    public async Task<EvmPeriodRefDto> ReopenPeriodAsync(Guid projectId, Guid fiscalPeriodId, ReopenEvmPeriodRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/evm/periods/{fiscalPeriodId}/reopen", request, cancellationToken)).ReadAsync<EvmPeriodRefDto>(cancellationToken);

    public async Task<IReadOnlyList<EvmVarianceDto>> ListVariancesAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync(Period($"api/v1/projects/{projectId}/evm/variances", fiscalPeriodId), cancellationToken).ReadListAsync<EvmVarianceDto>(cancellationToken);

    public async Task<EvmVarianceDto> SaveVarianceAsync(Guid projectId, Guid fiscalPeriodId, Guid controlAccountWbsId, SaveVarianceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/projects/{projectId}/evm/periods/{fiscalPeriodId}/variances/{controlAccountWbsId}", request, cancellationToken))
            .ReadAsync<EvmVarianceDto>(cancellationToken);
}

public sealed class HttpEvmChangeRequestService(HttpClient http) : IEvmChangeRequestService
{
    public async Task<IReadOnlyList<EvmChangeRequestDto>> ListAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/evm/change-requests", cancellationToken).ReadListAsync<EvmChangeRequestDto>(cancellationToken);

    public async Task<EvmChangeRequestDto> GetAsync(Guid changeRequestId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/evm/change-requests/{changeRequestId}", cancellationToken)).ReadAsync<EvmChangeRequestDto>(cancellationToken);

    public async Task<EvmChangeRequestDto> CreateAsync(Guid projectId, CreateChangeRequestRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/evm/change-requests", request, cancellationToken)).ReadAsync<EvmChangeRequestDto>(cancellationToken);

    public async Task<EvmChangeRequestDto> ApproveAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/evm/change-requests/{changeRequestId}/approve", request, cancellationToken)).ReadAsync<EvmChangeRequestDto>(cancellationToken);

    public async Task<EvmChangeRequestDto> RejectAsync(Guid changeRequestId, DecideChangeRequestRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/evm/change-requests/{changeRequestId}/reject", request, cancellationToken)).ReadAsync<EvmChangeRequestDto>(cancellationToken);
}

public sealed class HttpEvmExportService(HttpClient http) : IEvmExportService
{
    public async Task<EvmExportFile> ExportFormat1Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url(projectId, "format1", fiscalPeriodId), cancellationToken)).ReadAsync<EvmExportFile>(cancellationToken);

    public async Task<EvmExportFile> ExportFormat5Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Url(projectId, "format5", fiscalPeriodId), cancellationToken)).ReadAsync<EvmExportFile>(cancellationToken);

    private static string Url(Guid projectId, string format, Guid? periodId) =>
        $"api/v1/projects/{projectId}/evm/export/{format}" + (periodId is { } p ? $"?periodId={p}" : "");
}

public static class EvmClientRegistration
{
    public static IServiceCollection AddEvmClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IEvmService, HttpEvmService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IEvmChangeRequestService, HttpEvmChangeRequestService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IEvmExportService, HttpEvmExportService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

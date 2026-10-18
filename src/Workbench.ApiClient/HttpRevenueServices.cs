using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Revenue;

namespace Workbench.ApiClient;

public sealed class HttpRevenueSetupService(HttpClient http) : IRevenueSetupService
{
    private const string Base = "api/v1/revenue";

    public async Task<IReadOnlyList<RevenueProjectSummaryDto>> ListProjectsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects", cancellationToken)).ReadLedgerAsync<List<RevenueProjectSummaryDto>>(cancellationToken);

    public async Task<ProjectRevenueDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/projects/{projectId}", cancellationToken)).ReadLedgerAsync<ProjectRevenueDto>(cancellationToken);

    public async Task<RevenueSetupDto> SaveSetupAsync(SaveRevenueSetupRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/setups", request, cancellationToken)).ReadLedgerAsync<RevenueSetupDto>(cancellationToken);

    public async Task DeleteSetupAsync(Guid setupId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/setups/{setupId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<IReadOnlyList<EstimateDto>> ListEstimatesAsync(EstimateState? status = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/estimates", ("status", status)), cancellationToken)).ReadLedgerAsync<List<EstimateDto>>(cancellationToken);

    public async Task<EstimateDto> SubmitEstimateAsync(SubmitEstimateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/estimates", request, cancellationToken)).ReadLedgerAsync<EstimateDto>(cancellationToken);

    public async Task<EstimateDto> ApproveEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/estimates/{estimateId}/approve", request, cancellationToken)).ReadLedgerAsync<EstimateDto>(cancellationToken);

    public async Task<EstimateDto> RejectEstimateAsync(Guid estimateId, DecideEstimateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/estimates/{estimateId}/reject", request, cancellationToken)).ReadLedgerAsync<EstimateDto>(cancellationToken);

    public async Task<RevenueDeliveryDto> RecordDeliveryAsync(RecordDeliveryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/deliveries", request, cancellationToken)).ReadLedgerAsync<RevenueDeliveryDto>(cancellationToken);
}

public sealed class HttpRevenueRunService(HttpClient http) : IRevenueRunService
{
    private const string Base = "api/v1/revenue";

    public async Task<RevenuePreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/periods/{fiscalPeriodId}/preview", cancellationToken)).ReadLedgerAsync<RevenuePreviewDto>(cancellationToken);

    public async Task<PostRevenueRunResultDto> PostAsync(Guid fiscalPeriodId, PostRevenueRunRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/periods/{fiscalPeriodId}/post", request, cancellationToken)).ReadLedgerAsync<PostRevenueRunResultDto>(cancellationToken);

    public async Task<RevenueRunDto> ReverseAsync(Guid runId, ReverseRevenueRunRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/runs/{runId}/reverse", request, cancellationToken)).ReadLedgerAsync<RevenueRunDto>(cancellationToken);

    public async Task<IReadOnlyList<RevenueRunDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/runs", ("periodId", fiscalPeriodId)), cancellationToken)).ReadLedgerAsync<List<RevenueRunDto>>(cancellationToken);

    public async Task<RevenueRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/runs/{runId}", cancellationToken)).ReadLedgerAsync<RevenueRunDto>(cancellationToken);
}

public sealed class HttpRevenueReportService(HttpClient http) : IRevenueReportService
{
    private const string Base = "api/v1/revenue/reports";

    public async Task<RevenueByPeriodDto> GetRevenueByPeriodAsync(RevenueByPeriodQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/by-period", ("fromPeriodId", query.FromPeriodId), ("toPeriodId", query.ToPeriodId), ("groupBy", query.GroupBy)),
            cancellationToken)).ReadLedgerAsync<RevenueByPeriodDto>(cancellationToken);

    public async Task<RevenueVsBillingsDto> GetRevenueVsBillingsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/revenue-vs-billings/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<RevenueVsBillingsDto>(cancellationToken);

    public async Task<BacklogDto> GetBacklogAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/backlog/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<BacklogDto>(cancellationToken);
}

public static class RevenueClientRegistration
{
    public static IServiceCollection AddRevenueClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRevenueSetupService, HttpRevenueSetupService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IRevenueRunService, HttpRevenueRunService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IRevenueReportService, HttpRevenueReportService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

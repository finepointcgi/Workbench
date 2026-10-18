using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.IncurredCost;

namespace Workbench.ApiClient;

public sealed class HttpIncurredCostService(HttpClient http) : IIncurredCostService
{
    private const string Base = "api/v1/incurred-cost";

    public async Task<IReadOnlyList<IncurredCostSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<List<IncurredCostSummaryDto>>(cancellationToken);

    public async Task<IncurredCostPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{id}", cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> CreateAsync(CreateIncurredCostRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> RegenerateAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{id}/regenerate", null, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> SaveInputsAsync(Guid id, SaveIceInputsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{id}/inputs", request, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> MarkReviewedAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{id}/review", null, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> ReopenAsync(Guid id, ReopenIncurredCostRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/reopen", request, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> CertifyAsync(Guid id, CertifyIncurredCostRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/certify", request, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostPackageDto> SubmitAsync(Guid id, SubmitIncurredCostRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{id}/submit", request, cancellationToken)).ReadLedgerAsync<IncurredCostPackageDto>(cancellationToken);

    public async Task<IncurredCostExportFile> ExportWorkbookAsync(Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{id}/export", cancellationToken)).ReadLedgerAsync<IncurredCostExportFile>(cancellationToken);
}

public static class IncurredCostClientRegistration
{
    public static IServiceCollection AddIncurredCostClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IIncurredCostService, HttpIncurredCostService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

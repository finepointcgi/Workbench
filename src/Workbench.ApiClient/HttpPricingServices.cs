using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Pricing;

namespace Workbench.ApiClient;

public sealed class HttpProposalPricingService(HttpClient http) : IProposalPricingService
{
    private const string Base = "api/v1/pricing";

    public async Task<IReadOnlyList<ProposalSummaryDto>> ListProposalsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/proposals", cancellationToken).ReadListAsync<ProposalSummaryDto>(cancellationToken);

    public async Task<ProposalDto> GetProposalAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/proposals/{proposalId}", cancellationToken)).ReadAsync<ProposalDto>(cancellationToken);

    public async Task<ProposalDto> CreateProposalAsync(SaveProposalRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/proposals", request, cancellationToken)).ReadAsync<ProposalDto>(cancellationToken);

    public async Task<ProposalDto> UpdateProposalAsync(Guid proposalId, SaveProposalRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/proposals/{proposalId}", request, cancellationToken)).ReadAsync<ProposalDto>(cancellationToken);

    public async Task<PriceModelDto> GetModelAsync(Guid modelId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/models/{modelId}", cancellationToken)).ReadAsync<PriceModelDto>(cancellationToken);

    public Task<PriceModelDto> SaveSettingsAsync(Guid modelId, SavePriceSettingsRequest request, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/settings", request, cancellationToken);

    public Task<PriceModelDto> SavePeriodsAsync(Guid modelId, IReadOnlyList<SavePricePeriodRequest> periods, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/periods", periods, cancellationToken);

    public Task<PriceModelDto> SaveClinsAsync(Guid modelId, IReadOnlyList<SavePriceClinRequest> clins, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/clins", clins, cancellationToken);

    public Task<PriceModelDto> SaveWbsAsync(Guid modelId, IReadOnlyList<SavePriceWbsRequest> wbs, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/wbs", wbs, cancellationToken);

    public Task<PriceModelDto> SaveEscalationsAsync(Guid modelId, IReadOnlyList<PriceEscalationDto> escalations, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/escalations", escalations, cancellationToken);

    public Task<PriceModelDto> SaveLaborLinesAsync(Guid modelId, IReadOnlyList<SavePriceLaborLineRequest> lines, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/labor", lines, cancellationToken);

    public Task<PriceModelDto> SaveCostLinesAsync(Guid modelId, IReadOnlyList<SavePriceCostLineRequest> lines, CancellationToken cancellationToken = default) =>
        PutAsync($"{Base}/models/{modelId}/costs", lines, cancellationToken);

    public async Task<PriceResultDto> CalculateAsync(Guid modelId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/models/{modelId}/result", cancellationToken)).ReadAsync<PriceResultDto>(cancellationToken);

    public async Task<PriceModelDto> CopyModelAsync(Guid modelId, CopyPriceModelRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/models/{modelId}/copy", request, cancellationToken)).ReadAsync<PriceModelDto>(cancellationToken);

    public async Task DeleteModelAsync(Guid modelId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/models/{modelId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<PriceModelDto> LockModelAsync(Guid modelId, LockPriceModelRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/models/{modelId}/lock", request, cancellationToken)).ReadAsync<PriceModelDto>(cancellationToken);

    public async Task<ScenarioComparisonDto> CompareAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/proposals/{proposalId}/compare", cancellationToken)).ReadAsync<ScenarioComparisonDto>(cancellationToken);

    public async Task<IReadOnlyList<PricingHistoryDto>> ListHistoryAsync(Guid proposalId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"{Base}/proposals/{proposalId}/history", cancellationToken).ReadListAsync<PricingHistoryDto>(cancellationToken);

    public async Task<PricingOptionsDto> GetOptionsAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/options" + (asOf is { } d ? $"?asOf={d:yyyy-MM-dd}" : ""), cancellationToken)).ReadAsync<PricingOptionsDto>(cancellationToken);

    public async Task<PricingExportFile> ExportCostVolumeAsync(Guid modelId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/models/{modelId}/export", cancellationToken)).ReadAsync<PricingExportFile>(cancellationToken);

    public async Task<HandoffResultDto> HandOffAsync(Guid modelId, HandoffRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/models/{modelId}/handoff", request, cancellationToken)).ReadAsync<HandoffResultDto>(cancellationToken);

    private async Task<PriceModelDto> PutAsync<T>(string url, T body, CancellationToken ct) =>
        await (await http.PutAsJsonAsync(url, body, ct)).ReadAsync<PriceModelDto>(ct);
}

public sealed class HttpPricingOpportunityLookup(HttpClient http) : IPricingOpportunityLookup
{
    public async Task<IReadOnlyList<OpportunityRefDto>> SearchAsync(string? search, CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/pricing/opportunities" + (string.IsNullOrWhiteSpace(search) ? "" : $"?search={Uri.EscapeDataString(search)}"), cancellationToken)
            .ReadListAsync<OpportunityRefDto>(cancellationToken);

    public async Task<OpportunityRefDto?> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"api/v1/pricing/opportunities/{opportunityId}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadAsync<OpportunityRefDto>(cancellationToken);
    }
}

public static class PricingClientRegistration
{
    public static IServiceCollection AddPricingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IProposalPricingService, HttpProposalPricingService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPricingOpportunityLookup, HttpPricingOpportunityLookup>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

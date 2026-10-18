using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;

namespace Workbench.ApiClient;

public sealed class HttpIndirectRateService(HttpClient http) : IIndirectRateService
{
    private const string Root = "api/v1/accounting/indirect-rates";

    public async Task<IReadOnlyList<PoolSetupDto>> ListPoolSetupsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/pools", cancellationToken)).ReadLedgerAsync<List<PoolSetupDto>>(cancellationToken);

    public async Task<PoolSetupDto> SavePoolSetupAsync(CostPool pool, SavePoolSetupRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/pools/{pool}", request, cancellationToken)).ReadLedgerAsync<PoolSetupDto>(cancellationToken);

    public async Task<int> InstallDefaultPoolsAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/pools/install-default", null, cancellationToken)).ReadLedgerAsync<int>(cancellationToken);

    public async Task<IReadOnlyList<IndirectRateDto>> ListRatesAsync(int? fiscalYear = null, bool includeHistory = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/rates", ("fiscalYear", fiscalYear), ("includeHistory", includeHistory ? "true" : null)), cancellationToken))
            .ReadLedgerAsync<List<IndirectRateDto>>(cancellationToken);

    public async Task<IndirectRateDto> SaveRateAsync(SaveIndirectRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/rates", request, cancellationToken)).ReadLedgerAsync<IndirectRateDto>(cancellationToken);

    public async Task<IndirectRateDto> ApproveRateAsync(Guid rateId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/rates/{rateId}/approve", null, cancellationToken)).ReadLedgerAsync<IndirectRateDto>(cancellationToken);

    public async Task WithdrawRateAsync(Guid rateId, WithdrawIndirectRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/rates/{rateId}/withdraw", request, cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<RateCalculationDto> GetRateCalculationAsync(Guid fromPeriodId, Guid toPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Root}/calculation", ("fromPeriodId", fromPeriodId), ("toPeriodId", toPeriodId)), cancellationToken))
            .ReadLedgerAsync<RateCalculationDto>(cancellationToken);

    public async Task<IReadOnlyList<AllocationRunSummaryDto>> ListAllocationRunsAsync(int fiscalYear, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/allocations?fiscalYear={fiscalYear}", cancellationToken)).ReadLedgerAsync<List<AllocationRunSummaryDto>>(cancellationToken);

    public async Task<AllocationPreviewDto> PreviewAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/allocations/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<AllocationPreviewDto>(cancellationToken);

    public async Task<AllocationPreviewDto> PostAllocationAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/allocations/{fiscalPeriodId}/post", null, cancellationToken)).ReadLedgerAsync<AllocationPreviewDto>(cancellationToken);

    public async Task<AllocationPreviewDto> ReverseAllocationAsync(Guid fiscalPeriodId, ReverseAllocationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/allocations/{fiscalPeriodId}/reverse", request, cancellationToken)).ReadLedgerAsync<AllocationPreviewDto>(cancellationToken);

    public async Task<TrueUpDto> GetTrueUpAsync(int fiscalYear, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/true-up/{fiscalYear}", cancellationToken)).ReadLedgerAsync<TrueUpDto>(cancellationToken);

    public async Task<TrueUpDto> PostTrueUpAsync(int fiscalYear, PostTrueUpRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/true-up/{fiscalYear}/post", request, cancellationToken)).ReadLedgerAsync<TrueUpDto>(cancellationToken);
}

public sealed class HttpBurdenedCostService(HttpClient http) : IBurdenedCostService
{
    public async Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"api/v1/projects/{projectId}/burdened-cost", ("from", from), ("to", to), ("basis", basis)), cancellationToken))
            .ReadLedgerAsync<BurdenedCostResult>(cancellationToken);
}

public sealed class HttpBurdenedFinancialsService(HttpClient http) : IBurdenedFinancialsService
{
    public async Task<BurdenedFinancialsDto> GetProjectAsync(Guid projectId, Guid? budgetVersionId = null, Guid? forecastVersionId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"api/v1/projects/{projectId}/financials/burdened", ("budgetVersionId", budgetVersionId), ("forecastVersionId", forecastVersionId)), cancellationToken))
            .ReadLedgerAsync<BurdenedFinancialsDto>(cancellationToken);
}

public static class IndirectRateClientRegistration
{
    public static IServiceCollection AddIndirectRateClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IIndirectRateService, HttpIndirectRateService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBurdenedCostService, HttpBurdenedCostService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBurdenedFinancialsService, HttpBurdenedFinancialsService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

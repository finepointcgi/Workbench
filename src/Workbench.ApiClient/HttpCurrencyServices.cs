using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Currencies;

namespace Workbench.ApiClient;

public sealed class HttpCurrencyService(HttpClient http) : ICurrencyService
{
    private const string Base = "api/v1/currencies";

    public async Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<CurrencyDto>>(cancellationToken);

    public async Task<CurrencyDto> SaveCurrencyAsync(SaveCurrencyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<CurrencyDto>(cancellationToken);

    public async Task<IReadOnlyList<ExchangeRateDto>> ListRatesAsync(ExchangeRateQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/rates", ("currency", query.Currency), ("rateType", query.RateType), ("from", query.From), ("to", query.To), ("take", query.Take)), cancellationToken))
            .ReadLedgerAsync<List<ExchangeRateDto>>(cancellationToken);

    public async Task<ExchangeRateDto> SaveRateAsync(SaveExchangeRateRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/rates", request, cancellationToken)).ReadLedgerAsync<ExchangeRateDto>(cancellationToken);

    public async Task<RateLookupDto> GetRateAsync(string from, string to, DateOnly date, RateKind rateType = RateKind.Spot, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/rates/lookup", ("from", from), ("to", to), ("date", date), ("rateType", rateType)), cancellationToken))
            .ReadLedgerAsync<RateLookupDto>(cancellationToken);

    public async Task<RateImportResultDto> ImportRatesAsync(ImportExchangeRatesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/rates/import", request, cancellationToken)).ReadLedgerAsync<RateImportResultDto>(cancellationToken);

    public async Task<IReadOnlyList<RateProviderDto>> ListRateProvidersAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/rate-providers", cancellationToken)).ReadLedgerAsync<List<RateProviderDto>>(cancellationToken);

    public async Task<RateImportResultDto> FetchRatesAsync(FetchExchangeRatesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/rates/fetch", request, cancellationToken)).ReadLedgerAsync<RateImportResultDto>(cancellationToken);

    public async Task<CurrencySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<CurrencySettingsDto>(cancellationToken);

    public async Task<CurrencySettingsDto> SaveSettingsAsync(SaveCurrencySettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/settings", request, cancellationToken)).ReadLedgerAsync<CurrencySettingsDto>(cancellationToken);

    public async Task<IReadOnlyList<PartyCurrencyDto>> ListPartyCurrenciesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/parties", cancellationToken)).ReadLedgerAsync<List<PartyCurrencyDto>>(cancellationToken);

    public async Task<PartyCurrencyDto> SetPartyCurrencyAsync(SetPartyCurrencyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/parties", request, cancellationToken)).ReadLedgerAsync<PartyCurrencyDto>(cancellationToken);

    public async Task SetInvoiceCurrencyAsync(Guid invoiceId, SetInvoiceCurrencyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/invoices/{invoiceId}", request, cancellationToken)).EnsureLedgerOkAsync(cancellationToken);
}

public sealed class HttpRevaluationService(HttpClient http) : IRevaluationService
{
    private const string Base = "api/v1/currencies";

    public async Task<IReadOnlyList<RevaluationSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/revaluations", cancellationToken)).ReadLedgerAsync<List<RevaluationSummaryDto>>(cancellationToken);

    public async Task<RevaluationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/revaluations/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<RevaluationPreviewDto>(cancellationToken);

    public async Task<RevaluationPreviewDto> PostAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/revaluations/{fiscalPeriodId}", null, cancellationToken)).ReadLedgerAsync<RevaluationPreviewDto>(cancellationToken);

    public async Task<ForeignCurrencyReportDto> GetReportAsync(DateOnly asOf, DateOnly? from = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/report", ("asOf", asOf), ("from", from)), cancellationToken)).ReadLedgerAsync<ForeignCurrencyReportDto>(cancellationToken);
}

public static class CurrencyClientRegistration
{
    public static IServiceCollection AddCurrencyClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICurrencyService, HttpCurrencyService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IRevaluationService, HttpRevaluationService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

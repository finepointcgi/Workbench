using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Currencies;

namespace Workbench.Application.Identity;

// Multicurrency: anyone signed in reads currencies and rates (expense reports need them); Finance and administrators
// change them, see the setup, revalue and read the foreign currency report.

public static class CurrencyPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchCurrencyPermissions(this IServiceCollection services)
    {
        services.Decorate<ICurrencyService, PermissionCheckingCurrencyService>();
        services.Decorate<IRevaluationService, PermissionCheckingRevaluationService>();
        return services;
    }
}

public sealed class PermissionCheckingCurrencyService(ICurrencyService inner, AccessControl access) : ICurrencyService
{
    private const string What = "manage currencies and exchange rates";

    public Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListCurrenciesAsync(includeInactive, cancellationToken);
    }

    public async Task<CurrencyDto> SaveCurrencyAsync(SaveCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveCurrencyAsync(request, cancellationToken);
    }

    public Task<IReadOnlyList<ExchangeRateDto>> ListRatesAsync(ExchangeRateQuery query, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.ListRatesAsync(query, cancellationToken);
    }

    public async Task<ExchangeRateDto> SaveRateAsync(SaveExchangeRateRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveRateAsync(request, cancellationToken);
    }

    public Task<RateLookupDto> GetRateAsync(string from, string to, DateOnly date, RateKind rateType = RateKind.Spot, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        return inner.GetRateAsync(from, to, date, rateType, cancellationToken);
    }

    public async Task<RateImportResultDto> ImportRatesAsync(ImportExchangeRatesRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ImportRatesAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<RateProviderDto>> ListRateProvidersAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListRateProvidersAsync(cancellationToken);
    }

    public async Task<RateImportResultDto> FetchRatesAsync(FetchExchangeRatesRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.FetchRatesAsync(request, cancellationToken);
    }

    public async Task<CurrencySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<CurrencySettingsDto> SaveSettingsAsync(SaveCurrencySettingsRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveSettingsAsync(request, cancellationToken);
    }

    public async Task<IReadOnlyList<PartyCurrencyDto>> ListPartyCurrenciesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListPartyCurrenciesAsync(cancellationToken);
    }

    public async Task<PartyCurrencyDto> SetPartyCurrencyAsync(SetPartyCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SetPartyCurrencyAsync(request, cancellationToken);
    }

    public async Task SetInvoiceCurrencyAsync(Guid invoiceId, SetInvoiceCurrencyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("change an invoice's currency", cancellationToken);
        await inner.SetInvoiceCurrencyAsync(invoiceId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingRevaluationService(IRevaluationService inner, AccessControl access) : IRevaluationService
{
    private const string What = "revalue foreign currency balances";

    public async Task<IReadOnlyList<RevaluationSummaryDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<RevaluationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PreviewAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<RevaluationPreviewDto> PostAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PostAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<ForeignCurrencyReportDto> GetReportAsync(DateOnly asOf, DateOnly? from = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync("see the foreign currency report", cancellationToken);
        return await inner.GetReportAsync(asOf, from, cancellationToken);
    }
}

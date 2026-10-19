namespace Workbench.Contracts.Currencies;

// Multicurrency (Costpoint parity): currencies, exchange rates, the functional currency, and the period-end revaluation
// of open foreign receivables, payables and bank balances. Reading currencies and rates is open to anyone signed in
// (expense reports need them); changing them, revaluation and the foreign currency report are Finance and Admin.
// A rate is "functional units per unit of the foreign currency": 1 EUR = 1.0850 USD is EUR → USD 1.0850.

public enum RateKind { Spot, Average, MonthEnd }

public enum RevaluationKind { Receivable, Payable, Bank }

public sealed record CurrencyDto(string Code, string Name, int Decimals, bool IsActive, bool IsFunctional);

/// <summary>Adds a currency or changes one (by <see cref="Code"/>, an ISO 4217 code).</summary>
public sealed record SaveCurrencyRequest(string Code, string Name, int Decimals = 2, bool IsActive = true);

public sealed record ExchangeRateDto(
    Guid Id,
    string FromCurrency,
    string ToCurrency,
    RateKind RateType,
    DateOnly EffectiveDate,
    decimal Rate,
    string Source,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <summary>Enters a rate. The same pair, type and date again replaces the earlier rate (audited).</summary>
public sealed record SaveExchangeRateRequest(string FromCurrency, string ToCurrency, RateKind RateType, DateOnly EffectiveDate, decimal Rate);

public sealed record ExchangeRateQuery(string? Currency = null, RateKind? RateType = null, DateOnly? From = null, DateOnly? To = null, int Take = 500);

/// <summary>
/// The rate in effect: the latest rate of <see cref="RateType"/> on or before the date (or the inverse pair), falling back
/// to another rate type when there's none (<see cref="IsFallback"/>). <see cref="Rate"/> is 1 for the same currency.
/// </summary>
public sealed record RateLookupDto(string FromCurrency, string ToCurrency, DateOnly Date, decimal Rate, RateKind RateType, DateOnly EffectiveDate, bool IsFallback);

/// <summary>
/// A CSV of rates with a header row: <c>From,To,Type,EffectiveDate,Rate</c> (Type is Spot, Average or MonthEnd; dates
/// yyyy-MM-dd). With <see cref="DryRun"/> nothing is saved.
/// </summary>
public sealed record ImportExchangeRatesRequest(string Csv, bool DryRun = false);

public sealed record RateImportResultDto(int Added, int Replaced, int Unchanged, IReadOnlyList<string> Errors, bool DryRun);

/// <summary>A pluggable source of rates (a bank feed or market data service). None is registered by default.</summary>
public sealed record RateProviderDto(string Name, string Description);

public sealed record FetchExchangeRatesRequest(string Provider, DateOnly Date, RateKind RateType = RateKind.Spot);

public sealed record CurrencySettingsDto(
    string FunctionalCurrency,
    Guid? RealizedGainLossAccountId,
    string? RealizedGainLossAccount,
    Guid? UnrealizedGainLossAccountId,
    string? UnrealizedGainLossAccount,
    bool CanChangeFunctionalCurrency);

/// <summary>
/// Changes the setup. The functional currency can change only while nothing has been posted in another currency.
/// Accounts default to 4920 (realized) and 4930 (unrealized).
/// </summary>
public sealed record SaveCurrencySettingsRequest(string FunctionalCurrency, Guid? RealizedGainLossAccountId = null, Guid? UnrealizedGainLossAccountId = null);

public enum CurrencyPartyKind { Vendor, Customer }

/// <summary>A vendor (invoices us) or customer (we invoice) and the currency its documents default to (null: functional).</summary>
public sealed record PartyCurrencyDto(CurrencyPartyKind Kind, Guid Id, string Code, string Name, string? CurrencyCode);

public sealed record SetPartyCurrencyRequest(CurrencyPartyKind Kind, Guid Id, string? CurrencyCode);

/// <summary>Issues a draft invoice in another currency (null: the functional currency). The rate is fixed when it's approved.</summary>
public sealed record SetInvoiceCurrencyRequest(string? CurrencyCode);

public interface ICurrencyService
{
    /// <summary>Currencies (active only unless <paramref name="includeInactive"/>), the functional currency first.</summary>
    Task<IReadOnlyList<CurrencyDto>> ListCurrenciesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<CurrencyDto> SaveCurrencyAsync(SaveCurrencyRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExchangeRateDto>> ListRatesAsync(ExchangeRateQuery query, CancellationToken cancellationToken = default);

    Task<ExchangeRateDto> SaveRateAsync(SaveExchangeRateRequest request, CancellationToken cancellationToken = default);

    /// <summary>The rate to convert <paramref name="from"/> into <paramref name="to"/> on <paramref name="date"/>; not found when there's none.</summary>
    Task<RateLookupDto> GetRateAsync(string from, string to, DateOnly date, RateKind rateType = RateKind.Spot, CancellationToken cancellationToken = default);

    Task<RateImportResultDto> ImportRatesAsync(ImportExchangeRatesRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RateProviderDto>> ListRateProvidersAsync(CancellationToken cancellationToken = default);

    /// <summary>Asks a registered rate provider for the active currencies' rates on a date and saves them.</summary>
    Task<RateImportResultDto> FetchRatesAsync(FetchExchangeRatesRequest request, CancellationToken cancellationToken = default);

    Task<CurrencySettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<CurrencySettingsDto> SaveSettingsAsync(SaveCurrencySettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Vendors and customers with their document currency.</summary>
    Task<IReadOnlyList<PartyCurrencyDto>> ListPartyCurrenciesAsync(CancellationToken cancellationToken = default);

    Task<PartyCurrencyDto> SetPartyCurrencyAsync(SetPartyCurrencyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes the currency a draft invoice will be issued in.</summary>
    Task SetInvoiceCurrencyAsync(Guid invoiceId, SetInvoiceCurrencyRequest request, CancellationToken cancellationToken = default);
}

// ---- Revaluation and the foreign currency report ----

public sealed record RevaluationLineDto(
    RevaluationKind Kind,
    Guid DocumentId,
    string DocumentNumber,
    string Party,
    string CurrencyCode,
    decimal TransactionBalance,
    decimal CarryingAmount,
    decimal Rate,
    decimal RevaluedAmount,
    decimal GainLoss,
    string AccountNumber);

/// <summary>
/// What a revaluation of the period would post (or did post): every open foreign balance at the period's last day, revalued
/// at the month-end rate. <see cref="Warnings"/> lists missing rates (those balances are skipped) and fallbacks.
/// </summary>
public sealed record RevaluationPreviewDto(
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly RateDate,
    DateOnly ReverseOn,
    string FunctionalCurrency,
    IReadOnlyList<RevaluationLineDto> Lines,
    decimal TotalGain,
    decimal TotalLoss,
    decimal NetGainLoss,
    IReadOnlyList<string> Warnings,
    bool IsPosted,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    Guid? ReversalEntryId,
    string? ReversalEntryNumber,
    string? PostedBy,
    DateTime? PostedAtUtc,
    bool CanPost);

public sealed record RevaluationSummaryDto(
    Guid Id,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly RateDate,
    decimal NetGainLoss,
    string JournalEntryNumber,
    string? ReversalEntryNumber,
    string PostedBy,
    DateTime PostedAtUtc);

public sealed record ForeignCurrencyExposureDto(
    string CurrencyCode,
    decimal ReceivableBalance,
    decimal PayableBalance,
    decimal BankBalance,
    decimal NetExposure,
    decimal? Rate,
    decimal? NetFunctional);

public sealed record ForeignDocumentDto(
    RevaluationKind Kind,
    Guid DocumentId,
    string DocumentNumber,
    string Party,
    DateOnly Date,
    string CurrencyCode,
    decimal DocumentRate,
    decimal TransactionAmount,
    decimal TransactionBalance,
    decimal FunctionalBalance,
    decimal? CurrentRate,
    decimal? CurrentValue,
    decimal? UnrealizedGainLoss);

public sealed record RealizedGainLossDto(DateOnly Date, string Document, string Party, string CurrencyCode, decimal TransactionAmount, decimal DocumentRate, decimal PaymentRate, decimal GainLoss, string? JournalEntryNumber);

/// <summary>
/// Open foreign balances as of a date by currency and document (valued at the newest rate on that date, month-end first), plus
/// realized gains and losses on payments between <see cref="From"/> and <see cref="AsOf"/>.
/// </summary>
public sealed record ForeignCurrencyReportDto(
    DateOnly From,
    DateOnly AsOf,
    string FunctionalCurrency,
    IReadOnlyList<ForeignCurrencyExposureDto> ByCurrency,
    IReadOnlyList<ForeignDocumentDto> Documents,
    IReadOnlyList<RealizedGainLossDto> Realized,
    decimal RealizedTotal,
    decimal UnrealizedTotal,
    IReadOnlyList<string> Warnings);

public interface IRevaluationService
{
    Task<IReadOnlyList<RevaluationSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>What revaluing the period would post; for a period already revalued, what was posted.</summary>
    Task<RevaluationPreviewDto> PreviewAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts the revaluation on the period's last day (each balance's gain or loss against the unrealized gain/loss account)
    /// and its reversal on the next period's first day. Once per period; both periods must be open.
    /// </summary>
    Task<RevaluationPreviewDto> PostAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<ForeignCurrencyReportDto> GetReportAsync(DateOnly asOf, DateOnly? from = null, CancellationToken cancellationToken = default);
}

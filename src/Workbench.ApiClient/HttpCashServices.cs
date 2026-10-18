using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Cash;

namespace Workbench.ApiClient;

public sealed class HttpBankAccountService(HttpClient http) : IBankAccountService
{
    private const string Base = "api/v1/cash/accounts";

    public async Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<BankAccountDto>>(cancellationToken);

    public async Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/{bankAccountId}", cancellationToken)).ReadLedgerAsync<BankAccountDto>(cancellationToken);

    public async Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<BankAccountDto>(cancellationToken);

    public async Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/{bankAccountId}", request, cancellationToken)).ReadLedgerAsync<BankAccountDto>(cancellationToken);

    public async Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/{bankAccountId}/reveal", null, cancellationToken)).ReadLedgerAsync<BankAccountNumberDto>(cancellationToken);

    public async Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/gl-accounts", cancellationToken)).ReadLedgerAsync<List<CashGlAccountDto>>(cancellationToken);
}

public sealed class HttpBankStatementService(HttpClient http) : IBankStatementService
{
    private const string Base = "api/v1/cash";

    public async Task<StatementImportResultDto> ImportAsync(ImportStatementRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/statements", request, cancellationToken)).ReadLedgerAsync<StatementImportResultDto>(cancellationToken);

    public async Task<IReadOnlyList<StatementImportDto>> ListImportsAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/statements", ("bankAccountId", bankAccountId)), cancellationToken)).ReadLedgerAsync<List<StatementImportDto>>(cancellationToken);

    public async Task<IReadOnlyList<BankItemDto>> ListItemsAsync(Guid bankAccountId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/accounts/{bankAccountId}/items", ("from", from), ("to", to)), cancellationToken)).ReadLedgerAsync<List<BankItemDto>>(cancellationToken);

    public async Task<BankAccountDto> SaveCsvMappingAsync(Guid bankAccountId, CsvColumnMapping mapping, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/accounts/{bankAccountId}/csv-mapping", mapping, cancellationToken)).ReadLedgerAsync<BankAccountDto>(cancellationToken);
}

public sealed class HttpBankReconciliationService(HttpClient http) : IBankReconciliationService
{
    private const string Base = "api/v1/cash";

    public async Task<IReadOnlyList<BankReconciliationDto>> ListAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/reconciliations", ("bankAccountId", bankAccountId)), cancellationToken)).ReadLedgerAsync<List<BankReconciliationDto>>(cancellationToken);

    public async Task<ReconciliationDefaultsDto> GetDefaultsAsync(Guid bankAccountId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/reconciliations/defaults/{bankAccountId}", cancellationToken)).ReadLedgerAsync<ReconciliationDefaultsDto>(cancellationToken);

    public async Task<BankReconciliationDto> StartAsync(StartReconciliationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/reconciliations", request, cancellationToken)).ReadLedgerAsync<BankReconciliationDto>(cancellationToken);

    public async Task<BankReconciliationDto> UpdateAsync(Guid reconciliationId, UpdateReconciliationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/reconciliations/{reconciliationId}", request, cancellationToken)).ReadLedgerAsync<BankReconciliationDto>(cancellationToken);

    public async Task<ReconcileWorkspaceDto> GetWorkspaceAsync(Guid reconciliationId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/reconciliations/{reconciliationId}/workspace", cancellationToken)).ReadLedgerAsync<ReconcileWorkspaceDto>(cancellationToken);

    public async Task<AutoMatchResultDto> AutoMatchAsync(AutoMatchRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/matches/auto", request, cancellationToken)).ReadLedgerAsync<AutoMatchResultDto>(cancellationToken);

    public async Task<BankMatchDto> MatchAsync(MatchItemsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/matches", request, cancellationToken)).ReadLedgerAsync<BankMatchDto>(cancellationToken);

    public async Task UnmatchAsync(Guid matchId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/matches/{matchId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<BookingAccountsDto> GetBookingAccountsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/booking-accounts", cancellationToken)).ReadLedgerAsync<BookingAccountsDto>(cancellationToken);

    public async Task<BankMatchDto> BookBankItemAsync(BookBankItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/bank-items/book", request, cancellationToken)).ReadLedgerAsync<BankMatchDto>(cancellationToken);

    public async Task<BankReconciliationDto> CompleteAsync(Guid reconciliationId, CompleteReconciliationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/reconciliations/{reconciliationId}/complete", request, cancellationToken)).ReadLedgerAsync<BankReconciliationDto>(cancellationToken);

    public async Task<BankReconciliationDto> ReopenAsync(Guid reconciliationId, ReconciliationReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/reconciliations/{reconciliationId}/reopen", request, cancellationToken)).ReadLedgerAsync<BankReconciliationDto>(cancellationToken);

    public async Task<ReconciliationReportDto> GetReportAsync(Guid reconciliationId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/reconciliations/{reconciliationId}/report", cancellationToken)).ReadLedgerAsync<ReconciliationReportDto>(cancellationToken);

    public async Task<IReadOnlyList<BankCloseWaiverDto>> ListCloseWaiversAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/close-waivers", ("fiscalPeriodId", fiscalPeriodId)), cancellationToken)).ReadLedgerAsync<List<BankCloseWaiverDto>>(cancellationToken);

    public async Task<BankCloseWaiverDto> WaiveCloseCheckAsync(WaiveBankCloseCheckRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/close-waivers", request, cancellationToken)).ReadLedgerAsync<BankCloseWaiverDto>(cancellationToken);
}

public sealed class HttpCashForecastService(HttpClient http) : ICashForecastService
{
    private const string Base = "api/v1/cash";

    public async Task<CashPositionDto> GetPositionAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/position", cancellationToken)).ReadLedgerAsync<CashPositionDto>(cancellationToken);

    public async Task<CashForecastDto> GetForecastAsync(int weeks = 13, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/forecast?weeks={weeks}", cancellationToken)).ReadLedgerAsync<CashForecastDto>(cancellationToken);

    public async Task<IReadOnlyList<ForecastItemDto>> ListItemsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/forecast/items", cancellationToken)).ReadLedgerAsync<List<ForecastItemDto>>(cancellationToken);

    public async Task<ForecastItemDto> CreateItemAsync(SaveForecastItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/forecast/items", request, cancellationToken)).ReadLedgerAsync<ForecastItemDto>(cancellationToken);

    public async Task<ForecastItemDto> UpdateItemAsync(Guid itemId, SaveForecastItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/forecast/items/{itemId}", request, cancellationToken)).ReadLedgerAsync<ForecastItemDto>(cancellationToken);

    public async Task<CashSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/settings", cancellationToken)).ReadLedgerAsync<CashSettingsDto>(cancellationToken);

    public async Task<CashSettingsDto> SaveSettingsAsync(CashSettingsDto settings, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/settings", settings, cancellationToken)).ReadLedgerAsync<CashSettingsDto>(cancellationToken);
}

public static class CashClientRegistration
{
    public static IServiceCollection AddCashClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IBankAccountService, HttpBankAccountService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBankStatementService, HttpBankStatementService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBankReconciliationService, HttpBankReconciliationService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICashForecastService, HttpCashForecastService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

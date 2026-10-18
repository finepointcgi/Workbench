using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Accounting;

namespace Workbench.ApiClient;

public sealed class HttpChartOfAccountsService(HttpClient http) : IChartOfAccountsService
{
    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/accounts?includeInactive={includeInactive}", cancellationToken)).ReadLedgerAsync<List<AccountDto>>(cancellationToken);

    public async Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/accounting/accounts", request, cancellationToken)).ReadLedgerAsync<AccountDto>(cancellationToken);

    public async Task<AccountDto> UpdateAccountAsync(Guid accountId, SaveAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/accounting/accounts/{accountId}", request, cancellationToken)).ReadLedgerAsync<AccountDto>(cancellationToken);

    public async Task<AccountDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/accounting/accounts/{accountId}/active?isActive={isActive}", null, cancellationToken)).ReadLedgerAsync<AccountDto>(cancellationToken);

    public async Task<int> InstallDefaultChartAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync("api/v1/accounting/accounts/install-default", null, cancellationToken)).ReadLedgerAsync<int>(cancellationToken);
}

public sealed class HttpJournalService(HttpClient http) : IJournalService
{
    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        var url = LedgerUrls.With("api/v1/accounting/journal",
            ("periodId", query.FiscalPeriodId), ("status", query.Status), ("source", query.Source), ("search", query.Search), ("take", query.Take));
        return await (await http.GetAsync(url, cancellationToken)).ReadLedgerAsync<List<JournalEntrySummaryDto>>(cancellationToken);
    }

    public async Task<JournalEntryDto> GetAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/journal/{entryId}", cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);

    public async Task<JournalEntryDto> CreateDraftAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/accounting/journal", request, cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);

    public async Task<JournalEntryDto> UpdateDraftAsync(Guid entryId, SaveJournalEntryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/accounting/journal/{entryId}", request, cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/accounting/journal/{entryId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<JournalEntryDto> PostAsync(Guid entryId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/accounting/journal/{entryId}/post", null, cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);

    public async Task<JournalEntryDto> CreateAndPostAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/accounting/journal/post", request, cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);

    public async Task<JournalEntryDto> ReverseAsync(Guid entryId, ReverseJournalEntryRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/accounting/journal/{entryId}/reverse", request, cancellationToken)).ReadLedgerAsync<JournalEntryDto>(cancellationToken);
}

public sealed class HttpPostingService(HttpClient http) : IPostingService
{
    public async Task<PostingSetupDto> GetSetupAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/accounting/posting/setup", cancellationToken)).ReadLedgerAsync<PostingSetupDto>(cancellationToken);

    public async Task<PostingRuleDto> SaveRuleAsync(SavePostingRuleRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/accounting/posting/rules", request, cancellationToken)).ReadLedgerAsync<PostingRuleDto>(cancellationToken);

    public async Task<WbsPoolDto> SetWbsPoolAsync(Guid wbsElementId, SetWbsPoolRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/accounting/posting/pools/{wbsElementId}", request, cancellationToken)).ReadLedgerAsync<WbsPoolDto>(cancellationToken);

    public async Task<IReadOnlyList<UnpostedItemDto>> ListUnpostedAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With("api/v1/accounting/posting/unposted", ("periodId", fiscalPeriodId)), cancellationToken))
            .ReadLedgerAsync<List<UnpostedItemDto>>(cancellationToken);

    public async Task<PostingResultDto> PostBacklogAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync("api/v1/accounting/posting/backlog", null, cancellationToken)).ReadLedgerAsync<PostingResultDto>(cancellationToken);

    public async Task<PostingResultDto> PostTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/accounting/posting/timesheets/{timesheetId}", null, cancellationToken)).ReadLedgerAsync<PostingResultDto>(cancellationToken);

    public async Task<PostingResultDto> PostOdcAsync(Guid odcId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/accounting/posting/odcs/{odcId}", null, cancellationToken)).ReadLedgerAsync<PostingResultDto>(cancellationToken);
}

public sealed class HttpPeriodCloseService(HttpClient http) : IPeriodCloseService
{
    public async Task<PeriodCloseChecklistDto> GetChecklistAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/periods/{fiscalPeriodId}/close", cancellationToken)).ReadLedgerAsync<PeriodCloseChecklistDto>(cancellationToken);

    public async Task<PeriodCloseChecklistDto> CloseAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/accounting/periods/{fiscalPeriodId}/close", request, cancellationToken)).ReadLedgerAsync<PeriodCloseChecklistDto>(cancellationToken);

    public async Task<PeriodCloseChecklistDto> ReopenAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/accounting/periods/{fiscalPeriodId}/reopen", request, cancellationToken)).ReadLedgerAsync<PeriodCloseChecklistDto>(cancellationToken);
}

public sealed class HttpAccountingReportService(HttpClient http) : IAccountingReportService
{
    public async Task<TrialBalanceDto> GetTrialBalanceAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/reports/trial-balance/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<TrialBalanceDto>(cancellationToken);

    public async Task<LedgerDetailDto> GetLedgerDetailAsync(LedgerQuery query, CancellationToken cancellationToken = default)
    {
        var url = LedgerUrls.With("api/v1/accounting/reports/ledger", ("accountId", query.AccountId), ("from", query.From), ("to", query.To),
            ("orgUnitId", query.OrgUnitId), ("projectId", query.ProjectId), ("wbsElementId", query.WbsElementId), ("take", query.Take));
        return await (await http.GetAsync(url, cancellationToken)).ReadLedgerAsync<LedgerDetailDto>(cancellationToken);
    }

    public async Task<FinancialStatementDto> GetIncomeStatementAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/reports/income-statement/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<FinancialStatementDto>(cancellationToken);

    public async Task<FinancialStatementDto> GetBalanceSheetAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/accounting/reports/balance-sheet/{fiscalPeriodId}", cancellationToken)).ReadLedgerAsync<FinancialStatementDto>(cancellationToken);

    public async Task<IReadOnlyList<ProjectCostRowDto>> GetProjectCostLedgerAsync(Guid? projectId = null, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With("api/v1/accounting/reports/project-cost", ("projectId", projectId), ("periodId", fiscalPeriodId)), cancellationToken))
            .ReadLedgerAsync<List<ProjectCostRowDto>>(cancellationToken);

    public async Task<ReconciliationDto> GetReconciliationAsync(Guid? projectId = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With("api/v1/accounting/reports/reconciliation", ("projectId", projectId)), cancellationToken))
            .ReadLedgerAsync<ReconciliationDto>(cancellationToken);

    public async Task<AccountingExportFile> ExportCsvAsync(AccountingExportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/accounting/reports/export", request, cancellationToken)).ReadLedgerAsync<AccountingExportFile>(cancellationToken);
}

internal static class LedgerUrls
{
    public static string With(string path, params (string Name, object? Value)[] query)
    {
        var parts = query.Where(q => q.Value is not null)
            .Select(q => $"{q.Name}={Uri.EscapeDataString(q.Value switch
            {
                DateOnly d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                var v => v!.ToString()!,
            })}")
            .ToList();
        return parts.Count == 0 ? path : $"{path}?{string.Join('&', parts)}";
    }

    /// <summary>Like <see cref="ApiResponses.ReadAsync{T}"/>, plus 409 → <see cref="InvalidOperationException"/> (wrong state).</summary>
    public static async Task<T> ReadLedgerAsync<T>(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await ThrowConflictAsync(response, cancellationToken);
            return await response.ReadAsync<T>(cancellationToken);
        }
    }

    public static async Task EnsureLedgerOkAsync(this HttpResponseMessage response, CancellationToken cancellationToken)
    {
        using (response)
        {
            await ThrowConflictAsync(response, cancellationToken);
            await response.EnsureOkAsync(cancellationToken);
        }
    }

    private static async Task ThrowConflictAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.StatusCode is HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException(await response.Content.ReadAsStringAsync(cancellationToken));
        }
    }
}

public static class AccountingClientRegistration
{
    public static IServiceCollection AddAccountingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IChartOfAccountsService, HttpChartOfAccountsService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IJournalService, HttpJournalService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPostingService, HttpPostingService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPeriodCloseService, HttpPeriodCloseService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAccountingReportService, HttpAccountingReportService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

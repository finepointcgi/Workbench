using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Accounting;

namespace Workbench.Application.Identity;

// Finance-or-Admin checks in front of every general-ledger service. Automatic posting from approvals doesn't go
// through these: the posting decorators call the unchecked PostingEngine for whoever approved.

public static class AccountingPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchAccountingPermissions(this IServiceCollection services)
    {
        services.Decorate<IChartOfAccountsService, PermissionCheckingChartOfAccountsService>();
        services.Decorate<IJournalService, PermissionCheckingJournalService>();
        services.Decorate<IPostingService, PermissionCheckingPostingService>();
        services.Decorate<IPeriodCloseService, PermissionCheckingPeriodCloseService>();
        services.Decorate<IAccountingReportService, PermissionCheckingAccountingReportService>();
        return services;
    }
}

public sealed class PermissionCheckingChartOfAccountsService(IChartOfAccountsService inner, AccessControl access) : IChartOfAccountsService
{
    private const string What = "use the chart of accounts";

    public async Task<IReadOnlyList<AccountDto>> ListAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAccountsAsync(includeInactive, cancellationToken);
    }

    public async Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CreateAccountAsync(request, cancellationToken);
    }

    public async Task<AccountDto> UpdateAccountAsync(Guid accountId, SaveAccountRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.UpdateAccountAsync(accountId, request, cancellationToken);
    }

    public async Task<AccountDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SetActiveAsync(accountId, isActive, cancellationToken);
    }

    public async Task<int> InstallDefaultChartAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.InstallDefaultChartAsync(cancellationToken);
    }
}

public sealed class PermissionCheckingJournalService(IJournalService inner, AccessControl access) : IJournalService
{
    private const string What = "work with journal entries";

    public async Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListAsync(query, cancellationToken);
    }

    public async Task<JournalEntryDto> GetAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateDraftAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CreateDraftAsync(request, cancellationToken);
    }

    public async Task<JournalEntryDto> UpdateDraftAsync(Guid entryId, SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.UpdateDraftAsync(entryId, request, cancellationToken);
    }

    public async Task DeleteDraftAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        await inner.DeleteDraftAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> PostAsync(Guid entryId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PostAsync(entryId, cancellationToken);
    }

    public async Task<JournalEntryDto> CreateAndPostAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CreateAndPostAsync(request, cancellationToken);
    }

    public async Task<JournalEntryDto> ReverseAsync(Guid entryId, ReverseJournalEntryRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ReverseAsync(entryId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingPostingService(IPostingService inner, AccessControl access) : IPostingService
{
    private const string What = "manage general-ledger posting";

    public async Task<PostingSetupDto> GetSetupAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetSetupAsync(cancellationToken);
    }

    public async Task<PostingRuleDto> SaveRuleAsync(SavePostingRuleRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SaveRuleAsync(request, cancellationToken);
    }

    public async Task<WbsPoolDto> SetWbsPoolAsync(Guid wbsElementId, SetWbsPoolRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.SetWbsPoolAsync(wbsElementId, request, cancellationToken);
    }

    public async Task<IReadOnlyList<UnpostedItemDto>> ListUnpostedAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ListUnpostedAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<PostingResultDto> PostBacklogAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PostBacklogAsync(cancellationToken);
    }

    public async Task<PostingResultDto> PostTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PostTimesheetAsync(timesheetId, cancellationToken);
    }

    public async Task<PostingResultDto> PostOdcAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.PostOdcAsync(odcId, cancellationToken);
    }
}

public sealed class PermissionCheckingPeriodCloseService(IPeriodCloseService inner, AccessControl access) : IPeriodCloseService
{
    private const string What = "close fiscal periods";

    public async Task<PeriodCloseChecklistDto> GetChecklistAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetChecklistAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<PeriodCloseChecklistDto> CloseAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.CloseAsync(fiscalPeriodId, request, cancellationToken);
    }

    public async Task<PeriodCloseChecklistDto> ReopenAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ReopenAsync(fiscalPeriodId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingAccountingReportService(IAccountingReportService inner, AccessControl access) : IAccountingReportService
{
    private const string What = "see accounting reports";

    public async Task<TrialBalanceDto> GetTrialBalanceAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetTrialBalanceAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<LedgerDetailDto> GetLedgerDetailAsync(LedgerQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetLedgerDetailAsync(query, cancellationToken);
    }

    public async Task<FinancialStatementDto> GetIncomeStatementAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetIncomeStatementAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<FinancialStatementDto> GetBalanceSheetAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetBalanceSheetAsync(fiscalPeriodId, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectCostRowDto>> GetProjectCostLedgerAsync(Guid? projectId = null, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetProjectCostLedgerAsync(projectId, fiscalPeriodId, cancellationToken);
    }

    public async Task<ReconciliationDto> GetReconciliationAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.GetReconciliationAsync(projectId, cancellationToken);
    }

    public async Task<AccountingExportFile> ExportCsvAsync(AccountingExportRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireFinanceAsync(What, cancellationToken);
        return await inner.ExportCsvAsync(request, cancellationToken);
    }
}

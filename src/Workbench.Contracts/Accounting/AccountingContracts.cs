namespace Workbench.Contracts.Accounting;

// General ledger contracts. Every later accounting module (indirect rates, billing/AR, AP, payroll) posts into the
// ledger through IJournalService (manual or system entries with their own JournalEntrySource) and reads it through
// IAccountingReportService. All of these services are for Finance and administrators only.

public enum AccountKind { Asset, Liability, Equity, Revenue, Expense }

public enum BalanceSide { Debit, Credit }

public enum CostClass { None, DirectLabor, DirectOdc, IndirectLabor, Fringe, Overhead, GeneralAndAdministrative, Unallowable, Other, AppliedBurden }

public enum JournalEntrySource { Manual, Timesheet, Odc, Payroll, Billing, AP, Allocation, Reversal }

public enum JournalEntryState { Draft, Posted, Reversed }

public enum PostingRuleType { DirectLabor, IndirectLabor, LeaveLabor, Odc, UnallowableOdc, AccruedPayroll, AccountsPayable, AccruedReimbursement }

public enum CostPool { Fringe, Overhead, GeneralAndAdministrative }

// ---- Chart of accounts ----

/// <summary>An account. <see cref="HasChildren"/> accounts are summary accounts: they roll up and take no postings.</summary>
public sealed record AccountDto(
    Guid Id,
    string Number,
    string Name,
    AccountKind Type,
    BalanceSide NormalBalance,
    bool IsActive,
    Guid? ParentId,
    CostClass CostClass,
    string? Description,
    bool HasChildren,
    bool HasPostings);

/// <summary>
/// Creates or changes an account. <see cref="NormalBalance"/> defaults from the type (debit for assets and expenses).
/// <see cref="CostClass"/> applies to expense accounts only.
/// </summary>
public sealed record SaveAccountRequest(
    string Number,
    string Name,
    AccountKind Type,
    Guid? ParentId = null,
    CostClass CostClass = CostClass.None,
    BalanceSide? NormalBalance = null,
    string? Description = null);

public interface IChartOfAccountsService
{
    /// <summary>All accounts ordered by number (active only unless <paramref name="includeInactive"/>).</summary>
    Task<IReadOnlyList<AccountDto>> ListAccountsAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<AccountDto> CreateAccountAsync(SaveAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes an account. Its type can't change once it has postings.</summary>
    Task<AccountDto> UpdateAccountAsync(Guid accountId, SaveAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deactivates or reactivates. An account used by a posting rule or with active children can't be deactivated.</summary>
    Task<AccountDto> SetActiveAsync(Guid accountId, bool isActive, CancellationToken cancellationToken = default);

    /// <summary>Installs the default GovCon services chart and posting rules when the chart is empty; returns the accounts created.</summary>
    Task<int> InstallDefaultChartAsync(CancellationToken cancellationToken = default);
}

// ---- Journal entries ----

public sealed record JournalLineDto(
    Guid Id,
    int LineNumber,
    Guid AccountId,
    string AccountNumber,
    string AccountName,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    decimal Debit,
    decimal Credit,
    string? Memo);

public sealed record JournalEntrySummaryDto(
    Guid Id,
    string Number,
    DateOnly Date,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    JournalEntrySource Source,
    Guid? SourceId,
    string Description,
    JournalEntryState Status,
    decimal TotalDebit,
    decimal TotalCredit,
    string CreatedBy,
    string? PostedBy,
    DateTime? PostedAtUtc,
    Guid? ReversalOfId,
    Guid? ReversedById);

public sealed record JournalEntryDto(JournalEntrySummaryDto Entry, IReadOnlyList<JournalLineDto> Lines, string? ReversalReason, int SourceRevision);

public sealed record JournalLineInput(
    Guid AccountId,
    decimal Debit,
    decimal Credit,
    string? Memo = null,
    Guid? OrgUnitId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null);

/// <summary>A manual entry (or a system entry from another accounting module, with its own source and source id).</summary>
public sealed record SaveJournalEntryRequest(
    DateOnly Date,
    string Description,
    IReadOnlyList<JournalLineInput> Lines,
    JournalEntrySource Source = JournalEntrySource.Manual,
    Guid? SourceId = null);

/// <summary>Reverses a posted entry. The reversal is dated <see cref="Date"/> (default: the original date) in an open period.</summary>
public sealed record ReverseJournalEntryRequest(string Reason, DateOnly? Date = null);

public sealed record JournalQuery(
    Guid? FiscalPeriodId = null,
    JournalEntryState? Status = null,
    JournalEntrySource? Source = null,
    string? Search = null,
    int Take = 200);

public interface IJournalService
{
    Task<IReadOnlyList<JournalEntrySummaryDto>> ListAsync(JournalQuery query, CancellationToken cancellationToken = default);

    Task<JournalEntryDto> GetAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>Saves a draft. Drafts may be unbalanced; posting checks balance.</summary>
    Task<JournalEntryDto> CreateDraftAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces a draft's date, description and lines. Posted entries are immutable.</summary>
    Task<JournalEntryDto> UpdateDraftAsync(Guid entryId, SaveJournalEntryRequest request, CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>Posts a draft: it must balance to the cent and its date must be in an open fiscal period. Audited.</summary>
    Task<JournalEntryDto> PostAsync(Guid entryId, CancellationToken cancellationToken = default);

    /// <summary>Creates and posts an entry in one step (what other accounting modules call).</summary>
    Task<JournalEntryDto> CreateAndPostAsync(SaveJournalEntryRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts a reversing entry and marks the original reversed. Entries posted from timesheets and ODCs are reversed by
    /// reopening their source instead. Audited.
    /// </summary>
    Task<JournalEntryDto> ReverseAsync(Guid entryId, ReverseJournalEntryRequest request, CancellationToken cancellationToken = default);
}

// ---- Posting (subledgers into the ledger) ----

public sealed record PostingRuleDto(PostingRuleType Kind, string Key, string Label, Guid? AccountId, string? AccountNumber, string? AccountName);

public sealed record WbsPoolDto(Guid WbsElementId, string ProjectCode, string WbsCode, string WbsName, string Kind, CostPool? Pool, CostPool EffectivePool, bool Inherited);

public sealed record PostingSetupDto(IReadOnlyList<PostingRuleDto> Rules, IReadOnlyList<WbsPoolDto> IndirectWbs);

public sealed record SavePostingRuleRequest(PostingRuleType Kind, string? Key, Guid AccountId);

public sealed record SetWbsPoolRequest(CostPool? Pool);

/// <summary>
/// An approved source record without its journal entry (<see cref="Problem"/> says why, when posting failed), or a
/// posted entry whose source is no longer approved (<see cref="IsStale"/>, needs a reversal).
/// </summary>
public sealed record UnpostedItemDto(
    JournalEntrySource Source,
    Guid SourceId,
    string Description,
    DateOnly Date,
    decimal Amount,
    bool IsStale,
    string? Problem);

public sealed record PostingResultDto(int EntriesPosted, int EntriesReversed, int AlreadyPosted, IReadOnlyList<string> Problems);

public interface IPostingService
{
    Task<PostingSetupDto> GetSetupAsync(CancellationToken cancellationToken = default);

    Task<PostingRuleDto> SaveRuleAsync(SavePostingRuleRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sets (or clears, with null) the indirect pool of an indirect or leave WBS element; children inherit it.</summary>
    Task<WbsPoolDto> SetWbsPoolAsync(Guid wbsElementId, SetWbsPoolRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approved timesheets and ODCs without a journal entry, and entries whose source was reopened, optionally in one period.</summary>
    Task<IReadOnlyList<UnpostedItemDto>> ListUnpostedAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts every approved item that has no entry and reverses entries whose source was reopened. Items in closed periods
    /// post into the earliest open period instead. Idempotent: running it twice posts nothing new.
    /// </summary>
    Task<PostingResultDto> PostBacklogAsync(CancellationToken cancellationToken = default);

    /// <summary>Posts one approved timesheet's labor (no-op when already posted for its revision).</summary>
    Task<PostingResultDto> PostTimesheetAsync(Guid timesheetId, CancellationToken cancellationToken = default);

    /// <summary>Posts one approved ODC or expense line (no-op when already posted).</summary>
    Task<PostingResultDto> PostOdcAsync(Guid odcId, CancellationToken cancellationToken = default);
}

// ---- Period close ----

public enum CloseCheckKind { TimesheetsApproved, OdcsDecided, NothingUnposted, TrialBalanceBalanced, AllocationPosted, UnbilledAccrual }

public sealed record CloseCheckDto(CloseCheckKind Kind, string Title, bool Passed, int Count, string Summary, IReadOnlyList<string> Details);

public sealed record PeriodCloseChecklistDto(
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly Start,
    DateOnly End,
    bool IsClosed,
    IReadOnlyList<CloseCheckDto> Checks)
{
    public bool CanClose => !IsClosed && Checks.All(c => c.Passed);
}

public sealed record ClosePeriodRequest(string Reason);

public interface IPeriodCloseService
{
    Task<PeriodCloseChecklistDto> GetChecklistAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Closes the period when every check passes (otherwise <see cref="InvalidOperationException"/>). A closed period
    /// rejects postings and any time or ODC change dated in it. Audited.
    /// </summary>
    Task<PeriodCloseChecklistDto> CloseAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reopens a closed period (reason required, audited).</summary>
    Task<PeriodCloseChecklistDto> ReopenAsync(Guid fiscalPeriodId, ClosePeriodRequest request, CancellationToken cancellationToken = default);
}

// ---- Reports ----

/// <summary>
/// One account's line on the trial balance: balance before the period (balance-sheet accounts since inception, income
/// statement accounts since the start of the fiscal year), the period's activity and the ending balance.
/// Balances are signed debit-positive.
/// </summary>
public sealed record TrialBalanceRowDto(
    Guid AccountId,
    string AccountNumber,
    string AccountName,
    AccountKind Type,
    decimal Opening,
    decimal PeriodDebit,
    decimal PeriodCredit,
    decimal Ending)
{
    public decimal EndingDebit => Ending > 0 ? Ending : 0;

    public decimal EndingCredit => Ending < 0 ? -Ending : 0;
}

public sealed record TrialBalanceDto(
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    IReadOnlyList<TrialBalanceRowDto> Rows,
    decimal TotalPeriodDebit,
    decimal TotalPeriodCredit,
    decimal TotalEndingDebit,
    decimal TotalEndingCredit)
{
    public bool IsBalanced => TotalPeriodDebit == TotalPeriodCredit && TotalEndingDebit == TotalEndingCredit;
}

public sealed record LedgerQuery(
    Guid? AccountId = null,
    DateOnly? From = null,
    DateOnly? To = null,
    Guid? OrgUnitId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null,
    int Take = 2000);

public sealed record LedgerLineDto(
    Guid JournalEntryId,
    string EntryNumber,
    DateOnly Date,
    string PeriodName,
    JournalEntrySource Source,
    string Description,
    string AccountNumber,
    string AccountName,
    string? OrgUnitCode,
    string? ProjectCode,
    string? WbsCode,
    decimal Debit,
    decimal Credit,
    decimal RunningBalance,
    string? Memo);

public sealed record LedgerDetailDto(LedgerQuery Query, decimal OpeningBalance, IReadOnlyList<LedgerLineDto> Lines, decimal TotalDebit, decimal TotalCredit, decimal ClosingBalance);

/// <summary>A line on the income statement or balance sheet. Amounts are in the account's natural sign (revenue and liabilities positive).</summary>
public sealed record StatementLineDto(string Section, string? AccountNumber, string Label, decimal Period, decimal YearToDate, bool IsTotal = false);

/// <summary>
/// The income statement (period and year to date) or the balance sheet (change in the period and balance at its end);
/// <see cref="PeriodLabel"/> and <see cref="YearToDateLabel"/> name the two amount columns.
/// </summary>
public sealed record FinancialStatementDto(
    string Title,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly YearStart,
    DateOnly PeriodEnd,
    string PeriodLabel,
    string YearToDateLabel,
    IReadOnlyList<StatementLineDto> Lines);

/// <summary>GL cost on one project/WBS by cost class (labor, ODC, indirect, unallowable).</summary>
public sealed record ProjectCostRowDto(
    Guid ProjectId,
    string ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    string? WbsName,
    decimal DirectLabor,
    decimal DirectOdc,

    /// <summary>Indirect labor, leave (fringe) and other pool costs charged to the project.</summary>
    decimal Indirect,
    decimal Unallowable,
    decimal Other)
{
    public decimal Allowable => DirectLabor + DirectOdc + Indirect + Other;

    public decimal Total => Allowable + Unallowable;
}

/// <summary>GL allowable cost against the project financials actuals (approved labor at cost plus approved allowable ODCs).</summary>
public sealed record ReconciliationRowDto(
    Guid ProjectId,
    string ProjectCode,
    Guid WbsElementId,
    string WbsCode,
    decimal FinancialsLabor,
    decimal FinancialsOdc,
    decimal GlLabor,
    decimal GlOdc,

    /// <summary>Burden applied in the GL by allocation runs and true-ups (applied-burden accounts); not part of <see cref="Difference"/>.</summary>
    decimal GlAppliedBurden = 0,

    /// <summary>Burden at provisional rates on the financials actuals (filled in by indirect rates; 0 without rates).</summary>
    decimal ExpectedBurden = 0)
{
    public decimal BurdenDifference => GlAppliedBurden - ExpectedBurden;

    public decimal BurdenedTotal => FinancialsTotal + ExpectedBurden;

    public decimal FinancialsTotal => FinancialsLabor + FinancialsOdc;

    public decimal GlTotal => GlLabor + GlOdc;

    public decimal Difference => GlTotal - FinancialsTotal;

    public bool IsReconciled => Difference == 0;
}

public sealed record ReconciliationDto(IReadOnlyList<ReconciliationRowDto> Rows, IReadOnlyList<string> Notes)
{
    public bool IsReconciled => Rows.All(r => r.IsReconciled);

    public decimal TotalDifference => Rows.Sum(r => r.Difference);
}

public enum AccountingReport { TrialBalance, LedgerDetail, IncomeStatement, BalanceSheet, ProjectCost, Reconciliation, JournalEntries }

/// <summary>Which report to export and its parameters (period for the statements; the ledger filters for detail).</summary>
public sealed record AccountingExportRequest(AccountingReport Report, Guid? FiscalPeriodId = null, LedgerQuery? Ledger = null, Guid? ProjectId = null);

public sealed record AccountingExportFile(string FileName, string ContentType, string Content);

public interface IAccountingReportService
{
    Task<TrialBalanceDto> GetTrialBalanceAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<LedgerDetailDto> GetLedgerDetailAsync(LedgerQuery query, CancellationToken cancellationToken = default);

    Task<FinancialStatementDto> GetIncomeStatementAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    Task<FinancialStatementDto> GetBalanceSheetAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>GL cost by project and WBS, for one period or (without one) since inception.</summary>
    Task<IReadOnlyList<ProjectCostRowDto>> GetProjectCostLedgerAsync(Guid? projectId = null, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>Inception-to-date GL cost per WBS against the project financials actuals; any difference is flagged.</summary>
    Task<ReconciliationDto> GetReconciliationAsync(Guid? projectId = null, CancellationToken cancellationToken = default);

    Task<AccountingExportFile> ExportCsvAsync(AccountingExportRequest request, CancellationToken cancellationToken = default);
}

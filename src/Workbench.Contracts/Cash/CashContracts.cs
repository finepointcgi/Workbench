using Workbench.Contracts.Accounting;

namespace Workbench.Contracts.Cash;

// Cash management and bank reconciliation: bank accounts linked to GL cash accounts, statement import (CSV, OFX/QFX,
// BAI2), matching bank items to GL cash lines, reconciliations, and the 13-week cash forecast. Finance and administrators
// only. Account numbers are masked everywhere; revealing one is a security event.

public enum StatementFileFormat { Csv, Ofx, Bai2 }

public enum BankItemType { Deposit, Check, Ach, Wire, Fee, Interest, Transfer, Other }

public enum MatchRule { Manual, ArPaymentReference, ApCheckNumber, ApAchReference, AmountAndDate, AmountAndDateGroup, ReversalPair, BookedFromBank }

public enum ReconciliationState { InProgress, Completed }

public enum ForecastRepeat { Once, Weekly, Biweekly, Monthly }

// ---- Bank accounts ----

/// <summary>
/// A bank account. <see cref="MaskedAccountNumber"/> is all that's ever shown (for example <c>••••4321</c>);
/// <see cref="GlBalance"/> is the linked GL cash account's posted balance today.
/// </summary>
public sealed record BankAccountDto(
    Guid Id,
    string Name,
    string BankName,
    string MaskedAccountNumber,
    string AccountNumberLast4,
    bool HasFullAccountNumber,
    string? RoutingNumber,
    Guid GlAccountId,
    string GlAccountNumber,
    string GlAccountName,
    string Currency,
    bool IsActive,
    decimal GlBalance,
    DateOnly? LastStatementDate,
    decimal? LastStatementBalance,
    DateOnly? ReconciledThrough,
    int UnmatchedBankItems,
    CsvColumnMapping? CsvMapping);

/// <summary>
/// Creates or changes a bank account. <see cref="AccountNumber"/>: the full number (stored encrypted, only the last four
/// shown) or just the last four digits; leave it empty on an update to keep the current one.
/// </summary>
public sealed record SaveBankAccountRequest(
    string Name,
    string BankName,
    string? AccountNumber,
    string? RoutingNumber,
    Guid GlAccountId,
    bool IsActive = true);

/// <summary>A revealed account number (logged as a security event).</summary>
public sealed record BankAccountNumberDto(Guid BankAccountId, string AccountNumber);

/// <summary>A GL account that a bank account can link to (detail asset accounts under cash).</summary>
public sealed record CashGlAccountDto(Guid Id, string Number, string Name);

public interface IBankAccountService
{
    Task<IReadOnlyList<BankAccountDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<BankAccountDto> GetAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    Task<BankAccountDto> CreateAsync(SaveBankAccountRequest request, CancellationToken cancellationToken = default);

    Task<BankAccountDto> UpdateAsync(Guid bankAccountId, SaveBankAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>The full account number; recorded as a security event. Fails when only the last four digits were entered.</summary>
    Task<BankAccountNumberDto> RevealAccountNumberAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    /// <summary>Asset accounts a bank account can link to (1010 Cash - operating and other cash accounts).</summary>
    Task<IReadOnlyList<CashGlAccountDto>> ListCashGlAccountsAsync(CancellationToken cancellationToken = default);
}

// ---- Statement import ----

/// <summary>
/// How a bank's CSV export maps to bank items. Columns are header names (case-insensitive) or 1-based column numbers
/// (<c>"3"</c>). Use <see cref="Amount"/> for one signed column, or <see cref="Debit"/> and <see cref="Credit"/> for two.
/// <see cref="DateFormat"/> is a .NET format such as <c>MM/dd/yyyy</c>; empty tries the common ones.
/// </summary>
public sealed record CsvColumnMapping(
    string Date,
    string Description,
    string? Amount = null,
    string? Debit = null,
    string? Credit = null,
    string? CheckNumber = null,
    string? Reference = null,
    string? Type = null,
    string? DateFormat = null,
    bool HasHeader = true,
    string Delimiter = ",",
    bool InvertSign = false);

/// <summary>
/// Imports a statement file. <see cref="Format"/> null detects it from the file. CSV uses <see cref="Mapping"/> or the
/// account's saved mapping; a mapping passed here is saved for the account.
/// </summary>
public sealed record ImportStatementRequest(
    Guid BankAccountId,
    string FileName,
    string Content,
    StatementFileFormat? Format = null,
    CsvColumnMapping? Mapping = null,
    decimal? ClosingBalance = null);

public sealed record StatementImportDto(
    Guid Id,
    Guid BankAccountId,
    StatementFileFormat Format,
    string FileName,
    DateOnly? PeriodStart,
    DateOnly? PeriodEnd,
    decimal? OpeningBalance,
    decimal? ClosingBalance,
    int TransactionCount,
    int DuplicateCount,
    string ImportedBy,
    DateTime ImportedAtUtc);

public sealed record StatementImportResultDto(StatementImportDto Import, IReadOnlyList<string> Warnings);

/// <summary>A bank item. <see cref="Amount"/> is positive for deposits and interest, negative for checks, debits and fees.</summary>
public sealed record BankItemDto(
    Guid Id,
    DateOnly Date,
    decimal Amount,
    BankItemType Type,
    string Description,
    string? CheckNumber,
    string? BankReference,
    Guid? MatchId,
    bool IsLocked,
    Guid? JournalEntryId);

public interface IBankStatementService
{
    Task<StatementImportResultDto> ImportAsync(ImportStatementRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StatementImportDto>> ListImportsAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BankItemDto>> ListItemsAsync(Guid bankAccountId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    /// <summary>Saves the CSV column mapping used for this bank account's statements.</summary>
    Task<BankAccountDto> SaveCsvMappingAsync(Guid bankAccountId, CsvColumnMapping mapping, CancellationToken cancellationToken = default);
}

// ---- Matching and reconciliation ----

/// <summary>
/// A posted journal line on the bank account's GL cash account. <see cref="Amount"/> is debit-positive (receipts
/// positive, payments negative); <see cref="Reference"/> is the AR payment reference or AP check/ACH reference.
/// </summary>
public sealed record GlCashItemDto(
    Guid JournalLineId,
    Guid JournalEntryId,
    string EntryNumber,
    DateOnly Date,
    JournalEntrySource Source,
    string Description,
    string? Reference,
    decimal Amount,
    Guid? MatchId,
    bool IsLocked);

public sealed record BankMatchDto(
    Guid Id,
    MatchRule Rule,
    int Confidence,
    string? Note,
    string MatchedBy,
    DateTime MatchedAtUtc,
    bool IsLocked,
    IReadOnlyList<Guid> BankItemIds,
    IReadOnlyList<Guid> JournalLineIds,
    decimal BankTotal,
    decimal GlTotal);

/// <summary>
/// The reconciliation math as of the statement end date. Bank side: statement ending balance plus deposits in transit
/// less outstanding checks and payments (GL items not yet cleared by the bank). Book side: the GL balance plus bank items
/// not yet recorded in the GL (fees, interest). The adjusted balances must agree.
/// </summary>
public sealed record ReconciliationMathDto(
    decimal StatementEndingBalance,
    decimal DepositsInTransit,
    decimal OutstandingPayments,
    decimal AdjustedBankBalance,
    decimal GlBalance,
    decimal UnrecordedBankItems,
    decimal AdjustedBookBalance,
    IReadOnlyList<GlCashItemDto> OutstandingItems,
    IReadOnlyList<BankItemDto> UnrecordedItems)
{
    public decimal Difference => AdjustedBankBalance - AdjustedBookBalance;

    public bool IsBalanced => Difference == 0;
}

public sealed record BankReconciliationDto(
    Guid Id,
    Guid BankAccountId,
    string BankAccountName,
    string MaskedAccountNumber,
    string GlAccountNumber,
    DateOnly StatementEndDate,
    decimal StatementEndingBalance,
    ReconciliationState Status,
    string StartedBy,
    DateTime StartedAtUtc,
    string? CompletedBy,
    DateTime? CompletedAtUtc,
    string? ReopenedBy,
    DateTime? ReopenedAtUtc,
    string? ReopenReason,
    string? Note,
    ReconciliationMathDto Math);

/// <summary>Everything the two-pane matching screen needs: the reconciliation, the open bank and GL items, and the matches.</summary>
public sealed record ReconcileWorkspaceDto(
    BankReconciliationDto Reconciliation,
    IReadOnlyList<BankItemDto> BankItems,
    IReadOnlyList<GlCashItemDto> GlItems,
    IReadOnlyList<BankMatchDto> Matches,
    int MatchDateWindowDays);

/// <summary>The printable report: the reconciliation plus the items cleared by it.</summary>
public sealed record ReconciliationReportDto(
    BankReconciliationDto Reconciliation,
    string BankName,
    string? RoutingNumber,
    IReadOnlyList<BankItemDto> ClearedBankItems,
    IReadOnlyList<GlCashItemDto> ClearedGlItems);

public sealed record StartReconciliationRequest(Guid BankAccountId, DateOnly StatementEndDate, decimal? StatementEndingBalance = null);

public sealed record UpdateReconciliationRequest(decimal StatementEndingBalance, string? Note = null);

/// <summary>Suggested start values: the latest imported statement's end date and closing (or computed) balance.</summary>
public sealed record ReconciliationDefaultsDto(Guid BankAccountId, DateOnly? StatementEndDate, decimal? StatementEndingBalance, DateOnly? ReconciledThrough, Guid? InProgressId);

public sealed record MatchItemsRequest(Guid BankAccountId, IReadOnlyList<Guid> BankItemIds, IReadOnlyList<Guid> JournalLineIds, string? Note = null);

public sealed record AutoMatchRequest(Guid BankAccountId, DateOnly? Through = null);

public sealed record AutoMatchResultDto(int MatchesCreated, int BankItemsMatched, int GlItemsMatched, IReadOnlyList<BankMatchDto> Matches);

/// <summary>
/// Books an unmatched bank item (a fee, interest, a wire) to the GL: the offset account gets the expense or income and
/// the cash account the bank amount, dated the bank date. The new entry is matched to the item.
/// </summary>
public sealed record BookBankItemRequest(Guid BankItemId, Guid OffsetAccountId, string? Description = null, Guid? OrgUnitId = null);

public sealed record ReconciliationReasonRequest(string Reason);

public sealed record CompleteReconciliationRequest(string? Note = null);

/// <summary>Waives the bank reconciliation close check for one fiscal period (Finance, reason required).</summary>
public sealed record WaiveBankCloseCheckRequest(Guid FiscalPeriodId, string Reason);

public sealed record BankCloseWaiverDto(Guid Id, Guid FiscalPeriodId, string Reason, string WaivedBy, DateTime WaivedAtUtc);

/// <summary>Suggested offset accounts for booking bank items: bank fees (expense) and interest income.</summary>
public sealed record BookingAccountsDto(IReadOnlyList<CashGlAccountDto> Accounts, Guid? BankFeeAccountId, Guid? InterestIncomeAccountId);

public interface IBankReconciliationService
{
    Task<IReadOnlyList<BankReconciliationDto>> ListAsync(Guid? bankAccountId = null, CancellationToken cancellationToken = default);

    Task<ReconciliationDefaultsDto> GetDefaultsAsync(Guid bankAccountId, CancellationToken cancellationToken = default);

    /// <summary>Starts (or returns the in-progress) reconciliation for a statement end date after the last completed one.</summary>
    Task<BankReconciliationDto> StartAsync(StartReconciliationRequest request, CancellationToken cancellationToken = default);

    Task<BankReconciliationDto> UpdateAsync(Guid reconciliationId, UpdateReconciliationRequest request, CancellationToken cancellationToken = default);

    Task<ReconcileWorkspaceDto> GetWorkspaceAsync(Guid reconciliationId, CancellationToken cancellationToken = default);

    Task<AutoMatchResultDto> AutoMatchAsync(AutoMatchRequest request, CancellationToken cancellationToken = default);

    /// <summary>Matches bank items to GL lines (one to one, one to many, many to one); both sides must total the same.</summary>
    Task<BankMatchDto> MatchAsync(MatchItemsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Undoes a match that no completed reconciliation has locked.</summary>
    Task UnmatchAsync(Guid matchId, CancellationToken cancellationToken = default);

    Task<BookingAccountsDto> GetBookingAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>Posts a journal entry (source Bank) for an unmatched bank item and matches it.</summary>
    Task<BankMatchDto> BookBankItemAsync(BookBankItemRequest request, CancellationToken cancellationToken = default);

    /// <summary>Completes when the adjusted balances agree and every bank item is matched or booked; locks the cleared matches. Audited.</summary>
    Task<BankReconciliationDto> CompleteAsync(Guid reconciliationId, CompleteReconciliationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reopens the latest completed reconciliation of an account (reason required), unlocking its matches. Audited.</summary>
    Task<BankReconciliationDto> ReopenAsync(Guid reconciliationId, ReconciliationReasonRequest request, CancellationToken cancellationToken = default);

    Task<ReconciliationReportDto> GetReportAsync(Guid reconciliationId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BankCloseWaiverDto>> ListCloseWaiversAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<BankCloseWaiverDto> WaiveCloseCheckAsync(WaiveBankCloseCheckRequest request, CancellationToken cancellationToken = default);
}

// ---- Cash position and forecast ----

public sealed record CashPositionRowDto(Guid BankAccountId, string Name, string MaskedAccountNumber, string GlAccountNumber, decimal GlBalance, decimal? LastStatementBalance, DateOnly? LastStatementDate, DateOnly? ReconciledThrough);

public sealed record CashPositionDto(DateOnly AsOf, IReadOnlyList<CashPositionRowDto> Accounts)
{
    public decimal Total => Accounts.Sum(a => a.GlBalance);
}

public enum ForecastLineKind { Receivable, Payable, PaymentRun, Payroll, Recurring }

/// <summary>One expected receipt (positive) or payment (negative) in a forecast week.</summary>
public sealed record ForecastLineDto(int Week, DateOnly ExpectedDate, ForecastLineKind Kind, string Description, decimal Amount);

public sealed record ForecastWeekDto(
    int Week,
    DateOnly Start,
    DateOnly End,
    decimal Opening,
    decimal Receivables,
    decimal Payables,
    decimal Payroll,
    decimal Other,
    decimal Closing)
{
    public decimal Net => Receivables + Payables + Payroll + Other;
}

/// <summary>
/// The 13-week forecast from the cash balance today: AR by due date shifted by each customer's average days late, AP by
/// due date (or the approved payment run's date), payroll and recurring items. Overdue items fall in week 1.
/// </summary>
public sealed record CashForecastDto(
    DateOnly AsOf,
    decimal OpeningBalance,
    IReadOnlyList<ForecastWeekDto> Weeks,
    IReadOnlyList<ForecastLineDto> Lines,
    decimal PayrollPerCycle,
    int PayCycleDays,
    string PayrollBasis,
    IReadOnlyList<string> Notes);

public sealed record ForecastItemDto(Guid Id, string Name, decimal Amount, ForecastRepeat Frequency, DateOnly StartDate, DateOnly? EndDate, bool IsActive);

public sealed record SaveForecastItemRequest(string Name, decimal Amount, ForecastRepeat Frequency, DateOnly StartDate, DateOnly? EndDate = null, bool IsActive = true);

public sealed record CashSettingsDto(decimal? PayrollAmount, int PayCycleDays, DateOnly? PayDateAnchor, int MatchDateWindowDays);

public interface ICashForecastService
{
    Task<CashPositionDto> GetPositionAsync(CancellationToken cancellationToken = default);

    Task<CashForecastDto> GetForecastAsync(int weeks = 13, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForecastItemDto>> ListItemsAsync(CancellationToken cancellationToken = default);

    Task<ForecastItemDto> CreateItemAsync(SaveForecastItemRequest request, CancellationToken cancellationToken = default);

    Task<ForecastItemDto> UpdateItemAsync(Guid itemId, SaveForecastItemRequest request, CancellationToken cancellationToken = default);

    Task<CashSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    Task<CashSettingsDto> SaveSettingsAsync(CashSettingsDto settings, CancellationToken cancellationToken = default);
}

/// <summary>Security event names for cash management (see <c>Contracts.Security.SecurityEventTypes</c>).</summary>
public static class CashSecurityEvents
{
    public const string AccountNumberRevealed = "bank.account_number_revealed";
    public const string AccountNumberChanged = "bank.account_number_changed";
}

namespace Workbench.Contracts.Companies;

// Multicompany: legal companies within one tenant, intercompany accounts and consolidated statements.
// Finance and Admin manage companies and see consolidations; Admin decides who may use which company. Everyone signed in
// reads their own company context (the app bar switcher).

public sealed record CompanyDto(
    Guid Id,
    string Code,
    string LegalName,
    string? Ein,
    int FiscalYearStartMonth,
    string HomeCurrencyCode,
    bool IsActive,
    bool IsDefault);

/// <summary>Creates or changes a company. <see cref="FiscalYearStartMonth"/> defaults to the tenant's fiscal calendar.</summary>
public sealed record SaveCompanyRequest(string Code, string LegalName, string? Ein = null, int? FiscalYearStartMonth = null, bool IsActive = true);

/// <summary>
/// The companies the signed-in user can work in and the one they picked (null = all of them). The switcher shows only
/// when <see cref="ShowSwitcher"/> (two or more companies).
/// </summary>
public sealed record CompanyContextDto(IReadOnlyList<CompanyDto> Companies, Guid? SelectedCompanyId, bool ShowSwitcher)
{
    public CompanyDto? Selected => Companies.FirstOrDefault(c => c.Id == SelectedCompanyId);
}

public sealed record SelectCompanyRequest(Guid? CompanyId);

/// <summary>A user's company list. Empty means every company.</summary>
public sealed record UserCompanyAccessDto(Guid UserId, string Email, string DisplayName, IReadOnlyList<Guid> CompanyIds);

public sealed record SetUserCompanyAccessRequest(IReadOnlyList<Guid> CompanyIds);

/// <summary>Records that can be moved to another company from Setup › Companies.</summary>
public enum CompanyRecordKind { Contract, Project, Employee, BankAccount, FixedAsset }

/// <summary>A record and its company. <see cref="Locked"/> says why it can't move (for example, it has posted entries).</summary>
public sealed record CompanyRecordDto(CompanyRecordKind Kind, Guid Id, string Code, string Name, Guid CompanyId, string CompanyCode, string? Locked);

public sealed record AssignCompanyRequest(CompanyRecordKind Kind, Guid RecordId, Guid CompanyId);

/// <summary>One company's state in a fiscal period. <see cref="ClosedForEveryone"/>: the period itself is closed.</summary>
public sealed record CompanyPeriodDto(
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    bool IsClosed,
    bool ClosedForEveryone,
    string? ChangedBy,
    DateTime? ChangedAtUtc,
    string? Reason);

public sealed record SetCompanyPeriodRequest(bool Closed, string Reason);

public interface ICompanyService
{
    /// <summary>The signed-in user's companies and their pick (anyone signed in).</summary>
    Task<CompanyContextDto> GetContextAsync(CancellationToken cancellationToken = default);

    /// <summary>Works in one company (or, with null, all of them). Lists, the ledger reports and new records follow it.</summary>
    Task<CompanyContextDto> SelectAsync(SelectCompanyRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanyDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    /// <summary>Adds a company, with intercompany accounts to every other company when the default chart's are there.</summary>
    Task<CompanyDto> CreateAsync(SaveCompanyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a company. The default company can't be deactivated.</summary>
    Task<CompanyDto> UpdateAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<UserCompanyAccessDto>> ListUserAccessAsync(CancellationToken cancellationToken = default);

    Task<UserCompanyAccessDto> SetUserAccessAsync(Guid userId, SetUserCompanyAccessRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanyRecordDto>> ListRecordsAsync(CompanyRecordKind kind, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves a record to another company. A contract takes its projects along; records with posted ledger activity stay
    /// where they are (history never moves between ledgers).
    /// </summary>
    Task<CompanyRecordDto> AssignAsync(AssignCompanyRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CompanyPeriodDto>> ListPeriodsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>Closes or reopens one company's period (reason required, audited). Closed companies refuse postings there.</summary>
    Task<CompanyPeriodDto> SetPeriodClosedAsync(Guid companyId, Guid fiscalPeriodId, SetCompanyPeriodRequest request, CancellationToken cancellationToken = default);
}

// ---- Intercompany ----

/// <summary>
/// The accounts one company uses with another: due from (an asset) and due to (a liability). Listed for every ordered pair
/// of active companies, configured or not.
/// </summary>
public sealed record IntercompanyAccountDto(
    Guid CompanyId,
    string CompanyCode,
    Guid CounterpartyCompanyId,
    string CounterpartyCode,
    Guid? DueFromAccountId,
    string? DueFromAccount,
    Guid? DueToAccountId,
    string? DueToAccount)
{
    public bool IsConfigured => DueFromAccountId is not null && DueToAccountId is not null;
}

public sealed record SaveIntercompanyAccountRequest(Guid CompanyId, Guid CounterpartyCompanyId, Guid DueFromAccountId, Guid DueToAccountId);

/// <summary>
/// What one company's books say about another at a period end (debit-positive: due from minus due to), beside what the
/// other company's books say. They agree when <see cref="Difference"/> is zero.
/// </summary>
public sealed record IntercompanyBalanceDto(
    Guid CompanyId,
    string CompanyCode,
    Guid CounterpartyCompanyId,
    string CounterpartyCode,
    decimal Balance,
    decimal CounterpartyBalance)
{
    public decimal Difference => Balance + CounterpartyBalance;
}

public interface IIntercompanyService
{
    Task<IReadOnlyList<IntercompanyAccountDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<IntercompanyAccountDto> SaveAsync(SaveIntercompanyAccountRequest request, CancellationToken cancellationToken = default);

    /// <summary>Intercompany balances at the end of a fiscal period, one row per pair of companies.</summary>
    Task<IReadOnlyList<IntercompanyBalanceDto>> ListBalancesAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);
}

// ---- Consolidation ----

public enum ConsolidatedReport { TrialBalance, IncomeStatement, BalanceSheet }

/// <summary>Consolidates <see cref="CompanyIds"/> (null or empty: every company the user can use) at a period end.</summary>
public sealed record ConsolidationRequest(Guid FiscalPeriodId, IReadOnlyList<Guid>? CompanyIds = null);

public sealed record ConsolidationCompanyDto(Guid Id, string Code, string LegalName);

/// <summary>
/// One line of a consolidated report: each company's amount (in <see cref="ConsolidationDto.Companies"/> order), the
/// eliminations and the consolidated total (companies plus eliminations).
/// </summary>
public sealed record ConsolidationRowDto(
    string Section,
    string? AccountNumber,
    string Label,
    IReadOnlyList<decimal> Companies,
    decimal Eliminations,
    decimal Total,
    bool IsTotal = false);

/// <summary>An eliminated intercompany balance: <see cref="Amount"/> is what the elimination adds (debit-positive).</summary>
public sealed record EliminationDto(string CompanyCode, string CounterpartyCode, string AccountNumber, string AccountName, decimal Amount);

/// <summary>
/// A consolidated trial balance (ending balances, debit-positive), income statement (year to date) or balance sheet
/// (balances at period end), in the statements' natural signs.
/// </summary>
public sealed record ConsolidationDto(
    ConsolidatedReport Report,
    string Title,
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    string AmountLabel,
    IReadOnlyList<ConsolidationCompanyDto> Companies,
    IReadOnlyList<ConsolidationRowDto> Rows,
    IReadOnlyList<EliminationDto> Eliminations,
    IReadOnlyList<string> Notes);

public interface IConsolidationService
{
    Task<ConsolidationDto> GetAsync(ConsolidatedReport report, ConsolidationRequest request, CancellationToken cancellationToken = default);
}

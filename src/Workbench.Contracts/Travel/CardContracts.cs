using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Travel;

// Mirrors Workbench.Domain.Travel.CardPaymentResponsibility by name and value.
public enum CardPaymentKind { CompanyPaid, EmployeePaid }

/// <summary>Where a card charge stands.</summary>
public enum CardChargeState
{
    /// <summary>Not on an expense report yet.</summary>
    Unassigned,

    /// <summary>On an expense report that hasn't been submitted (draft or sent back).</summary>
    OnDraftReport,

    Submitted,
    Approved,

    /// <summary>Finance took it off the list (a duplicate, a credit already handled, a personal charge repaid).</summary>
    Excluded,
}

public sealed record CardProgramDto(
    Guid Id,
    string Name,
    string Issuer,
    Guid? VendorId,
    string? VendorName,
    CardPaymentKind PaymentResponsibility,
    Guid? LiabilityAccountId,
    string? LiabilityAccount,
    int UnsubmittedAlertDays,
    bool IsActive,
    int CardCount);

/// <summary>A company-paid program needs a liability account; without one, 2030 Corporate card payable is used (and created).</summary>
public sealed record SaveCardProgramRequest(
    string Name,
    string Issuer,
    CardPaymentKind PaymentResponsibility = CardPaymentKind.CompanyPaid,
    Guid? VendorId = null,
    Guid? LiabilityAccountId = null,
    int UnsubmittedAlertDays = 30,
    bool IsActive = true);

public sealed record CorporateCardDto(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    CardPaymentKind PaymentResponsibility,
    Guid EmployeeId,
    string EmployeeName,
    string Last4,
    string? NameOnCard,
    bool IsActive);

public sealed record SaveCorporateCardRequest(Guid ProgramId, Guid EmployeeId, string Last4, string? NameOnCard = null, bool IsActive = true);

public sealed record CardImportResultDto(
    Guid ImportId,
    string Format,
    int Rows,
    int Imported,
    int Duplicates,
    int Unmatched,
    decimal ImportedTotal,
    IReadOnlyList<string> Warnings);

public sealed record CardStatementImportDto(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    string FileName,
    string Format,
    int Rows,
    int Imported,
    int Duplicates,
    int Unmatched,
    decimal ImportedTotal,
    string ImportedBy,
    DateTime ImportedAtUtc);

public sealed record CardTransactionDto(
    Guid Id,
    Guid ProgramId,
    string ProgramName,
    CardPaymentKind PaymentResponsibility,
    Guid? CardId,
    string CardLast4,
    Guid? EmployeeId,
    string? EmployeeName,
    DateOnly TransactionDate,
    DateOnly? PostedDate,
    decimal Amount,
    string Merchant,
    string? MerchantCategory,
    TravelCategory SuggestedCategory,
    CardChargeState State,
    Guid? OdcEntryId,
    Guid? ExpenseReportId,
    string? ExpenseReportTitle,
    int AgeDays,
    bool IsOverdue,
    string? ExcludedReason);

public sealed record CardTransactionQuery(
    Guid? ProgramId = null,
    CardChargeState? State = null,
    Guid? EmployeeId = null,
    Guid? ImportId = null,
    bool UnmatchedOnly = false,
    int Take = 500);

/// <summary>Unsubmitted charges (unassigned or on a draft report) by cardholder and age.</summary>
public sealed record CardAgingRowDto(
    Guid? EmployeeId,
    string Cardholder,
    string Cards,
    int Count,
    decimal Days0To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    int Overdue)
{
    public decimal Total => Days0To30 + Days31To60 + Days61To90 + Over90;
}

public sealed record CardAgingDto(DateOnly AsOf, IReadOnlyList<CardAgingRowDto> Rows, int OverdueCount)
{
    public decimal Total => Rows.Sum(r => r.Total);
}

/// <summary>A program's charges by state: what's still with cardholders, on reports, approved, and billed by the issuer.</summary>
public sealed record CardReconciliationDto(
    Guid ProgramId,
    string ProgramName,
    CardPaymentKind PaymentResponsibility,
    int Count,
    decimal Imported,
    decimal Unassigned,
    decimal OnReports,
    decimal Approved,
    decimal Excluded,
    int Unmatched,
    int Overdue);

public sealed record ExcludeCardTransactionRequest(string Reason);

public sealed record AssignCardRequest(Guid CardId);

/// <summary>
/// Corporate card programs, cards, statement imports and reconciliation: Finance and administrators. Statement files are
/// read by the registered card feeds (CSV, OFX); nothing calls a bank.
/// </summary>
public interface ICardProgramService
{
    Task<IReadOnlyList<CardProgramDto>> ListProgramsAsync(CancellationToken cancellationToken = default);

    Task<CardProgramDto> CreateProgramAsync(SaveCardProgramRequest request, CancellationToken cancellationToken = default);

    Task<CardProgramDto> UpdateProgramAsync(Guid programId, SaveCardProgramRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CorporateCardDto>> ListCardsAsync(Guid? programId = null, CancellationToken cancellationToken = default);

    Task<CorporateCardDto> CreateCardAsync(SaveCorporateCardRequest request, CancellationToken cancellationToken = default);

    Task<CorporateCardDto> UpdateCardAsync(Guid cardId, SaveCorporateCardRequest request, CancellationToken cancellationToken = default);

    /// <summary>Imports a CSV or OFX statement. Rows already imported (same bank id, or same card, date, amount and merchant) are skipped.</summary>
    Task<CardImportResultDto> ImportStatementAsync(Guid programId, string fileName, Stream content, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CardStatementImportDto>> ListImportsAsync(Guid? programId = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CardTransactionDto>> ListTransactionsAsync(CardTransactionQuery? query = null, CancellationToken cancellationToken = default);

    Task<CardAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CardReconciliationDto>> GetReconciliationAsync(CancellationToken cancellationToken = default);

    Task<CardTransactionDto> ExcludeTransactionAsync(Guid transactionId, ExcludeCardTransactionRequest request, CancellationToken cancellationToken = default);

    Task<CardTransactionDto> RestoreTransactionAsync(Guid transactionId, CancellationToken cancellationToken = default);

    /// <summary>Assigns an unmatched charge (no card with its last four digits) to a card.</summary>
    Task<CardTransactionDto> AssignCardAsync(Guid transactionId, AssignCardRequest request, CancellationToken cancellationToken = default);
}

// ---- The cardholder's side ----

/// <summary>A card charge on an expense line; the line's amount is locked to it.</summary>
public sealed record CardLineDto(Guid OdcEntryId, Guid TransactionId, string CardLast4, string Merchant, DateOnly TransactionDate, decimal Amount, bool IsCompanyPaid);

public sealed record CardChargeSummaryDto(int Unassigned, decimal UnassignedTotal, int Overdue, int AlertDays);

/// <summary>Adds a card charge to one of the cardholder's draft expense reports as a line (amount and date from the charge).</summary>
public sealed record AttachCardChargeRequest(
    Guid ExpenseReportId,
    Guid WbsElementId,
    OdcCostType Type = OdcCostType.Travel,
    string? Description = null,
    bool IsUnallowable = false,
    string? UnallowableReason = null);

/// <summary>The signed-in cardholder's cards and charges ("unassigned card charges").</summary>
public interface ICardChargeService
{
    Task<IReadOnlyList<CorporateCardDto>> ListMyCardsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<CardTransactionDto>> ListMyChargesAsync(bool includeAssigned = false, CancellationToken cancellationToken = default);

    Task<CardChargeSummaryDto> GetMySummaryAsync(CancellationToken cancellationToken = default);

    Task<OdcEntryDto> AttachToReportAsync(Guid transactionId, AttachCardChargeRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes the charge off its draft report (the line is removed) and back to the unassigned list.</summary>
    Task DetachAsync(Guid transactionId, CancellationToken cancellationToken = default);

    /// <summary>The card lines of an expense report (whoever may see the report).</summary>
    Task<IReadOnlyList<CardLineDto>> ListReportCardLinesAsync(Guid expenseReportId, CancellationToken cancellationToken = default);
}

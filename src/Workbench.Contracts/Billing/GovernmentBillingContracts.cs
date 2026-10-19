namespace Workbench.Contracts.Billing;

// Government billing: SF 1034/1035 vouchers, contract financing (progress payments on SF 1443 and performance-based
// payments) with liquidation on delivery invoices, and the PIEE (WAWF) import file. Finance and administrators do
// everything; a project manager can read their contracts' financing and print their vouchers.

public enum VoucherFormat { Custom, Sf1034 }

public enum FinancingKind { None, ProgressPayments, PerformanceBased }

public enum ProgressPaymentBasis { IncurredCosts, PaidCosts }

public enum PieeDocumentKind { TwoInOne, CostVoucher, Combo, ProgressPayment, PerformanceBasedPayment }

public enum PieeState { NotSubmitted, Submitted, Accepted, Rejected }

// ---- Settings ----

public sealed record GovernmentBillingSettingsDto(
    Guid ContractId,
    string ContractNumber,
    VoucherFormat Format,
    FinancingKind Financing,
    bool IsSmallBusiness,
    decimal ProgressPaymentRate,
    decimal LiquidationRate,
    ProgressPaymentBasis CostBasis,
    decimal? ContractPrice,
    decimal EffectiveContractPrice,
    string? IssueByDodaac,
    string? AdminByDodaac,
    string? PayOfficeDodaac,
    string? ShipToDodaac,
    PieeDocumentKind DeliveryDocument,
    string? CageCode);

/// <summary>
/// Rates are fractions (0.80 = 80%). <see cref="ContractPrice"/> null uses the contract ceiling. For performance-based
/// payments <see cref="LiquidationRate"/> 0 means "events total ÷ contract price".
/// </summary>
public sealed record SaveGovernmentBillingSettingsRequest(
    VoucherFormat Format,
    FinancingKind Financing,
    bool IsSmallBusiness = false,
    decimal ProgressPaymentRate = 0,
    decimal LiquidationRate = 0,
    ProgressPaymentBasis CostBasis = ProgressPaymentBasis.PaidCosts,
    decimal? ContractPrice = null,
    string? IssueByDodaac = null,
    string? AdminByDodaac = null,
    string? PayOfficeDodaac = null,
    string? ShipToDodaac = null,
    PieeDocumentKind DeliveryDocument = PieeDocumentKind.TwoInOne);

/// <summary>One invoice's effect on contract financing: financing billed (positive) or liquidated (positive).</summary>
public sealed record FinancingActivityDto(Guid InvoiceId, string InvoiceNumber, DateOnly InvoiceDate, InvoiceKind Type, InvoiceState Status, decimal Financing, decimal Liquidated, decimal DeliveryAmount);

/// <summary>Financing billed on approved invoices, less what deliveries have liquidated; drafts shown separately.</summary>
public sealed record FinancingPositionDto(
    Guid ContractId,
    FinancingKind Financing,
    decimal LiquidationRate,
    decimal FinancingBilled,
    decimal Liquidated,
    decimal Unliquidated,
    decimal DraftFinancing,
    decimal DraftLiquidation,
    decimal DeliveriesBilled,
    IReadOnlyList<FinancingActivityDto> Activity);

// ---- Progress payments (SF 1443) ----

/// <summary>
/// The SF 1443 figures Finance may change before a request is created; null keeps the value computed from the ledger
/// (line 12b estimate to complete, 14a/14b/14d subcontractor progress payments, 20a, 21a, 21d, 23).
/// </summary>
public sealed record Sf1443Overrides(
    decimal? EstimateToComplete = null,
    decimal? SubcontractorPaid = null,
    decimal? SubcontractorLiquidated = null,
    decimal? SubcontractorApproved = null,
    decimal? CostsOfDelivered = null,
    decimal? PriceOfDelivered = null,
    decimal? UnliquidatedAdvances = null,
    decimal? LiquidatedToDate = null);

public sealed record ProgressPaymentPreviewRequest(Guid ContractId, DateOnly CostsThrough, DateOnly? RequestDate = null, Sf1443Overrides? Overrides = null, string? Notes = null);

/// <summary>Every item on the SF 1443, computed. Line numbers follow the form (REV. 10/2023).</summary>
public sealed record Sf1443Dto(
    // Section I
    string To,
    string From,
    bool IsSmallBusiness,
    string ContractNumber,
    decimal ContractPrice,
    decimal ProgressPaymentRate,
    decimal LiquidationRate,
    DateOnly InitialAwardDate,
    int RequestNumber,
    DateOnly RequestDate,
    // Section II
    DateOnly CostsThrough,
    ProgressPaymentBasis CostBasis,
    decimal PaidCosts,
    decimal UnpaidCosts,
    decimal Line11EligibleCosts,
    decimal Line12aIncurredToDate,
    decimal Line12bEstimateToComplete,
    decimal Line13,
    bool LossRatioApplied,
    decimal Line14aSubPaid,
    decimal Line14bSubLiquidated,
    decimal Line14cSubUnliquidated,
    decimal Line14dSubApproved,
    decimal Line14eSubEligible,
    decimal Line15,
    decimal Line16,
    decimal Line17,
    decimal Line18PreviousRequested,
    decimal Line19MaximumEligible,
    // Section III
    decimal Line20aCostsDelivered,
    decimal Line20bCostsUndelivered,
    decimal Line20c,
    decimal Line20dSubEligible,
    decimal Line20eLimitation,
    decimal Line21aPriceDelivered,
    decimal Line21bPriceUndelivered,
    decimal Line21c,
    decimal Line21dUnliquidatedAdvances,
    decimal Line21eLimitation,
    decimal Line22MaximumUnliquidated,
    decimal Line23Liquidated,
    decimal Line24Unliquidated,
    decimal Line25MaximumPermissible,
    // Section IV
    decimal Line26Amount,
    IReadOnlyList<string> Notes);

public sealed record ProgressPaymentRequestDto(
    Guid Id,
    Guid ContractId,
    int Number,
    DateOnly RequestDate,
    DateOnly CostsThrough,
    decimal Amount,
    Guid? InvoiceId,
    string? InvoiceNumber,
    InvoiceState? InvoiceStatus,
    string? Notes,
    string CreatedBy,
    DateTime CreatedAtUtc,
    Sf1443Dto Form);

public interface IProgressPaymentService
{
    Task<IReadOnlyList<ProgressPaymentRequestDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ProgressPaymentRequestDto> GetAsync(Guid requestId, CancellationToken cancellationToken = default);

    /// <summary>The SF 1443 a request through <see cref="ProgressPaymentPreviewRequest.CostsThrough"/> would carry. Saves nothing.</summary>
    Task<Sf1443Dto> PreviewAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>Saves the request and a draft ProgressPayment invoice for line 26. Approve that invoice to bill it.</summary>
    Task<ProgressPaymentRequestDto> CreateAsync(ProgressPaymentPreviewRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a request whose invoice is still a draft (the draft goes too) or void.</summary>
    Task DeleteAsync(Guid requestId, CancellationToken cancellationToken = default);
}

// ---- Performance-based payments ----

public sealed record PerformanceEventDto(
    Guid Id,
    Guid ContractId,
    Guid? ClinId,
    string? ClinNumber,
    int Order,
    string Description,
    string? Criteria,
    decimal Amount,
    DateOnly PlannedDate,
    DateOnly? CompletedDate,
    string? CompletedBy,
    string? Evidence,
    string? EvidenceReference,
    Guid? InvoiceId,
    string? InvoiceNumber)
{
    public bool IsBilled => InvoiceId is not null;
}

public sealed record SavePerformanceEventRequest(string Description, decimal Amount, DateOnly PlannedDate, string? Criteria = null, Guid? ClinId = null);

/// <summary>Evidence is required: what shows the event was accomplished.</summary>
public sealed record CompletePerformanceEventRequest(DateOnly CompletedDate, string Evidence, string? EvidenceReference = null);

public interface IPerformanceBasedPaymentService
{
    Task<IReadOnlyList<PerformanceEventDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>The contract must use performance-based payments; events can't total more than 90% of the contract price (FAR 32.1004).</summary>
    Task<PerformanceEventDto> CreateAsync(Guid contractId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default);

    Task<PerformanceEventDto> UpdateAsync(Guid eventId, SavePerformanceEventRequest request, CancellationToken cancellationToken = default);

    Task<PerformanceEventDto> CompleteAsync(Guid eventId, CompletePerformanceEventRequest request, CancellationToken cancellationToken = default);

    Task<PerformanceEventDto> ReopenAsync(Guid eventId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid eventId, CancellationToken cancellationToken = default);
}

// ---- SF 1034 / SF 1035 voucher ----

public sealed record VoucherLineDto(string? ClinNumber, string Description, decimal? Quantity, string? Unit, decimal? UnitPrice, decimal Amount, decimal? CumulativeAmount);

/// <summary>
/// One printed page: the SF 1034 (page 1) or an SF 1035 continuation sheet. <see cref="BroughtForward"/> is the total of
/// earlier pages; <see cref="CarriedForward"/> includes this page.
/// </summary>
public sealed record VoucherPageDto(int PageNumber, int PageCount, bool IsContinuation, IReadOnlyList<VoucherLineDto> Lines, decimal BroughtForward, decimal PageTotal, decimal CarriedForward);

public sealed record VoucherDto(
    InvoicePrintDto Print,
    VoucherFormat ContractFormat,
    bool IsCostType,
    string PaymentKind,
    IReadOnlyList<VoucherPageDto> Pages,
    decimal GrossAmount,
    decimal LiquidationAmount,
    string? Uei,
    string? CageCode,
    Guid? ProgressPaymentRequestId);

// ---- PIEE (WAWF) ----

public sealed record PieeIssueDto(string Field, string Message);

public sealed record PieeStatusDto(
    Guid InvoiceId,
    string InvoiceNumber,
    PieeDocumentKind DocumentType,
    PieeState Status,
    IReadOnlyList<PieeIssueDto> Issues,
    string? FileName,
    DateTime? ExportedAtUtc,
    string? Reference,
    DateTime? SubmittedAtUtc,
    string? SubmittedBy,
    string? RejectionReason,
    DateTime? StatusChangedAtUtc,
    string? StatusChangedBy,
    string SubmitterName)
{
    public bool IsValid => Issues.Count == 0;
}

/// <summary>Records a status by hand: Submitted (with the PIEE reference), Accepted, or Rejected (a reason is required).</summary>
public sealed record SetPieeStatusRequest(PieeState Status, string? Reference = null, string? Reason = null);

/// <summary>What the configured submitter did with the file. The built-in one never calls PIEE.</summary>
public sealed record PieeSubmitResultDto(PieeStatusDto Status, bool Transmitted, string Message, BillingExportFile File);

public interface IGovernmentBillingService
{
    Task<GovernmentBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<GovernmentBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveGovernmentBillingSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Financing billed, liquidated and outstanding for a contract.</summary>
    Task<FinancingPositionDto> GetFinancingAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>The SF 1034 voucher (with SF 1035 continuation pages) for any invoice, and the contract's chosen format.</summary>
    Task<VoucherDto> GetVoucherAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>The PIEE document type, the problems that block export, and the submission status.</summary>
    Task<PieeStatusDto> GetPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>The PIEE XML import file. Refused (409) while required fields are missing.</summary>
    Task<BillingExportFile> ExportPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Hands the file to the configured submitter (by default: nothing is sent; upload it in PIEE).</summary>
    Task<PieeSubmitResultDto> SubmitPieeAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<PieeStatusDto> SetPieeStatusAsync(Guid invoiceId, SetPieeStatusRequest request, CancellationToken cancellationToken = default);
}

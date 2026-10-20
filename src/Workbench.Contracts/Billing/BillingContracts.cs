namespace Workbench.Contracts.Billing;

// Billing and accounts receivable. Finance and administrators do everything here; a project manager can read the
// invoices (and print them) for contracts their projects are on. Approving an invoice posts it to the general ledger.

public enum InvoiceKind { TimeAndMaterials, CostPlus, FixedPriceMilestone, TrueUp, CreditMemo, ProgressPayment, PerformanceBased }

public enum InvoiceState { Draft, Approved, Sent, PartiallyPaid, Paid, Void }

public enum InvoiceLineType { Labor, Odc, MaterialHandling, Burden, Fee, FeeWithhold, WithholdRelease, Milestone, Adjustment, ProgressPayment, PerformanceBasedPayment, Liquidation, SalesTax }

public enum FundingCheckLevel { Ok, OverFunded, OverCeiling }

public enum CashReceiptMethod { Eft, Check, Wire, Other }

// ---- Invoices ----

public sealed record InvoiceSummaryDto(
    Guid Id,
    string Number,
    Guid ContractId,
    string ContractNumber,
    Guid CustomerId,
    string CustomerName,
    InvoiceKind Type,
    InvoiceState Status,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    decimal Total,
    decimal AmountPaid,
    decimal AmountCredited,
    decimal Balance,
    int DaysPastDue,

    // Added by multicurrency: an invoice issued in the customer's currency (null: the functional currency). The rate and the
    // transaction amounts are fixed when it's approved; the amounts above stay functional.
    string? CurrencyCode = null,
    decimal ExchangeRate = 1m,
    decimal TransactionTotal = 0m,
    decimal TransactionBalance = 0m)
{
    public bool IsForeignCurrency => CurrencyCode is not null;
}

public sealed record InvoiceLineDto(
    Guid Id,
    int LineNumber,
    Guid? ClinId,
    string? ClinNumber,
    InvoiceLineType Kind,
    string Description,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    Guid? LaborCategoryId,
    string? LaborCategoryCode,
    string? OdcType,
    decimal Quantity,
    decimal Rate,
    decimal Amount,
    bool IsGenerated,
    int SourceCount);

/// <summary>Billed to date on one CLIN against its funding and ceiling, including the invoice being checked.</summary>
public sealed record ClinFundingCheckDto(
    Guid ClinId,
    string ClinNumber,
    string Description,
    string Pricing,
    decimal FundedValue,
    decimal CeilingValue,
    decimal PreviouslyBilled,
    decimal ThisInvoice,
    FundingCheckLevel Level)
{
    public decimal CumulativeBilled => PreviouslyBilled + ThisInvoice;

    public decimal FundedRemaining => FundedValue - CumulativeBilled;
}

public sealed record InvoiceDto(
    InvoiceSummaryDto Summary,
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<ClinFundingCheckDto> Funding,
    decimal Withheld,
    string? RateNote,
    string? Notes,
    string? FundingOverrideReason,
    Guid? CreditOfInvoiceId,
    string? CreditOfInvoiceNumber,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    int PaymentTermsDays,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    DateTime? SentAtUtc,
    string? VoidedBy,
    DateTime? VoidedAtUtc,
    string? VoidReason,
    IReadOnlyList<string> Warnings)
{
    public bool NeedsFundingOverride => Funding.Any(f => f.Level == FundingCheckLevel.OverFunded);

    public bool IsOverCeiling => Funding.Any(f => f.Level == FundingCheckLevel.OverCeiling);
}

public sealed record InvoiceQuery(
    Guid? ContractId = null,
    Guid? CustomerId = null,
    InvoiceState? Status = null,
    InvoiceKind? Type = null,
    string? Search = null,
    bool OpenOnly = false,
    int Take = 500);

/// <summary>
/// What to bill: one contract, one type, one period. <see cref="InvoiceDate"/> defaults to today.
/// For <see cref="InvoiceKind.TrueUp"/> the period is the fiscal year being trued up.
/// <see cref="ReleaseWithhold"/> adds the fee withheld so far (the final invoice).
/// </summary>
public sealed record GenerateInvoiceRequest(
    Guid ContractId,
    InvoiceKind Type,
    DateOnly PeriodFrom,
    DateOnly PeriodTo,
    DateOnly? InvoiceDate = null,
    bool ReleaseWithhold = false,
    string? Notes = null);

/// <summary>What a draft would contain, without saving anything.</summary>
public sealed record InvoicePreviewDto(
    GenerateInvoiceRequest Request,
    string ContractNumber,
    string CustomerName,
    IReadOnlyList<InvoiceLineDto> Lines,
    IReadOnlyList<ClinFundingCheckDto> Funding,
    decimal Total,
    decimal Withheld,
    string? RateNote,
    IReadOnlyList<string> Warnings,
    Guid? ExistingDraftId)
{
    public bool NeedsFundingOverride => Funding.Any(f => f.Level == FundingCheckLevel.OverFunded);

    public bool IsOverCeiling => Funding.Any(f => f.Level == FundingCheckLevel.OverCeiling);
}

public sealed record UpdateInvoiceDraftRequest(DateOnly InvoiceDate, string? Notes);

/// <summary>A manual line on a draft (or credit memo). Amount may be negative.</summary>
public sealed record AddInvoiceLineRequest(Guid? ClinId, string Description, decimal Amount, InvoiceLineType Kind = InvoiceLineType.Adjustment);

/// <summary><see cref="FundingOverrideReason"/> is required when the invoice takes a CLIN past its funded value.</summary>
public sealed record ApproveInvoiceRequest(string? FundingOverrideReason = null);

public sealed record VoidInvoiceRequest(string Reason);

/// <summary>A credit memo against an approved invoice: one line per CLIN credited (positive amounts are credited).</summary>
public sealed record CreditMemoRequest(string Reason, IReadOnlyList<CreditMemoLine> Lines, DateOnly? InvoiceDate = null);

public sealed record CreditMemoLine(Guid? ClinId, string Description, decimal Amount);

public sealed record BillingExportFile(string FileName, string ContentType, string Content);

// ---- Print view ----

public sealed record InvoicePartyDto(string Name, string? Address, string? Code);

public sealed record LaborDetailRowDto(string EmployeeName, string EmployeeNumber, string LaborCategory, string? ClinNumber, decimal Hours, decimal Rate, decimal Amount);

public sealed record OdcDetailRowDto(DateOnly Date, string? ClinNumber, string Type, string Description, string? Vendor, decimal Amount);

public sealed record ClinTotalDto(string? ClinNumber, string Description, decimal ThisInvoice, decimal BilledToDate, decimal FundedValue);

public sealed record InvoicePrintDto(
    InvoiceDto Invoice,
    string ContractNumber,
    string ContractName,
    string ContractType,
    InvoicePartyDto Payee,
    InvoicePartyDto Payer,
    string? PayingOffice,
    string? AdminOffice,
    IReadOnlyList<ClinTotalDto> ClinTotals,
    IReadOnlyList<LaborDetailRowDto> Labor,
    IReadOnlyList<OdcDetailRowDto> Odcs,
    decimal BilledToDate);

// ---- Contract billing ----

public sealed record BillingClinDto(Guid Id, string Number, string Description, string Pricing, bool IsExercised, decimal? FeeRate, decimal FundedValue, decimal CeilingValue, decimal BilledToDate);

public sealed record BillingContractDto(
    Guid Id,
    string ContractNumber,
    string Name,
    Guid CustomerId,
    string CustomerName,
    string ContractType,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    IReadOnlyList<BillingClinDto> Clins,
    IReadOnlyList<InvoiceKind> SuggestedTypes);

/// <summary>The billed-to-date card on the contract page.</summary>
public sealed record ContractBillingSummaryDto(
    Guid ContractId,
    decimal FundedValue,
    decimal CeilingValue,
    decimal BilledToDate,
    decimal Paid,
    decimal Outstanding,
    decimal WithheldToDate,
    decimal DraftTotal,
    int InvoiceCount,
    DateOnly? LastInvoiceDate,
    IReadOnlyList<BillingClinDto> Clins);

public sealed record ContractBillingSettingsDto(
    Guid ContractId,
    string InvoicePrefix,
    decimal FeeWithholdRate,
    decimal? FeeWithholdCap,
    decimal? MaterialHandlingRate,
    int? PaymentTermsDays,
    string? PayingOffice,
    string? AdminOffice,
    string? CageCode,
    int CustomerPaymentTermsDays,
    string? CustomerBillingAddress);

public sealed record SaveContractBillingSettingsRequest(
    string InvoicePrefix,
    decimal FeeWithholdRate,
    decimal? FeeWithholdCap = null,
    decimal? MaterialHandlingRate = null,
    int? PaymentTermsDays = null,
    string? PayingOffice = null,
    string? AdminOffice = null,
    string? CageCode = null,
    int? CustomerPaymentTermsDays = null,
    string? CustomerBillingAddress = null);

// ---- Unbilled accrual ----

public sealed record UnbilledAccrualLineDto(Guid ContractId, string ContractNumber, string? ClinNumber, string Description, decimal Amount);

public sealed record UnbilledAccrualDto(
    Guid FiscalPeriodId,
    string FiscalPeriodName,
    DateOnly PeriodEnd,
    IReadOnlyList<UnbilledAccrualLineDto> Lines,
    decimal Amount,
    bool IsPosted,
    Guid? JournalEntryId,
    Guid? ReversalEntryId,
    string? PostedBy,
    DateTime? PostedAtUtc,
    IReadOnlyList<string> Notes);

public interface IInvoiceService
{
    /// <summary>Invoices, newest first. Project managers see only their contracts' invoices.</summary>
    Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default);

    Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Contracts with their CLINs, for the new-invoice wizard.</summary>
    Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default);

    /// <summary>The lines a draft would get, grouped by CLIN, with the funding check. Saves nothing.</summary>
    Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Creates the draft, or regenerates the existing draft for the same contract, type and period (idempotent). Each
    /// hour, ODC and milestone is billed on one invoice only.
    /// </summary>
    Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Recomputes a draft's generated lines (manual adjustments are kept).</summary>
    Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default);

    Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a draft line; its hours and ODCs become billable again.</summary>
    Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves a draft and posts it (debit AR billed, credit revenue). Refused past a CLIN's ceiling, and past its funded
    /// value unless a funding override reason is given.
    /// </summary>
    Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Marks an approved invoice sent to the customer (WAWF/PIEE or email).</summary>
    Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Voids a draft or an unpaid approved invoice: reverses its GL entry and releases what it billed.</summary>
    Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a draft credit memo against an approved invoice; approving it reduces that invoice's balance.</summary>
    Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default);

    /// <summary>Everything the SF 1034 and commercial print views need, with the hours and ODC attachment.</summary>
    Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>A WAWF/PIEE-ready CSV of the invoice (header and line items). No live integration.</summary>
    Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default);

    /// <summary>Billed to date, paid and outstanding for a contract, by CLIN.</summary>
    Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default);

    Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Earned-but-unbilled cost-plus and T&amp;M revenue at a period's end, and whether its accrual is posted.</summary>
    Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Posts the month-end accrual (debit AR unbilled, credit revenue) dated the period's last day, and its reversal on the
    /// first day of the next period. Idempotent per period.
    /// </summary>
    Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);
}

// ---- Milestones ----

public sealed record BillingMilestoneDto(
    Guid Id,
    Guid ContractId,
    Guid ClinId,
    string ClinNumber,
    string Description,
    decimal Amount,
    DateOnly PlannedDate,
    DateOnly? CompletedDate,
    string? CompletedBy,
    Guid? InvoiceId,
    string? InvoiceNumber,
    int Order)
{
    public bool IsBilled => InvoiceId is not null;
}

public sealed record SaveMilestoneRequest(Guid ClinId, string Description, decimal Amount, DateOnly PlannedDate);

public sealed record CompleteMilestoneRequest(DateOnly CompletedDate);

public interface IBillingMilestoneService
{
    Task<IReadOnlyList<BillingMilestoneDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default);

    /// <summary>Milestones belong to firm-fixed-price CLINs.</summary>
    Task<BillingMilestoneDto> CreateAsync(Guid contractId, SaveMilestoneRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes an unbilled milestone.</summary>
    Task<BillingMilestoneDto> UpdateAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<BillingMilestoneDto> CompleteAsync(Guid milestoneId, CompleteMilestoneRequest request, CancellationToken cancellationToken = default);

    /// <summary>Clears the completion of an unbilled milestone.</summary>
    Task<BillingMilestoneDto> ReopenAsync(Guid milestoneId, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid milestoneId, CancellationToken cancellationToken = default);
}

// ---- Payments ----

/// <summary>For a foreign currency invoice, <see cref="Amount"/> is the receivable relieved and <see cref="TransactionAmount"/> what was applied in its currency.</summary>
public sealed record PaymentApplicationDto(Guid InvoiceId, string InvoiceNumber, string ContractNumber, decimal Amount, decimal? TransactionAmount = null);

public sealed record PaymentDto(
    Guid Id,
    Guid CustomerId,
    string CustomerName,
    DateOnly ReceivedOn,
    decimal Amount,
    CashReceiptMethod Method,
    string? Reference,
    string? Notes,
    Guid? JournalEntryId,
    string? JournalEntryNumber,
    bool IsVoid,
    string? VoidReason,
    string CreatedBy,
    DateTime CreatedAtUtc,
    IReadOnlyList<PaymentApplicationDto> Applications,

    // Added by multicurrency: Amount is the cash in the functional currency; a foreign receipt also has its own amount and rate.
    string? CurrencyCode = null,
    decimal TransactionAmount = 0m,
    decimal ExchangeRate = 1m,
    decimal RealizedGainLoss = 0m);

public sealed record PaymentApplicationInput(Guid InvoiceId, decimal Amount);

/// <summary>A cash receipt, applied in full across the customer's open invoices.</summary>
public sealed record RecordPaymentRequest(
    Guid CustomerId,
    DateOnly ReceivedOn,
    decimal Amount,
    CashReceiptMethod Method,
    IReadOnlyList<PaymentApplicationInput> Applications,
    string? Reference = null,
    string? Notes = null,

    /// <summary>
    /// Added by multicurrency: a receipt in another currency. The amount and applications are then in that currency, every
    /// invoice must be in it, and the cash converts at <see cref="ExchangeRate"/> or the spot rate on the receipt date.
    /// </summary>
    string? CurrencyCode = null,
    decimal? ExchangeRate = null);

public sealed record PaymentQuery(Guid? CustomerId = null, DateOnly? From = null, DateOnly? To = null, int Take = 500);

public interface IPaymentService
{
    Task<IReadOnlyList<PaymentDto>> ListAsync(PaymentQuery query, CancellationToken cancellationToken = default);

    Task<PaymentDto> GetAsync(Guid paymentId, CancellationToken cancellationToken = default);

    /// <summary>Open invoices of a customer (oldest due first) to apply cash to.</summary>
    Task<IReadOnlyList<InvoiceSummaryDto>> ListOpenInvoicesAsync(Guid customerId, CancellationToken cancellationToken = default);

    /// <summary>Records the receipt, applies it and posts debit cash, credit AR billed. Applications must add up to the amount.</summary>
    Task<PaymentDto> RecordAsync(RecordPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Voids a receipt: reverses its GL entry and removes its applications.</summary>
    Task<PaymentDto> VoidAsync(Guid paymentId, VoidInvoiceRequest request, CancellationToken cancellationToken = default);
}

// ---- AR reports ----

public sealed record AgingBuckets(decimal Current, decimal Days1To30, decimal Days31To60, decimal Days61To90, decimal Over90)
{
    public static readonly AgingBuckets Zero = new(0, 0, 0, 0, 0);

    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;

    public AgingBuckets Add(AgingBuckets o) =>
        new(Current + o.Current, Days1To30 + o.Days1To30, Days31To60 + o.Days31To60, Days61To90 + o.Days61To90, Over90 + o.Over90);
}

public sealed record AgingRowDto(Guid CustomerId, string CustomerName, Guid? ContractId, string? ContractNumber, AgingBuckets Buckets, int InvoiceCount);

public sealed record ArAgingDto(DateOnly AsOf, IReadOnlyList<AgingRowDto> Customers, IReadOnlyList<AgingRowDto> Contracts, AgingBuckets Totals, IReadOnlyList<InvoiceSummaryDto> Invoices);

public sealed record CustomerStatementLineDto(DateOnly Date, string Kind, string Reference, string Description, decimal Charges, decimal Credits, decimal Balance, Guid? InvoiceId);

public sealed record CustomerStatementDto(
    Guid CustomerId,
    string CustomerName,
    string? BillingAddress,
    DateOnly From,
    DateOnly To,
    decimal OpeningBalance,
    IReadOnlyList<CustomerStatementLineDto> Lines,
    decimal ClosingBalance,
    AgingBuckets Aging,
    IReadOnlyList<InvoiceSummaryDto> OpenInvoices);

public sealed record BillingCustomerDto(Guid Id, string Code, string Name, int PaymentTermsDays, decimal OpenBalance);

public interface IArReportService
{
    /// <summary>Open balances by days past due (current, 1–30, 31–60, 61–90, 90+) by customer and contract.</summary>
    Task<ArAgingDto> GetAgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<CustomerStatementDto> GetStatementAsync(Guid customerId, DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BillingCustomerDto>> ListCustomersAsync(CancellationToken cancellationToken = default);

    /// <summary>Notifies Finance of invoices that went past due (once per invoice and aging bucket). Returns how many were sent.</summary>
    Task<int> SendOverdueRemindersAsync(CancellationToken cancellationToken = default);
}

using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Purchasing;

// Accounts payable, vendors and subcontracts. Finance and administrators use all of it; a project manager can see and
// approve the bill lines routed to them (project cost on their projects). Bills post to the general ledger: project and
// subcontract lines as approved vendor ODCs (so project financials see them), account lines and retainage as an AP
// entry; payments debit accounts payable and credit cash.

public enum VendorState { Active, OnHold, Inactive }

public enum SubcontractPricing { FirmFixedPrice, TimeAndMaterials, LaborHour, CostPlusFixedFee, CostPlusAwardFee, CostPlusIncentiveFee, CostNoFee }

public enum SubcontractState { Draft, Active, Closed }

public enum BillState { Draft, PendingApproval, Approved, Posted, Rejected, Void }

public enum BillLineKind { ProjectCost, Account, Subcontract }

public enum BillLineState { Pending, Approved, Rejected }

public enum PaymentKind { Check, Ach }

public enum PaymentRunState { Draft, Approved, Completed, Cancelled }

public enum PaymentState { Scheduled, Paid, Void }

/// <summary>Pick lists for vendor and subcontract forms.</summary>
public static class PurchasingCatalog
{
    public static IReadOnlyList<string> BusinessSizes { get; } = ["Small", "Large", "Other than small"];

    /// <summary>Socio-economic categories counted in small-business subcontracting reports.</summary>
    public static IReadOnlyList<string> SocioEconomicCategories { get; } = ["SDB", "8(a)", "WOSB", "HUBZone", "VOSB", "SDVOSB", "HBCU/MI"];

    public const string DefaultForm1099Box = "NEC-1";

    public static IReadOnlyList<(string Box, string Label)> Form1099Boxes { get; } =
    [
        ("NEC-1", "1099-NEC box 1: Nonemployee compensation"),
        ("MISC-1", "1099-MISC box 1: Rents"),
        ("MISC-3", "1099-MISC box 3: Other income"),
        ("MISC-10", "1099-MISC box 10: Gross proceeds paid to an attorney"),
    ];

    /// <summary>Clauses most prime contracts flow down; "Add standard clauses" puts these on a subcontract's checklist.</summary>
    public static IReadOnlyList<(string Clause, string Title)> StandardFlowDowns { get; } =
    [
        ("52.203-13", "Contractor Code of Business Ethics and Conduct"),
        ("52.204-21", "Basic Safeguarding of Covered Contractor Information Systems"),
        ("52.204-23", "Prohibition on Kaspersky Lab hardware, software and services"),
        ("52.204-25", "Prohibition on Certain Telecommunications and Video Surveillance Equipment"),
        ("52.219-8", "Utilization of Small Business Concerns"),
        ("52.222-26", "Equal Opportunity"),
        ("52.222-50", "Combating Trafficking in Persons"),
        ("52.244-6", "Subcontracts for Commercial Products and Commercial Services"),
        ("252.204-7012", "Safeguarding Covered Defense Information and Cyber Incident Reporting"),
        ("252.204-7020", "NIST SP 800-171 DoD Assessment Requirements"),
        ("252.225-7048", "Export-Controlled Items"),
    ];

    /// <summary>"•••••4821" (or empty when nothing is on file).</summary>
    public static string MaskTaxId(string? last4) => string.IsNullOrEmpty(last4) ? "" : $"•••••{last4}";
}

// ---- Vendors ----

public sealed record VendorDto(
    Guid Id,
    string Code,
    string Name,
    VendorState Status,
    bool Is1099,
    string Form1099Box,

    /// <summary>The TIN masked to its last four digits; the full TIN is only returned by <see cref="IVendorService.RevealTaxIdAsync"/>.</summary>
    string TaxIdMasked,
    bool HasFullTaxId,
    string? TaxIdKind,
    string? Uei,
    string? BusinessSize,
    IReadOnlyList<string> SocioEconomicCategories,
    int PaymentTermsDays,
    string? RemitToAddress,
    string? ContactName,
    string? ContactEmail,
    string? ContactPhone,
    Guid? W9FileId,
    string? W9FileName,
    DateTime? W9UploadedAtUtc)
{
    public bool IsSmallBusiness => string.Equals(BusinessSize, "Small", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Creates or changes a vendor. A vendor with the same UEI is refused; one with a similar name is refused unless
/// <see cref="ConfirmDuplicate"/> (see <see cref="IVendorService.FindDuplicatesAsync"/>).
/// </summary>
public sealed record SaveVendorRequest(
    string Code,
    string Name,
    VendorState Status = VendorState.Active,
    bool Is1099 = false,
    string? Form1099Box = null,
    string? Uei = null,
    string? BusinessSize = null,
    IReadOnlyList<string>? SocioEconomicCategories = null,
    int PaymentTermsDays = 30,
    string? RemitToAddress = null,
    string? ContactName = null,
    string? ContactEmail = null,
    string? ContactPhone = null,
    bool ConfirmDuplicate = false);

/// <summary>A full TIN: nine digits, dashes optional. <see cref="Kind"/> is "EIN" or "SSN".</summary>
public sealed record SetVendorTaxIdRequest(string TaxId, string Kind = "EIN");

/// <summary>A revealed TIN (formatted 12-3456789 or 123-45-6789). Revealing is logged as a security event.</summary>
public sealed record VendorTaxIdDto(Guid VendorId, string Kind, string TaxId);

public sealed record VendorDuplicateDto(Guid VendorId, string Code, string Name, string Reason);

public interface IVendorService
{
    Task<IReadOnlyList<VendorDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default);

    Task<VendorDto> GetAsync(Guid vendorId, CancellationToken cancellationToken = default);

    /// <summary>Existing vendors with the same UEI or a similar name (ignoring case, punctuation and suffixes such as LLC).</summary>
    Task<IReadOnlyList<VendorDuplicateDto>> FindDuplicatesAsync(string name, string? uei, Guid? excludeVendorId = null, CancellationToken cancellationToken = default);

    Task<VendorDto> CreateAsync(SaveVendorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a vendor, including its status (OnHold blocks new bills and payments). Audited.</summary>
    Task<VendorDto> UpdateAsync(Guid vendorId, SaveVendorRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stores the full TIN encrypted, and its last four digits in the clear. Audited without the TIN.</summary>
    Task<VendorDto> SetTaxIdAsync(Guid vendorId, SetVendorTaxIdRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finance and administrators only; records a <c>vendor.tax_id_revealed</c> security event.</summary>
    Task<VendorTaxIdDto> RevealTaxIdAsync(Guid vendorId, CancellationToken cancellationToken = default);

    Task<VendorDto> AttachW9Async(Guid vendorId, string fileName, Stream content, CancellationToken cancellationToken = default);

    Task<ReceiptContent> OpenW9Async(Guid vendorId, CancellationToken cancellationToken = default);

    /// <summary>A link that downloads the vendor's W-9 (the browser sends the sign-in cookie).</summary>
    string GetW9Url(Guid vendorId);
}

// ---- Subcontracts ----

/// <summary>
/// A subcontract with its running totals. Billed to date counts submitted, approved and posted subcontract invoices
/// (and vendor ODCs charged to the subcontract outside AP).
/// </summary>
public sealed record SubcontractDto(
    Guid Id,
    string Number,
    string Description,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    Guid? PrimeContractId,
    string? PrimeContractNumber,
    Guid? ProjectId,
    string? ProjectCode,
    SubcontractPricing Pricing,
    SubcontractState Status,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    decimal FundedValue,
    decimal RetainagePercent,
    decimal BilledToDate,
    decimal RetainageHeld,
    decimal PaidToDate,
    int FlowDownsIncluded,
    int FlowDownsTotal)
{
    public decimal RemainingFunding => FundedValue - BilledToDate;

    public decimal RemainingCeiling => CeilingValue - BilledToDate;
}

public sealed record SubcontractFundingDto(
    Guid Id,
    string Number,
    DateOnly EffectiveDate,
    decimal FundedChange,
    decimal CeilingChange,
    string Description,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record FlowDownDto(Guid Id, string ClauseNumber, string? Title, bool IsIncluded, string? Note);

/// <summary>An invoice against the subcontract: an AP bill line, or a vendor ODC entered outside AP.</summary>
public sealed record SubcontractInvoiceDto(Guid? BillId, string? BillNumber, string? InvoiceNumber, DateOnly InvoiceDate, decimal Amount, decimal Retainage, string Status);

public sealed record SubcontractDetailDto(
    SubcontractDto Subcontract,
    IReadOnlyList<SubcontractFundingDto> Funding,
    IReadOnlyList<FlowDownDto> FlowDowns,
    IReadOnlyList<SubcontractInvoiceDto> Invoices);

/// <summary>Creates or changes a subcontract. The ceiling can be edited while it's a draft; afterwards use a modification.</summary>
public sealed record SaveSubcontractRequest(
    Guid VendorId,
    string Number,
    string Description,
    SubcontractPricing Pricing,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal CeilingValue,
    Guid? PrimeContractId = null,
    Guid? ProjectId = null,
    decimal RetainagePercent = 0);

/// <summary>A modification: changes the funded value and/or the ceiling. Funded can't exceed the ceiling or drop below billed.</summary>
public sealed record AddSubcontractFundingRequest(string Number, DateOnly EffectiveDate, decimal FundedChange, decimal CeilingChange, string Description);

public sealed record SaveFlowDownRequest(string ClauseNumber, string? Title, bool IsIncluded, string? Note);

public interface ISubcontractService
{
    Task<IReadOnlyList<SubcontractDto>> ListAsync(Guid? vendorId = null, CancellationToken cancellationToken = default);

    Task<SubcontractDetailDto> GetAsync(Guid subcontractId, CancellationToken cancellationToken = default);

    Task<SubcontractDetailDto> CreateAsync(SaveSubcontractRequest request, CancellationToken cancellationToken = default);

    Task<SubcontractDetailDto> UpdateAsync(Guid subcontractId, SaveSubcontractRequest request, CancellationToken cancellationToken = default);

    /// <summary>Draft → Active (the vendor must not be inactive). Audited.</summary>
    Task<SubcontractDetailDto> ActivateAsync(Guid subcontractId, CancellationToken cancellationToken = default);

    Task<SubcontractDetailDto> CloseAsync(Guid subcontractId, CancellationToken cancellationToken = default);

    /// <summary>Adds a funding action (modification) and updates the running funded value and ceiling. Audited.</summary>
    Task<SubcontractDetailDto> AddFundingAsync(Guid subcontractId, AddSubcontractFundingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds or updates (by clause number) a flow-down clause.</summary>
    Task<SubcontractDetailDto> SaveFlowDownAsync(Guid subcontractId, SaveFlowDownRequest request, CancellationToken cancellationToken = default);

    Task<SubcontractDetailDto> RemoveFlowDownAsync(Guid subcontractId, Guid flowDownId, CancellationToken cancellationToken = default);

    /// <summary>Adds the <see cref="PurchasingCatalog.StandardFlowDowns"/> not yet on the checklist (undecided).</summary>
    Task<SubcontractDetailDto> AddStandardFlowDownsAsync(Guid subcontractId, CancellationToken cancellationToken = default);
}

// ---- Bills ----

public sealed record BillSummaryDto(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    DateOnly DueDate,
    BillState Status,
    decimal Amount,
    decimal RetainageAmount,
    decimal PaidAmount,
    string EnteredBy,
    string? PostingProblem)
{
    /// <summary>What's owed now: the amount less retainage.</summary>
    public decimal Payable => Amount - RetainageAmount;

    public decimal Balance => Payable - PaidAmount;

    public bool IsPaid => Status == BillState.Posted && Balance == 0;
}

public sealed record BillLineDto(
    Guid Id,
    int LineNumber,
    BillLineKind Kind,
    string Description,
    decimal Amount,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    OdcCostType? OdcType,
    Guid? SubcontractId,
    string? SubcontractNumber,
    Guid? OdcEntryId,
    Guid? AccountId,
    string? AccountNumber,
    string? AccountName,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    decimal RetainageAmount,
    BillLineState Status,

    /// <summary>The project manager's email, or "Finance".</summary>
    string RoutedTo,
    string? DecidedBy,
    DateTime? DecidedAtUtc);

public sealed record BillDuplicateDto(Guid BillId, string Number, string InvoiceNumber, DateOnly InvoiceDate, decimal Amount, BillState Status, string Reason);

public sealed record BillPaymentDto(Guid PaymentId, Guid RunId, string RunNumber, DateOnly? PaidOn, decimal Amount, PaymentKind Method, string? Reference, PaymentState Status);

public sealed record BillDto(
    BillSummaryDto Bill,
    string? Description,
    IReadOnlyList<BillLineDto> Lines,
    IReadOnlyList<BillDuplicateDto> PossibleDuplicates,
    IReadOnlyList<BillPaymentDto> Payments,
    Guid? JournalEntryId,
    string? FundingOverrideBy,
    string? FundingOverrideReason,
    string? RejectionReason,
    string? VoidReason,
    bool CanEdit,
    bool CanApprove);

/// <summary>
/// One bill line. <see cref="BillLineKind.ProjectCost"/>: a WBS element and ODC type (or <see cref="OdcEntryId"/> to link an
/// existing vendor ODC, whose WBS, type and amount it takes). <see cref="BillLineKind.Account"/>: a GL account and org.
/// <see cref="BillLineKind.Subcontract"/>: a subcontract and a WBS element on its project.
/// </summary>
public sealed record SaveBillLineRequest(
    BillLineKind Kind,
    string Description,
    decimal Amount,
    Guid? WbsElementId = null,
    OdcCostType? OdcType = null,
    Guid? SubcontractId = null,
    Guid? OdcEntryId = null,
    Guid? AccountId = null,
    Guid? OrgUnitId = null);

/// <summary>A bill. The due date defaults to the invoice date plus the vendor's payment terms.</summary>
public sealed record SaveBillRequest(
    Guid VendorId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    IReadOnlyList<SaveBillLineRequest> Lines,
    DateOnly? DueDate = null,
    string? Description = null);

/// <summary>
/// Submits a bill for approval. A possible duplicate (same vendor, amount and date) needs <see cref="ConfirmPossibleDuplicate"/>;
/// subcontract lines beyond the funded value need a Finance user's <see cref="FundingOverrideReason"/>.
/// </summary>
public sealed record SubmitBillRequest(bool ConfirmPossibleDuplicate = false, string? FundingOverrideReason = null);

public sealed record BillReasonRequest(string Reason);

public sealed record BillQuery(BillState? Status = null, Guid? VendorId = null, string? Search = null, int Take = 500);

public sealed record DuplicateCheckRequest(Guid VendorId, string? InvoiceNumber, DateOnly InvoiceDate, decimal Amount, Guid? ExcludeBillId = null);

/// <summary>A bill waiting on the current user, with the lines they can approve.</summary>
public sealed record BillApprovalItemDto(BillSummaryDto Bill, IReadOnlyList<BillLineDto> Lines);

/// <summary>Where a vendor ODC came from in AP (for the project ODC page).</summary>
public sealed record OdcBillLinkDto(Guid OdcEntryId, Guid? VendorId, string? VendorCode, string? VendorName, Guid? BillId, string? BillNumber);

/// <summary>A vendor ODC (entered on the project, not yet on a bill) that a bill line can link to.</summary>
public sealed record LinkableOdcDto(Guid OdcEntryId, string ProjectCode, string WbsCode, OdcCostType Type, DateOnly IncurredOn, decimal Amount, string Description, string? InvoiceNumber, OdcApprovalState Status);

public interface IApBillService
{
    Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default);

    /// <summary>Finance, administrators, and approvers the bill has lines routed to.</summary>
    Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default);

    /// <summary>Same vendor and invoice number, or same vendor, amount and invoice date (void bills excluded).</summary>
    Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default);

    /// <summary>Saves a draft. A repeated invoice number for the vendor is refused; an OnHold or inactive vendor is refused.</summary>
    Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default);

    Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default);

    Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Routes each line: project cost and subcontract lines to the project's manager (Finance when the manager entered the
    /// bill or there's none), account lines to Finance. Approvers are notified.
    /// </summary>
    Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default);

    /// <summary>Bills with lines waiting on the current user.</summary>
    Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Approves the lines routed to the current user (never their own entry). When every line is approved the bill posts:
    /// project and subcontract lines as approved vendor ODCs (reusing a linked ODC's posting), the rest as an AP entry.
    /// </summary>
    Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default);

    /// <summary>Rejects the bill back to the person who entered it (reason required).</summary>
    Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default);

    /// <summary>Retries posting an approved bill whose posting failed (for example, after a period was reopened).</summary>
    Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default);

    /// <summary>Voids a bill that hasn't posted (draft, rejected or waiting for approval).</summary>
    Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default);

    /// <summary>Vendor and bill for these ODCs, limited to projects whose financials the user can read.</summary>
    Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default);
}

// ---- Payments ----

public sealed record DueBillDto(BillSummaryDto Bill, decimal ScheduledAmount)
{
    /// <summary>The balance not yet in an open payment run.</summary>
    public decimal Available => Bill.Balance - ScheduledAmount;
}

public sealed record DueBillQuery(DateOnly? DueOnOrBefore = null, Guid? VendorId = null);

public sealed record PaymentDto(
    Guid Id,
    Guid RunId,
    string RunNumber,
    Guid BillId,
    string BillNumber,
    string InvoiceNumber,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    decimal Amount,
    PaymentKind Method,
    PaymentState Status,
    string? Reference,
    DateOnly? PaidOn,
    Guid? JournalEntryId,
    string? VoidReason);

public sealed record PaymentRunDto(
    Guid Id,
    string Number,
    DateOnly PaymentDate,
    PaymentKind Method,
    PaymentRunState Status,
    string CreatedBy,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    IReadOnlyList<PaymentDto> Payments)
{
    public decimal Total => Payments.Where(p => p.Status != PaymentState.Void).Sum(p => p.Amount);
}

/// <summary>Pay <see cref="Amount"/> of a bill (up to its available balance; partial payments are fine).</summary>
public sealed record PaymentRunItemRequest(Guid BillId, decimal Amount);

public sealed record CreatePaymentRunRequest(DateOnly PaymentDate, PaymentKind Method, IReadOnlyList<PaymentRunItemRequest> Items);

public sealed record PaymentReferenceRequest(Guid PaymentId, string Reference);

/// <summary>Records an approved run as paid on <see cref="PaidOn"/>, with each payment's check number or ACH reference.</summary>
public sealed record RecordPaymentsRequest(DateOnly PaidOn, IReadOnlyList<PaymentReferenceRequest> References);

public sealed record VoidPaymentRequest(string Reason);

public sealed record PurchasingExportFile(string FileName, string ContentType, string Content);

public interface IApPaymentService
{
    /// <summary>Posted bills with an open balance, optionally due by a date and for one vendor.</summary>
    Task<IReadOnlyList<DueBillDto>> ListDueBillsAsync(DueBillQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PaymentRunDto>> ListRunsAsync(CancellationToken cancellationToken = default);

    Task<PaymentRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>A draft run. OnHold and inactive vendors' bills are refused.</summary>
    Task<PaymentRunDto> CreateRunAsync(CreatePaymentRunRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finance approves the run (not the person who created it).</summary>
    Task<PaymentRunDto> ApproveRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<PaymentRunDto> CancelRunAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>Marks each payment paid and posts it (debit accounts payable, credit cash).</summary>
    Task<PaymentRunDto> RecordPaymentsAsync(Guid runId, RecordPaymentsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Voids a paid payment: reverses its entry and reopens the bill's balance.</summary>
    Task<PaymentDto> VoidPaymentAsync(Guid paymentId, VoidPaymentRequest request, CancellationToken cancellationToken = default);

    /// <summary>An ACH-style CSV of the run (no bank integration).</summary>
    Task<PurchasingExportFile> ExportAchAsync(Guid runId, CancellationToken cancellationToken = default);

    /// <summary>A positive-pay CSV of the run's checks for the bank.</summary>
    Task<PurchasingExportFile> ExportPositivePayAsync(Guid runId, CancellationToken cancellationToken = default);
}

// ---- Reports ----

/// <summary>A vendor's open payable balance (excluding retainage) by days past due.</summary>
public sealed record AgingRowDto(
    Guid VendorId,
    string VendorCode,
    string VendorName,
    decimal Current,
    decimal Days1To30,
    decimal Days31To60,
    decimal Days61To90,
    decimal Over90,
    decimal Retainage)
{
    public decimal Total => Current + Days1To30 + Days31To60 + Days61To90 + Over90;
}

public sealed record AgingReportDto(DateOnly AsOf, IReadOnlyList<AgingRowDto> Rows)
{
    public decimal Total => Rows.Sum(r => r.Total);
}

public sealed record VendorLedgerLineDto(DateOnly Date, string Kind, string Reference, string Description, decimal Charges, decimal Payments, decimal Balance);

public sealed record VendorLedgerDto(
    Guid VendorId,
    string VendorCode,
    string VendorName,
    DateOnly? From,
    DateOnly? To,
    decimal OpeningBalance,
    IReadOnlyList<VendorLedgerLineDto> Lines,
    decimal ClosingBalance);

public sealed record Form1099RowDto(Guid VendorId, string VendorCode, string VendorName, string TaxIdMasked, string Box, decimal Amount, int Payments);

/// <summary>Payments made in <see cref="Year"/> to 1099 vendors, by box. <see cref="Rows"/> meet the threshold ($600).</summary>
public sealed record Form1099ReportDto(int Year, decimal Threshold, IReadOnlyList<Form1099RowDto> Rows, IReadOnlyList<Form1099RowDto> BelowThreshold);

public sealed record SmallBusinessRowDto(string Category, int Vendors, decimal Amount, decimal Percent);

/// <summary>Posted AP dollars (by invoice date) by business size and socio-economic category, for subcontracting plan reports.</summary>
public sealed record SmallBusinessReportDto(DateOnly From, DateOnly To, decimal Total, IReadOnlyList<SmallBusinessRowDto> BySize, IReadOnlyList<SmallBusinessRowDto> BySocioEconomic);

public interface IApReportService
{
    Task<AgingReportDto> GetAgingAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    Task<VendorLedgerDto> GetVendorLedgerAsync(Guid vendorId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    Task<Form1099ReportDto> Get1099Async(int year, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SubcontractDto>> GetSubcontractStatusAsync(CancellationToken cancellationToken = default);

    Task<SmallBusinessReportDto> GetSmallBusinessAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);

    Task<PurchasingExportFile> ExportAgingCsvAsync(DateOnly asOf, CancellationToken cancellationToken = default);

    /// <summary>The 1099 report as CSV. TINs are masked: the full TIN never leaves the vendor record in an export.</summary>
    Task<PurchasingExportFile> Export1099CsvAsync(int year, CancellationToken cancellationToken = default);
}

/// <summary>Security event names for purchasing (see <c>Contracts.Security.SecurityEventTypes</c>).</summary>
public static class PurchasingSecurityEvents
{
    public const string TaxIdRevealed = "vendor.tax_id_revealed";
    public const string TaxIdChanged = "vendor.tax_id_changed";
    public const string Export1099 = "export.ap_1099";
    public const string ExportPayments = "export.ap_payments";
}

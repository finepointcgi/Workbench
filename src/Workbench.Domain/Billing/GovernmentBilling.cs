namespace Workbench.Domain.Billing;

/// <summary>Which printed layout a contract's invoices use.</summary>
public enum InvoiceFormat
{
    /// <summary>The company's own (commercial) invoice layout.</summary>
    Custom,

    /// <summary>SF 1034 public voucher, with SF 1035 continuation sheets when the lines don't fit.</summary>
    Sf1034,
}

/// <summary>Contract financing: how the government pays before delivery.</summary>
public enum ContractFinancing
{
    None,

    /// <summary>Progress payments based on cost (FAR 52.232-16), requested on SF 1443.</summary>
    ProgressPayments,

    /// <summary>Performance-based payments (FAR 32.10) for completed events or criteria.</summary>
    PerformanceBased,
}

/// <summary>Which costs count toward progress payments (SF 1443 line 11).</summary>
public enum ProgressPaymentCostBasis
{
    /// <summary>All eligible costs incurred, paid or not (small business).</summary>
    IncurredCosts,

    /// <summary>Only costs actually paid (large business).</summary>
    PaidCosts,
}

/// <summary>The PIEE (WAWF) document a delivery invoice is sent as.</summary>
public enum PieeDocumentType
{
    TwoInOne,
    CostVoucher,
    Combo,
    ProgressPayment,
    PerformanceBasedPayment,
}

public enum PieeSubmissionStatus
{
    NotSubmitted,
    Submitted,
    Accepted,
    Rejected,
}

/// <summary>
/// Government billing setup for one contract: the invoice format, contract financing (progress payments or
/// performance-based payments) and the PIEE routing codes. One row per contract; missing means no financing and the
/// custom format.
/// </summary>
public sealed class ContractGovernmentBilling : Entity
{
    public Guid ContractId { get; set; }
    public InvoiceFormat Format { get; set; } = InvoiceFormat.Custom;
    public ContractFinancing Financing { get; set; } = ContractFinancing.None;
    public bool IsSmallBusiness { get; set; }

    /// <summary>Progress payment rate (0.80 large business, 0.90 small business). SF 1443 item 6a.</summary>
    public decimal ProgressPaymentRate { get; set; }

    /// <summary>Share of each delivery recouped against outstanding financing. SF 1443 item 6b for progress payments.</summary>
    public decimal LiquidationRate { get; set; }

    public ProgressPaymentCostBasis CostBasis { get; set; } = ProgressPaymentCostBasis.PaidCosts;

    /// <summary>SF 1443 item 5. Null: the contract's ceiling value.</summary>
    public decimal? ContractPrice { get; set; }

    public string? IssueByDodaac { get; set; }
    public string? AdminByDodaac { get; set; }
    public string? PayOfficeDodaac { get; set; }
    public string? ShipToDodaac { get; set; }

    /// <summary>The PIEE document for delivery (FFP and T&amp;M) invoices: 2-in-1 for services, Combo for supplies.</summary>
    public PieeDocumentType DeliveryDocument { get; set; } = PieeDocumentType.TwoInOne;

    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A Contractor's Request for Progress Payment (SF 1443). Keeps the figures it was computed from, so the form prints the
/// same later; the amount requested is billed on <see cref="InvoiceId"/> (an invoice of type ProgressPayment).
/// </summary>
public sealed class ProgressPaymentRequest : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>Item 8a: 1, 2, 3 ... per contract.</summary>
    public int Number { get; set; }

    /// <summary>Item 8b.</summary>
    public DateOnly RequestDate { get; set; }

    /// <summary>Section II: costs through this date.</summary>
    public DateOnly CostsThrough { get; set; }

    public bool IsSmallBusiness { get; set; }
    public decimal ContractPrice { get; set; }
    public decimal ProgressPaymentRate { get; set; }
    public decimal LiquidationRate { get; set; }
    public ProgressPaymentCostBasis CostBasis { get; set; }

    /// <summary>Eligible costs paid / not yet paid (line 11 takes both, or only the paid ones).</summary>
    public decimal PaidCosts { get; set; }

    public decimal UnpaidCosts { get; set; }

    /// <summary>Line 12a.</summary>
    public decimal IncurredToDate { get; set; }

    /// <summary>Line 12b.</summary>
    public decimal EstimateToComplete { get; set; }

    /// <summary>Lines 14a, 14b, 14d.</summary>
    public decimal SubcontractorPaid { get; set; }

    public decimal SubcontractorLiquidated { get; set; }
    public decimal SubcontractorApproved { get; set; }

    /// <summary>Line 18.</summary>
    public decimal PreviousRequested { get; set; }

    /// <summary>Line 20a.</summary>
    public decimal CostsOfDelivered { get; set; }

    /// <summary>Line 21a.</summary>
    public decimal PriceOfDelivered { get; set; }

    /// <summary>Line 21d.</summary>
    public decimal UnliquidatedAdvances { get; set; }

    /// <summary>Line 23.</summary>
    public decimal LiquidatedToDate { get; set; }

    /// <summary>Line 26: what is billed.</summary>
    public decimal Amount { get; set; }

    public Guid? InvoiceId { get; set; }
    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// A performance-based payment event (FAR 32.1004): what has to be accomplished, its amount, and the evidence that it
/// was. Completed, unbilled events go on the next performance-based payment invoice.
/// </summary>
public sealed class PerformanceBasedPaymentEvent : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>Optional: the CLIN (deliverable item) the event belongs to; null for the whole contract.</summary>
    public Guid? ClinId { get; set; }

    public int Order { get; set; }
    public required string Description { get; set; }

    /// <summary>The objective criteria for completion.</summary>
    public string? Criteria { get; set; }

    public decimal Amount { get; set; }
    public DateOnly PlannedDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string? CompletedBy { get; set; }

    /// <summary>What shows the event was accomplished (a test report, an acceptance memo); required to complete it.</summary>
    public string? Evidence { get; set; }

    /// <summary>Optional link or document number for the evidence.</summary>
    public string? EvidenceReference { get; set; }

    /// <summary>The invoice billing it; cleared when that invoice is voided or the draft deleted.</summary>
    public Guid? InvoiceId { get; set; }
}

/// <summary>An invoice's PIEE (WAWF) document and its submission status, tracked by hand. One per invoice.</summary>
public sealed class PieeSubmission : Entity
{
    public Guid InvoiceId { get; set; }
    public PieeDocumentType DocumentType { get; set; }
    public PieeSubmissionStatus Status { get; set; } = PieeSubmissionStatus.NotSubmitted;
    public string? FileName { get; set; }
    public DateTime? ExportedAtUtc { get; set; }
    public string? ExportedBy { get; set; }

    /// <summary>The PIEE document or shipment number, once submitted.</summary>
    public string? Reference { get; set; }

    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmittedBy { get; set; }
    public string? RejectionReason { get; set; }
    public DateTime? StatusChangedAtUtc { get; set; }
    public string? StatusChangedBy { get; set; }
}

using Workbench.Domain.ProjectAccounting;

namespace Workbench.Domain.Purchasing;

/// <summary>
/// A vendor's full taxpayer identification number, kept apart from <see cref="Vendor"/> so the vendor row (and anything
/// that reads it) never carries it. <see cref="ProtectedValue"/> is ASP.NET Data Protection ciphertext.
/// </summary>
public sealed class VendorTaxId : Entity
{
    public Guid VendorId { get; set; }

    /// <summary>"EIN" or "SSN".</summary>
    public required string Kind { get; set; }

    public required string ProtectedValue { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A change to a subcontract's funding or ceiling (a subcontract modification). Append-only.</summary>
public sealed class SubcontractFundingAction : Entity
{
    public Guid SubcontractId { get; set; }

    /// <summary>For example "M001".</summary>
    public required string Number { get; set; }

    public DateOnly EffectiveDate { get; set; }
    public decimal FundedChange { get; set; }
    public decimal CeilingChange { get; set; }
    public required string Description { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One FAR/DFARS clause on a subcontract's flow-down checklist.</summary>
public sealed class SubcontractFlowDown : Entity
{
    public Guid SubcontractId { get; set; }

    /// <summary>For example "52.204-21" or "252.204-7012".</summary>
    public required string ClauseNumber { get; set; }

    public string? Title { get; set; }
    public bool IsIncluded { get; set; }
    public string? Note { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ApBillStatus
{
    Draft,
    PendingApproval,
    Approved,
    Posted,
    Rejected,
    Void,
}

public enum ApBillLineKind
{
    /// <summary>A direct cost on a project/WBS; becomes (or links) an <see cref="OdcEntry"/>.</summary>
    ProjectCost,

    /// <summary>A GL expense or asset account with an organization (overhead purchases such as rent).</summary>
    Account,

    /// <summary>A subcontractor invoice against a <see cref="Subcontract"/>; also becomes an ODC on the subcontract's project.</summary>
    Subcontract,
}

public enum ApLineStatus
{
    Pending,
    Approved,
    Rejected,
}

/// <summary>A vendor invoice (voucher) in accounts payable.</summary>
public sealed class ApBill : Entity
{
    public Guid VendorId { get; set; }

    /// <summary>Our voucher number, for example "V-000042".</summary>
    public required string Number { get; set; }

    /// <summary>The vendor's invoice number.</summary>
    public required string InvoiceNumber { get; set; }

    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public string? Description { get; set; }
    public ApBillStatus Status { get; set; } = ApBillStatus.Draft;

    /// <summary>Sum of the lines.</summary>
    public decimal Amount { get; set; }

    /// <summary>Held back from subcontract lines; owed later, not paid with the bill.</summary>
    public decimal RetainageAmount { get; set; }

    /// <summary>Paid by non-void payments (a running total maintained with the payments).</summary>
    public decimal PaidAmount { get; set; }

    public required string EnteredBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? PostedAtUtc { get; set; }

    /// <summary>The AP journal entry for account lines and retainage (project and subcontract lines post as ODCs).</summary>
    public Guid? JournalEntryId { get; set; }

    /// <summary>Why the last posting attempt failed (closed period, missing rule); cleared when posted.</summary>
    public string? PostingProblem { get; set; }

    /// <summary>Set when Finance let a subcontract invoice exceed the funded value.</summary>
    public string? FundingOverrideBy { get; set; }
    public string? FundingOverrideReason { get; set; }

    /// <summary>Set when the person submitting confirmed a possible duplicate (same amount and date).</summary>
    public string? DuplicateConfirmedBy { get; set; }

    public string? RejectionReason { get; set; }
    public string? VoidReason { get; set; }

    /// <summary>The invoice copy: a <c>StoredFile</c> id.</summary>
    public Guid? AttachmentFileId { get; set; }

    public List<ApBillLine> Lines { get; } = [];

    /// <summary>What's owed now: the amount less retainage.</summary>
    public decimal Payable => Amount - RetainageAmount;

    public decimal Balance => Payable - PaidAmount;

    // ---- Added by multicurrency (additive). Amount, RetainageAmount and PaidAmount stay in the functional currency. ----

    /// <summary>The vendor's invoice currency when it isn't the functional currency; null for functional-currency bills.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>Functional units per unit of <see cref="CurrencyCode"/> on the invoice date (1 for functional-currency bills).</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    /// <summary>The bill in its own currency (0 for functional-currency bills).</summary>
    public decimal TransactionAmount { get; set; }

    public decimal TransactionRetainage { get; set; }

    /// <summary>Paid in the bill's currency by non-void payments.</summary>
    public decimal TransactionPaid { get; set; }

    public bool IsForeignCurrency => CurrencyCode is not null;

    /// <summary>What's left to pay in the bill's currency.</summary>
    public decimal TransactionBalance => TransactionAmount - TransactionRetainage - TransactionPaid;
}

public sealed class ApBillLine : Entity
{
    public Guid BillId { get; set; }
    public int LineNumber { get; set; }
    public ApBillLineKind Kind { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }

    // Project cost and subcontract lines.
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public OdcType? OdcType { get; set; }
    public Guid? SubcontractId { get; set; }

    /// <summary>The ODC this line created or links to (an existing vendor ODC is reused, never posted twice).</summary>
    public Guid? OdcEntryId { get; set; }

    // Account lines.
    public Guid? AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }

    public decimal RetainageAmount { get; set; }

    public ApLineStatus Status { get; set; } = ApLineStatus.Pending;

    /// <summary>The project manager this line is routed to; null when it's routed to Finance.</summary>
    public string? ApproverEmail { get; set; }

    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }

    // ---- Added by procure-to-pay (additive): three-way match against a purchase order line ----

    /// <summary>The purchase order line this line bills; the match checks it against receipts and the PO price.</summary>
    public Guid? PurchaseOrderLineId { get; set; }

    public decimal? Quantity { get; set; }
    public decimal? UnitPrice { get; set; }

    /// <summary>Why the three-way match failed when last checked; null when it matched (or there's no PO line).</summary>
    public string? MatchProblem { get; set; }

    /// <summary>Set when Finance let a mismatched line through.</summary>
    public string? MatchOverrideBy { get; set; }
    public string? MatchOverrideReason { get; set; }
    public DateTime? MatchOverrideAtUtc { get; set; }

    /// <summary>Added by multicurrency: the line in the bill's currency (null on functional-currency bills); <see cref="Amount"/> is functional.</summary>
    public decimal? TransactionAmount { get; set; }
}

public enum PaymentMethod
{
    Check,
    Ach,
}

public enum PaymentRunStatus
{
    Draft,
    Approved,
    Completed,
    Cancelled,
}

public sealed class ApPaymentRun : Entity
{
    /// <summary>For example "PR-000003".</summary>
    public required string Number { get; set; }

    public DateOnly PaymentDate { get; set; }
    public PaymentMethod Method { get; set; }
    public PaymentRunStatus Status { get; set; } = PaymentRunStatus.Draft;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
}

public enum ApPaymentStatus
{
    Scheduled,
    Paid,
    Void,
}

/// <summary>One payment against one bill (partial payments are allowed).</summary>
public sealed class ApPayment : Entity
{
    public Guid RunId { get; set; }
    public Guid BillId { get; set; }
    public Guid VendorId { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public ApPaymentStatus Status { get; set; } = ApPaymentStatus.Scheduled;

    /// <summary>The check number or ACH trace reference.</summary>
    public string? Reference { get; set; }

    public DateOnly? PaidOn { get; set; }
    public Guid? JournalEntryId { get; set; }
    public string? PaidBy { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }

    // ---- Added by multicurrency (additive). For a foreign bill, Amount is the liability relieved at the bill's rate. ----

    /// <summary>The bill's currency when it isn't the functional currency.</summary>
    public string? CurrencyCode { get; set; }

    /// <summary>The paid amount in the bill's currency (0 for functional-currency payments).</summary>
    public decimal TransactionAmount { get; set; }

    /// <summary>The rate on the payment date (1 for functional-currency payments).</summary>
    public decimal ExchangeRate { get; set; } = 1m;

    /// <summary>The cash paid in the functional currency (the transaction amount at the payment rate); null means <see cref="Amount"/>.</summary>
    public decimal? CashAmount { get; set; }

    /// <summary>Gain (positive) or loss (negative): the liability relieved less the cash paid.</summary>
    public decimal RealizedGainLoss { get; set; }
}

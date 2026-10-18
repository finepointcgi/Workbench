namespace Workbench.Domain.Billing;

/// <summary>What an invoice bills. A contract with mixed CLINs gets one invoice per type and period.</summary>
public enum InvoiceType
{
    /// <summary>Time and materials or labor hour: approved hours × bill rate, plus ODCs at cost.</summary>
    TimeAndMaterials,

    /// <summary>Cost reimbursement: allowable burdened cost at provisional rates, plus fee less any withhold.</summary>
    CostPlus,

    /// <summary>Firm fixed price: completed milestones.</summary>
    FixedPriceMilestone,

    /// <summary>Year-end adjustment of cost-plus billings from provisional to actual indirect rates.</summary>
    TrueUp,

    /// <summary>A credit against an approved invoice.</summary>
    CreditMemo,
}

public enum InvoiceStatus
{
    Draft,
    Approved,
    Sent,
    PartiallyPaid,
    Paid,
    Void,
}

public enum InvoiceLineKind
{
    /// <summary>Hours for one labor category at its bill rate (T&amp;M), or direct labor cost (cost-plus).</summary>
    Labor,

    /// <summary>Other direct costs of one type, at cost.</summary>
    Odc,

    /// <summary>Material handling on T&amp;M materials.</summary>
    MaterialHandling,

    /// <summary>Indirect cost (fringe, overhead, G&amp;A) on cost-plus labor.</summary>
    Burden,

    /// <summary>Fee on cost-plus cost.</summary>
    Fee,

    /// <summary>Negative: the share of fee withheld until the final invoice.</summary>
    FeeWithhold,

    /// <summary>Release of previously withheld fee (final invoice).</summary>
    WithholdRelease,

    /// <summary>A completed FFP milestone.</summary>
    Milestone,

    /// <summary>A manual adjustment, true-up or credit.</summary>
    Adjustment,
}

/// <summary>
/// A customer invoice (public voucher) for one contract and billing period. Lines are grouped by CLIN. Approving posts it
/// to the general ledger (<see cref="JournalEntryId"/>); voiding reverses that entry and releases the billed hours, ODCs
/// and milestones so they can be billed again.
/// </summary>
public sealed class Invoice : Entity
{
    public Guid ContractId { get; set; }
    public Guid CustomerId { get; set; }

    /// <summary>Per-contract sequence (1, 2, ...); <see cref="Number"/> is the prefix plus this sequence.</summary>
    public int Sequence { get; set; }

    public required string Number { get; set; }
    public InvoiceType Type { get; set; }
    public InvoiceStatus Status { get; set; } = InvoiceStatus.Draft;
    public DateOnly PeriodFrom { get; set; }
    public DateOnly PeriodTo { get; set; }
    public DateOnly InvoiceDate { get; set; }
    public DateOnly DueDate { get; set; }
    public int PaymentTermsDays { get; set; } = 30;

    /// <summary>Sum of the lines (negative for a credit memo).</summary>
    public decimal Total { get; set; }

    /// <summary>Fee withheld on this invoice (a positive number; the FeeWithhold lines are negative).</summary>
    public decimal Withheld { get; set; }

    /// <summary>Cash applied from payments.</summary>
    public decimal AmountPaid { get; set; }

    /// <summary>Approved credit memos against this invoice.</summary>
    public decimal AmountCredited { get; set; }

    /// <summary>On a credit memo: the invoice it credits.</summary>
    public Guid? CreditOfInvoiceId { get; set; }

    /// <summary>For cost-plus and true-up invoices: the indirect rate basis used ("Provisional FY2027", "Direct cost only").</summary>
    public string? RateNote { get; set; }

    public string? Notes { get; set; }

    /// <summary>What the generator flagged (missing bill rates, excluded unallowable cost...), one per line.</summary>
    public string? GenerationWarnings { get; set; }

    /// <summary>Why Finance approved past the funded value (audited).</summary>
    public string? FundingOverrideReason { get; set; }

    public bool ReleasesWithhold { get; set; }
    public Guid? JournalEntryId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? SentBy { get; set; }
    public DateTime? SentAtUtc { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public string? VoidReason { get; set; }

    public List<InvoiceLine> Lines { get; } = [];

    /// <summary>What the customer still owes.</summary>
    public decimal Balance => IsOpenReceivable ? Total - AmountPaid - AmountCredited : 0m;

    /// <summary>Approved, not void, and not a credit memo (credit memos are applied to their invoice when approved).</summary>
    public bool IsOpenReceivable => (Status is InvoiceStatus.Approved or InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid) && Type != InvoiceType.CreditMemo;

    /// <summary>Counts toward billed-to-date and funding: approved or later, not void.</summary>
    public bool IsBilled => Status is InvoiceStatus.Approved or InvoiceStatus.Sent or InvoiceStatus.PartiallyPaid or InvoiceStatus.Paid;

    public void Recalculate()
    {
        Total = Lines.Sum(l => l.Amount);
        Withheld = -Lines.Where(l => l.Kind == InvoiceLineKind.FeeWithhold).Sum(l => l.Amount);
    }

    /// <summary>Sets PartiallyPaid / Paid (or back to Approved/Sent) from the amounts applied.</summary>
    public void UpdatePaymentStatus()
    {
        if (Status is InvoiceStatus.Draft or InvoiceStatus.Void || Type == InvoiceType.CreditMemo)
        {
            return;
        }

        var settled = AmountPaid + AmountCredited;
        Status = settled >= Total && Total > 0 ? InvoiceStatus.Paid
            : AmountPaid > 0 ? InvoiceStatus.PartiallyPaid
            : SentAtUtc is not null ? InvoiceStatus.Sent
            : InvoiceStatus.Approved;
    }
}

/// <summary>One line of an invoice. Segments (project, WBS) carry into the revenue posting.</summary>
public sealed class InvoiceLine : Entity
{
    public Guid InvoiceId { get; set; }
    public int LineNumber { get; set; }
    public Guid? ClinId { get; set; }
    public InvoiceLineKind Kind { get; set; }
    public required string Description { get; set; }
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public Guid? LaborCategoryId { get; set; }

    /// <summary>For ODC lines: the <c>OdcType</c> name.</summary>
    public string? OdcType { get; set; }

    /// <summary>Hours for labor lines, 1 otherwise.</summary>
    public decimal Quantity { get; set; }

    public decimal Rate { get; set; }
    public decimal Amount { get; set; }

    /// <summary>True for lines the generator wrote; manual adjustments are kept when a draft is regenerated.</summary>
    public bool IsGenerated { get; set; } = true;

    public List<BilledSource> Sources { get; } = [];
}

public enum BilledSourceKind
{
    TimeEntry,
    Odc,
    Milestone,
}

/// <summary>
/// A time entry, ODC or milestone billed on an invoice line, with what was billed for it. A source is billed once: it
/// can't appear on another invoice until this one is voided (<see cref="ReleasedAtUtc"/>) or the draft is deleted.
/// </summary>
public sealed class BilledSource : Entity
{
    public Guid InvoiceId { get; set; }
    public Guid InvoiceLineId { get; set; }
    public BilledSourceKind Kind { get; set; }
    public Guid SourceId { get; set; }
    public DateOnly Date { get; set; }
    public Guid? EmployeeId { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public Guid? WbsElementId { get; set; }
    public decimal Quantity { get; set; }
    public decimal Rate { get; set; }
    public decimal Amount { get; set; }
    public string? Description { get; set; }

    /// <summary>Set when the invoice is voided; the source can then be billed again.</summary>
    public DateTime? ReleasedAtUtc { get; set; }
}

/// <summary>An FFP milestone (payment event) on a CLIN.</summary>
public sealed class BillingMilestone : Entity
{
    public Guid ContractId { get; set; }
    public Guid ClinId { get; set; }
    public required string Description { get; set; }
    public decimal Amount { get; set; }
    public DateOnly PlannedDate { get; set; }
    public DateOnly? CompletedDate { get; set; }
    public string? CompletedBy { get; set; }

    /// <summary>The invoice (draft or approved) billing it; cleared when that invoice is voided or deleted.</summary>
    public Guid? InvoiceId { get; set; }

    public int Order { get; set; }
}

/// <summary>Per-contract billing setup: invoice numbering, fee withhold and material handling, voucher details.</summary>
public sealed class ContractBillingSettings : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>Invoice numbers are <c>{Prefix}-{sequence:0000}</c>; WAWF allows 22 characters.</summary>
    public required string InvoicePrefix { get; set; }

    /// <summary>Share of fee withheld on cost-plus invoices until the final invoice (0.15 = 15%).</summary>
    public decimal FeeWithholdRate { get; set; }

    /// <summary>Optional cap on the total withheld (for example 100,000 under FAR 52.216-8).</summary>
    public decimal? FeeWithholdCap { get; set; }

    /// <summary>Material handling added to T&amp;M materials (0.05 = 5%), when the contract allows it.</summary>
    public decimal? MaterialHandlingRate { get; set; }

    /// <summary>Overrides the customer's payment terms for this contract.</summary>
    public int? PaymentTermsDays { get; set; }

    /// <summary>Paying office DoDAAC / address for the voucher and WAWF.</summary>
    public string? PayingOffice { get; set; }

    /// <summary>Contract administration office (DCAA / DCMA).</summary>
    public string? AdminOffice { get; set; }

    public string? CageCode { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A customer's billing terms and addresses.</summary>
public sealed class CustomerBillingProfile : Entity
{
    public Guid CustomerId { get; set; }
    public int PaymentTermsDays { get; set; } = 30;
    public string? BillingAddress { get; set; }
    public string? StatementEmail { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum PaymentMethod
{
    Eft,
    Check,
    Wire,
    Other,
}

/// <summary>A cash receipt from a customer, fully applied to its invoices. Posts debit cash, credit AR billed.</summary>
public sealed class Payment : Entity
{
    public Guid CustomerId { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public decimal Amount { get; set; }
    public PaymentMethod Method { get; set; }
    public string? Reference { get; set; }
    public string? Notes { get; set; }
    public Guid? JournalEntryId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public bool IsVoid { get; set; }
    public string? VoidReason { get; set; }
    public string? VoidedBy { get; set; }
    public DateTime? VoidedAtUtc { get; set; }
    public List<PaymentApplication> Applications { get; } = [];
}

public sealed class PaymentApplication : Entity
{
    public Guid PaymentId { get; set; }
    public Guid InvoiceId { get; set; }
    public decimal Amount { get; set; }
}

/// <summary>
/// The month-end accrual of earned-but-unbilled revenue for one fiscal period (debit AR unbilled, credit revenue),
/// with its automatic reversal on the first day of the next period. One per period.
/// </summary>
public sealed class UnbilledAccrual : Entity
{
    public Guid FiscalPeriodId { get; set; }
    public decimal Amount { get; set; }
    public Guid JournalEntryId { get; set; }
    public Guid? ReversalEntryId { get; set; }
    public required string PostedBy { get; set; }
    public DateTime PostedAtUtc { get; set; }
}

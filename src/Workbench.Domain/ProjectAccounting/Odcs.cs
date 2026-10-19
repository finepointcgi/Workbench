namespace Workbench.Domain.ProjectAccounting;

public enum OdcType
{
    Travel,
    Materials,
    Equipment,
    Subcontract,
    Consultant,
    Other,
}

public enum OdcStatus
{
    Draft,
    Submitted,
    Approved,
    Rejected,
}

/// <summary>An other direct cost (or employee expense) charged to a WBS element.</summary>
public sealed class OdcEntry : Entity
{
    public Guid ProjectId { get; set; }
    public Guid WbsElementId { get; set; }
    public OdcType Type { get; set; }
    public DateOnly IncurredOn { get; set; }
    public decimal Amount { get; set; }
    public required string Description { get; set; }
    public string? Vendor { get; set; }

    /// <summary>The vendor record, when known (vendor ODCs and AP bills). <see cref="Vendor"/> stays as the display name.</summary>
    public Guid? VendorId { get; set; }

    /// <summary>Set when the cost comes from a subcontract invoice.</summary>
    public Guid? SubcontractId { get; set; }
    public string? InvoiceNumber { get; set; }

    /// <summary>Employee who incurred it (expenses); null for vendor ODCs such as subcontract invoices.</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>Unallowable costs (FAR 31.205) are recorded but excluded from billable cost.</summary>
    public bool IsUnallowable { get; set; }

    /// <summary>Why the cost is unallowable (e.g. "Alcohol, FAR 31.205-51"). Required when <see cref="IsUnallowable"/>.</summary>
    public string? UnallowableReason { get; set; }

    /// <summary>The expense report this line belongs to; null for vendor ODCs.</summary>
    public Guid? ExpenseReportId { get; set; }

    /// <summary>The receipt or invoice copy: a <c>StoredFile</c> id (see <c>Workbench.Domain.Files</c>).</summary>
    public Guid? ReceiptAttachmentId { get; set; }
    public OdcStatus Status { get; set; } = OdcStatus.Draft;
    public required string SubmittedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
    public string? RejectedBy { get; set; }
    public DateTime? RejectedAtUtc { get; set; }

    // ---- Added by multicurrency (additive): an expense paid in another currency. Amount stays functional. ----

    public string? CurrencyCode { get; set; }
    public decimal? TransactionAmount { get; set; }
    public decimal? ExchangeRate { get; set; }
}

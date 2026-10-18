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
    public string? InvoiceNumber { get; set; }

    /// <summary>Employee who incurred it (expenses); null for vendor ODCs such as subcontract invoices.</summary>
    public Guid? EmployeeId { get; set; }

    /// <summary>Unallowable costs (FAR 31.205) are recorded but excluded from billable cost.</summary>
    public bool IsUnallowable { get; set; }

    public Guid? ReceiptAttachmentId { get; set; }
    public OdcStatus Status { get; set; } = OdcStatus.Draft;
    public required string SubmittedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectionReason { get; set; }
}

using Workbench.Domain.ProjectAccounting;

namespace Workbench.Domain.Purchasing;

public enum RequisitionStatus
{
    Draft,
    PendingApproval,
    Approved,
    Rejected,

    /// <summary>Every line is on a purchase order.</summary>
    Ordered,
    Cancelled,
}

/// <summary>Who a pending requisition waits on.</summary>
public enum RequisitionStage
{
    /// <summary>The project managers of the projects its lines charge.</summary>
    ProjectManager,

    /// <summary>Anyone in Finance (above the threshold, for account lines, or when there's no other approver).</summary>
    Finance,
}

/// <summary>A request to buy something, approved before it becomes a purchase order.</summary>
public sealed class PurchaseRequisition : Entity
{
    /// <summary>For example "REQ-000012".</summary>
    public required string Number { get; set; }

    public required string RequesterEmail { get; set; }
    public Guid? RequesterEmployeeId { get; set; }

    /// <summary>The organization account lines are charged to (the requester's org by default).</summary>
    public Guid? OrgUnitId { get; set; }

    public required string Justification { get; set; }
    public RequisitionStatus Status { get; set; } = RequisitionStatus.Draft;

    /// <summary>The stage it waits on while pending.</summary>
    public RequisitionStage? Stage { get; set; }

    /// <summary>Set at submission: the total is over the Finance threshold or a line needs Finance.</summary>
    public bool NeedsFinance { get; set; }

    public decimal Total { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }
    public string? RejectedBy { get; set; }
    public string? RejectionReason { get; set; }

    public List<PurchaseRequisitionLine> Lines { get; } = [];
}

public sealed class PurchaseRequisitionLine : Entity
{
    public Guid RequisitionId { get; set; }
    public int LineNumber { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitCost { get; set; }

    /// <summary>Quantity × unit cost, rounded to the cent.</summary>
    public decimal Amount { get; set; }

    // A project line (WBS element) or an account line (GL account and organization).
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public OdcType? OdcType { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }

    public DateOnly? NeededBy { get; set; }
    public Guid? SuggestedVendorId { get; set; }

    /// <summary>The project manager this line waits on (null for account lines or when it goes to Finance).</summary>
    public string? ApproverEmail { get; set; }

    public string? ApprovedBy { get; set; }
    public DateTime? ApprovedAtUtc { get; set; }

    /// <summary>The purchase order line it was ordered on.</summary>
    public Guid? PurchaseOrderLineId { get; set; }
}

/// <summary>One approval decision on a requisition (append-only history).</summary>
public sealed class RequisitionApproval : Entity
{
    public Guid RequisitionId { get; set; }
    public RequisitionStage Stage { get; set; }

    /// <summary>"Approved" or "Rejected".</summary>
    public required string Decision { get; set; }

    public required string DecidedBy { get; set; }
    public DateTime DecidedAtUtc { get; set; }

    /// <summary>For project-manager decisions: the project decided on.</summary>
    public Guid? ProjectId { get; set; }

    public string? Comment { get; set; }
}

public enum PurchaseOrderStatus
{
    Draft,
    Issued,
    PartiallyReceived,
    Received,
    Closed,
    Cancelled,
}

/// <summary>A purchase order to a vendor. Issued orders change only through change orders (a new revision).</summary>
public sealed class PurchaseOrder : Entity
{
    /// <summary>For example "PO-000007".</summary>
    public required string Number { get; set; }

    public Guid VendorId { get; set; }
    public DateOnly OrderDate { get; set; }

    /// <summary>Payment and delivery terms, for example "Net 30, FOB destination".</summary>
    public string? Terms { get; set; }

    public string? ShipTo { get; set; }
    public string? Notes { get; set; }
    public PurchaseOrderStatus Status { get; set; } = PurchaseOrderStatus.Draft;

    /// <summary>0 for the original order; each change order adds one.</summary>
    public int Revision { get; set; }

    public decimal Total { get; set; }

    /// <summary>Comma-separated emails (besides Finance) who may receive against this order.</summary>
    public string? ReceiverEmails { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? IssuedBy { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? CancelReason { get; set; }

    public List<PurchaseOrderLine> Lines { get; } = [];

    public bool IsOpen => Status is PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived or PurchaseOrderStatus.Received;
}

public sealed class PurchaseOrderLine : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public int LineNumber { get; set; }
    public required string Description { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }

    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public OdcType? OdcType { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }

    public DateOnly? NeededBy { get; set; }

    /// <summary>A service, received by acceptance rather than delivery.</summary>
    public bool IsService { get; set; }

    /// <summary>Running total of receipts (maintained with the receipts).</summary>
    public decimal QuantityReceived { get; set; }

    /// <summary>The requisition line it came from, when it came from one.</summary>
    public Guid? RequisitionLineId { get; set; }
}

/// <summary>A change order: the order's revision history (append-only).</summary>
public sealed class PurchaseOrderRevision : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public int Revision { get; set; }
    public required string Reason { get; set; }
    public decimal TotalBefore { get; set; }
    public decimal TotalAfter { get; set; }

    /// <summary>What changed, one change per line of text.</summary>
    public required string Changes { get; set; }

    public required string ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
}

/// <summary>Goods received (or services accepted) against a purchase order.</summary>
public sealed class PurchaseReceipt : Entity
{
    /// <summary>For example "RCV-000003".</summary>
    public required string Number { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public DateOnly ReceivedOn { get; set; }
    public required string ReceivedBy { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<PurchaseReceiptLine> Lines { get; } = [];
}

public sealed class PurchaseReceiptLine : Entity
{
    public Guid ReceiptId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>A service acceptance rather than a delivery.</summary>
    public bool IsAcceptance { get; set; }
}

using Workbench.Domain.Materials;

namespace Workbench.Domain.Quality;

/// <summary>A reason something is nonconforming (for example "DIM" dimension out of tolerance).</summary>
public sealed class DefectCode : Entity
{
    /// <summary>Upper case, unique.</summary>
    public required string Code { get; set; }

    public required string Description { get; set; }
    public bool IsActive { get; set; } = true;
}

public enum NonconformanceSource
{
    ReceivingInspection,
    InProcessInspection,
    FinalInspection,

    /// <summary>Found in stock (damage, shelf life, a later discovery).</summary>
    Stock,
}

public enum NonconformanceStatus
{
    /// <summary>Waiting for the material review board's disposition.</summary>
    Open,

    /// <summary>A disposition is proposed and going through approval.</summary>
    PendingApproval,

    /// <summary>The approved disposition has been carried out. Final.</summary>
    Closed,

    Cancelled,
}

/// <summary>The material review board's decision.</summary>
public enum NonconformanceDisposition
{
    /// <summary>Accepted as it is (with engineering's agreement): released to available stock.</summary>
    UseAsIs,

    /// <summary>Reworked on a rework work order, then back to stock.</summary>
    Rework,

    /// <summary>Shipped back to the vendor, with a debit memo for accounts payable.</summary>
    ReturnToVendor,

    /// <summary>Scrapped, its cost charged to an expense account or a project.</summary>
    Scrap,
}

/// <summary>
/// A nonconformance report: rejected material (part, lot or serial, quantity, defect codes, description, photos) and,
/// once the material review board's disposition is approved through the approval workflow, what was done with it.
/// The material sits in nonconforming stock (<see cref="InventoryStockStatus.Nonconforming"/>) until then.
/// </summary>
public sealed class Nonconformance : Entity
{
    /// <summary>For example "NCR-000003".</summary>
    public required string Number { get; set; }

    public Guid PartId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public decimal Quantity { get; set; }
    public NonconformanceSource Source { get; set; }
    public NonconformanceStatus Status { get; set; } = NonconformanceStatus.Open;
    public Guid? InspectionId { get; set; }
    public Guid? VendorId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid? WorkOrderId { get; set; }

    /// <summary>Comma-separated defect codes, for example "DIM,DOC".</summary>
    public string DefectCodes { get; set; } = "";

    public required string Description { get; set; }

    // The proposed (then approved) disposition.
    public NonconformanceDisposition? Disposition { get; set; }
    public string? DispositionNote { get; set; }

    /// <summary>Scrap: the expense account charged (default: inventory adjustments and scrap).</summary>
    public Guid? ScrapAccountId { get; set; }

    /// <summary>Scrap: the project WBS element charged instead (direct material).</summary>
    public Guid? ScrapWbsElementId { get; set; }

    /// <summary>The date the disposition posts on (default: the day it's finally approved).</summary>
    public DateOnly? DispositionDate { get; set; }

    /// <summary>Return to vendor: the vendor's return authorization (RMA) number, copied to the debit memo.</summary>
    public string? ReturnAuthorization { get; set; }

    /// <summary>The material at cost when the disposition was proposed (what approval routing compares to its threshold).</summary>
    public decimal ValueAtCost { get; set; }

    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    // What the disposition did.
    public Guid? ReworkWorkOrderId { get; set; }
    public Guid? DebitMemoId { get; set; }
    public Guid? JournalEntryId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<NonconformanceItem> Items { get; } = [];
}

/// <summary>Where the nonconforming stock is (none for material still on a work order).</summary>
public sealed class NonconformanceItem : Entity
{
    public Guid NonconformanceId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid BinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public decimal Quantity { get; set; }
}

public enum DebitMemoStatus
{
    /// <summary>Waiting for accounts payable to take it against the vendor's next payment.</summary>
    Open,

    /// <summary>Taken by accounts payable (the reference says where).</summary>
    Applied,

    Cancelled,
}

/// <summary>
/// What the vendor owes back for returned goods (quantity × the purchase order price). The return already debited the
/// receiving clearing account; accounts payable applies the memo against the vendor's bill or payment.
/// </summary>
public sealed class VendorDebitMemo : Entity
{
    /// <summary>For example "DM-000001".</summary>
    public required string Number { get; set; }

    public Guid VendorId { get; set; }
    public Guid NonconformanceId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid PartId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }
    public decimal Amount { get; set; }

    /// <summary>The vendor's return authorization (RMA) number, when they gave one.</summary>
    public string? ReturnAuthorization { get; set; }

    public DebitMemoStatus Status { get; set; } = DebitMemoStatus.Open;
    public DateOnly Date { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? AppliedBy { get; set; }
    public DateTime? AppliedAtUtc { get; set; }

    /// <summary>For example the AP bill or payment the memo was taken against.</summary>
    public string? AppliedReference { get; set; }
}

public enum CorrectiveActionStatus
{
    Open,

    /// <summary>The action is done; waiting for verification that it worked.</summary>
    Implemented,

    /// <summary>Verified effective. Final.</summary>
    Closed,

    Cancelled,
}

public enum RootCauseCategory
{
    NotDetermined,
    Design,
    Process,
    Material,
    Supplier,
    Workmanship,
    Equipment,
    Documentation,
    Other,
}

/// <summary>A corrective action request: the problem, its root cause, the action, who owns it and when it's due.</summary>
public sealed class CorrectiveAction : Entity
{
    /// <summary>For example "CAR-000002".</summary>
    public required string Number { get; set; }

    public required string Title { get; set; }
    public Guid? NonconformanceId { get; set; }

    /// <summary>A supplier corrective action (SCAR) when set.</summary>
    public Guid? VendorId { get; set; }

    public Guid? PartId { get; set; }
    public required string Problem { get; set; }
    public RootCauseCategory RootCauseCategory { get; set; }
    public string? RootCause { get; set; }
    public string? Action { get; set; }

    /// <summary>What keeps it from happening elsewhere.</summary>
    public string? PreventiveAction { get; set; }

    public required string OwnerEmail { get; set; }
    public DateOnly DueDate { get; set; }
    public CorrectiveActionStatus Status { get; set; } = CorrectiveActionStatus.Open;
    public DateOnly? ImplementedOn { get; set; }
    public string? VerifiedBy { get; set; }
    public string? VerificationNote { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

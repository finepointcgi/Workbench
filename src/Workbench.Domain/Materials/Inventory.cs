namespace Workbench.Domain.Materials;

/// <summary>Whose stock it is.</summary>
public enum InventoryOwnership
{
    /// <summary>Company stock, issued to any project or work order.</summary>
    General,

    /// <summary>Bought for one project (<c>OwnerProjectId</c>) and issued only to it; costed like general stock until issued.</summary>
    Contract,

    /// <summary>
    /// Government-furnished material (FAR 52.245-1) held for one project: tracked by quantity, lot and serial, never
    /// costed to the contractor, never in average cost, inventory value or the GL.
    /// </summary>
    GovernmentFurnished,
}

public enum InventoryTransactionType
{
    /// <summary>Received from a purchase order line into stock.</summary>
    PurchaseReceipt,

    /// <summary>Government-furnished material received (no cost).</summary>
    GovernmentReceipt,

    /// <summary>Issued to a project WBS element: direct material cost.</summary>
    IssueToProject,

    /// <summary>Issued (kitted) to a work order.</summary>
    IssueToWorkOrder,

    /// <summary>Returned from a work order to stock.</summary>
    ReturnFromWorkOrder,

    TransferOut,
    TransferIn,

    /// <summary>A quantity correction with a reason (damage, found stock, shrinkage).</summary>
    Adjustment,

    /// <summary>The difference between a cycle count and the quantity on record.</summary>
    CycleCount,

    /// <summary>Finished units from a stock work order into stock.</summary>
    WorkOrderCompletion,

    /// <summary>Finished units from a project work order delivered to the project (no stock movement).</summary>
    WorkOrderShipment,

    /// <summary>Scrap reported on a work order (no stock movement; WIP relieved).</summary>
    Scrap,

    /// <summary>A standard cost change on stock already on hand (no quantity).</summary>
    Revaluation,

    /// <summary>Stock moved between available, inspection hold and nonconforming (a pair, like a transfer; no GL).</summary>
    StatusChange,

    /// <summary>Nonconforming stock shipped back to the vendor (an NCR disposition; credits inventory against the receiving clearing account).</summary>
    VendorReturn,

    /// <summary>Stock scrapped from inventory (an NCR disposition), charged to an expense account or a project.</summary>
    InventoryScrap,
}

/// <summary>
/// Stock of one part in one bin, lot and serial, for one owner. Kept as a running total by inventory transactions,
/// never typed. <see cref="ReceivedOn"/> is the date of the oldest stock still in it (for aging).
/// </summary>
public sealed class InventoryBalance : Entity
{
    public Guid PartId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid BinId { get; set; }

    /// <summary>Empty when the part isn't lot tracked.</summary>
    public string Lot { get; set; } = "";

    /// <summary>Empty when the part isn't serial tracked.</summary>
    public string Serial { get; set; } = "";

    public InventoryOwnership Ownership { get; set; }

    /// <summary>The project contract inventory or government-furnished material belongs to.</summary>
    public Guid? OwnerProjectId { get; set; }

    public decimal Quantity { get; set; }
    public DateOnly ReceivedOn { get; set; }

    /// <summary>For government-furnished material: the shipping document it came on (for example a DD Form 1149 number).</summary>
    public string? GovernmentReference { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>Added by quality: stock on inspection hold or nonconforming is costed but never picked, issued or planned by MRP.</summary>
    public InventoryStockStatus Status { get; set; }
}

/// <summary>
/// One inventory movement (append-only). Quantities are signed (+ into the location, − out of it). The amount is what
/// the movement is worth at cost (0 for government-furnished material); <see cref="JournalEntryId"/> is the GL entry
/// it posted, when it posted one.
/// </summary>
public sealed class InventoryTransaction : Entity
{
    /// <summary>For example "IT-000042".</summary>
    public required string Number { get; set; }

    public InventoryTransactionType Type { get; set; }
    public DateOnly Date { get; set; }
    public Guid PartId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? BinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>The unit cost the movement was valued at.</summary>
    public decimal UnitCost { get; set; }

    /// <summary>Quantity × unit cost to the cent (signed like the quantity).</summary>
    public decimal Amount { get; set; }

    /// <summary>Purchase price, standard-cost or production variance posted with the movement.</summary>
    public decimal Variance { get; set; }

    /// <summary>Where the cost went: the project and WBS element charged (issues and project shipments).</summary>
    public Guid? ProjectId { get; set; }

    public Guid? WbsElementId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid? PurchaseReceiptId { get; set; }

    /// <summary>Links the two halves of a transfer.</summary>
    public Guid? TransferGroupId { get; set; }

    /// <summary>Required for adjustments, cycle counts, scrap and revaluations.</summary>
    public string? Reason { get; set; }

    /// <summary>An outside document: a government shipping document, a packing slip.</summary>
    public string? Reference { get; set; }

    public Guid? JournalEntryId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Added by quality: the stock status of the location side of the movement.</summary>
    public InventoryStockStatus Status { get; set; }
}

/// <summary>
/// Ties a purchasing document to a part, so receiving the purchase order puts the goods into inventory. Purchasing's
/// own tables are unchanged: a planned buy firmed by MRP links its requisition line; the purchase order line made from
/// it is found through <c>PurchaseOrderLine.RequisitionLineId</c>, or linked directly.
/// </summary>
public sealed class MaterialPurchaseLink : Entity
{
    public Guid PartId { get; set; }
    public Guid? RequisitionLineId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid? PlannedOrderId { get; set; }

    /// <summary>General or Contract (never government-furnished: that isn't bought).</summary>
    public InventoryOwnership Ownership { get; set; }

    public Guid? OwnerProjectId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? BinId { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

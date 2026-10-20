namespace Workbench.Domain.Materials;

public enum SalesOrderStatus
{
    /// <summary>Being entered: lines can change, nothing is planned or picked.</summary>
    Draft,

    /// <summary>Released to the floor: its open lines are MRP demand and can be picked.</summary>
    Released,

    PartlyShipped,

    /// <summary>Every line shipped in full (still open for invoicing and paperwork until closed).</summary>
    Shipped,

    Closed,
    Cancelled,
}

/// <summary>Where the government inspects or accepts the goods (DD Form 250 blocks 8 and 21).</summary>
public enum InspectionPoint
{
    Origin,
    Destination,
}

/// <summary>
/// A customer order for parts (Costpoint Sales Order Entry): for a customer, optionally under a contract (lines tied to its
/// CLINs) and a project. Cost-type work is issued to <see cref="WbsElementId"/> when it ships; everything else goes to
/// cost of goods sold. The government shipping codes feed the DD Form 250 and the WAWF receiving report.
/// </summary>
public sealed class SalesOrder : Entity, Companies.ICompanyOwned
{
    /// <summary>For example "SO-000012".</summary>
    public required string Number { get; set; }

    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;
    public Guid CustomerId { get; set; }
    public Guid? ContractId { get; set; }
    public Guid? ProjectId { get; set; }

    /// <summary>The direct WBS element cost-type shipments are charged to (and COGS is segmented by, when set).</summary>
    public Guid? WbsElementId { get; set; }

    /// <summary>The customer's order or delivery order number.</summary>
    public string? CustomerReference { get; set; }

    public DateOnly OrderDate { get; set; }
    public SalesOrderStatus Status { get; set; }

    /// <summary>Credit hold: nothing is released, picked or shipped until it's cleared.</summary>
    public bool OnCreditHold { get; set; }

    /// <summary>A shipping hold for any other reason (engineering, customer request).</summary>
    public bool OnHold { get; set; }

    public string? HoldReason { get; set; }

    public string? ShipToName { get; set; }
    public string? ShipToAddress { get; set; }

    /// <summary>The ship-to DoDAAC (or CAGE) code, DD250 block 13.</summary>
    public string? ShipToCode { get; set; }

    /// <summary>Mark-for code, DD250 block 14.</summary>
    public string? MarkForCode { get; set; }

    /// <summary>The contracting office DoDAAC that issued the contract (WAWF "Issue By").</summary>
    public string? IssuedByCode { get; set; }

    /// <summary>Who inspects and accepts for the government (WAWF "Inspect By" / "Accept By" DoDAACs).</summary>
    public string? InspectByCode { get; set; }

    public string? AcceptByCode { get; set; }

    public InspectionPoint InspectionPoint { get; set; }
    public InspectionPoint AcceptancePoint { get; set; }

    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? ReleasedBy { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }
    public string? CancelReason { get; set; }

    public List<SalesOrderLine> Lines { get; set; } = [];

    public bool IsHeld => OnHold || OnCreditHold;
}

/// <summary>One part on a sales order. <see cref="QuantityShipped"/> is kept by confirmed shipments, never typed.</summary>
public sealed class SalesOrderLine : Entity
{
    public Guid SalesOrderId { get; set; }
    public int LineNumber { get; set; }
    public Guid PartId { get; set; }
    public Guid? ClinId { get; set; }

    /// <summary>The contract item number (CLIN or SLIN, for example "0001" or "0001AA") on the DD250 and WAWF.</summary>
    public string? ItemNumber { get; set; }

    public required string Description { get; set; }
    public decimal QuantityOrdered { get; set; }
    public decimal QuantityShipped { get; set; }
    public decimal UnitPrice { get; set; }
    public DateOnly NeedDate { get; set; }

    /// <summary>A ship-to for this line only (otherwise the order's).</summary>
    public string? ShipToName { get; set; }

    public string? ShipToAddress { get; set; }
    public string? ShipToCode { get; set; }

    /// <summary>National stock number, when the item has one (DD250 block 16).</summary>
    public string? StockNumber { get; set; }

    /// <summary>The item is UID-marked (DFARS 252.211-7003): each unit's unique item identifier goes to WAWF.</summary>
    public bool UidRequired { get; set; }

    /// <summary>Short-closed: nothing more ships on it.</summary>
    public bool IsClosed { get; set; }

    public decimal QuantityOpen => IsClosed ? 0 : Math.Max(0, QuantityOrdered - QuantityShipped);

    public decimal Amount => Math.Round(QuantityOrdered * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

public enum PickListStatus
{
    Open,

    /// <summary>Its picked quantities are on a shipment.</summary>
    Packed,

    Cancelled,
}

/// <summary>A pick list: stock reserved for released order lines, by bin, lot, serial and owner. Stock moves only when the shipment is confirmed.</summary>
public sealed class PickList : Entity
{
    /// <summary>For example "PK-000007".</summary>
    public required string Number { get; set; }

    public Guid SalesOrderId { get; set; }
    public PickListStatus Status { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public List<PickListLine> Lines { get; set; } = [];
}

/// <summary>Stock to pick from one location for one order line; the picker confirms <see cref="QuantityPicked"/>.</summary>
public sealed class PickListLine : Entity
{
    public Guid PickListId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public Guid PartId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid BinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }

    /// <summary>To pick (reserved against the location while the list is open).</summary>
    public decimal Quantity { get; set; }

    /// <summary>Confirmed by the picker (0 until picked; may be short).</summary>
    public decimal QuantityPicked { get; set; }

    public string? PickedBy { get; set; }
    public DateTime? PickedAtUtc { get; set; }
}

public enum ShipmentStatus
{
    /// <summary>Being packed: packages, weights and tracking can change.</summary>
    Packing,

    /// <summary>Shipped: inventory issued, the DD250 and WAWF data are final.</summary>
    Confirmed,

    Cancelled,
}

/// <summary>A shipment of picked stock to one ship-to. Confirming it issues the inventory and updates the order.</summary>
public sealed class Shipment : Entity, Companies.ICompanyOwned
{
    /// <summary>For example "SH-000004".</summary>
    public required string Number { get; set; }

    public Guid CompanyId { get; set; } = Companies.Company.DefaultId;
    public Guid SalesOrderId { get; set; }

    /// <summary>The DD250 shipment number (block 2): three letters and four digits, "Z" added on the final shipment. Set on confirmation.</summary>
    public string? ShipmentNumber { get; set; }

    public ShipmentStatus Status { get; set; }
    public DateOnly? ShipDate { get; set; }
    public string? Carrier { get; set; }
    public string? BillOfLading { get; set; }

    /// <summary>The master tracking number (each package may have its own).</summary>
    public string? TrackingNumber { get; set; }

    public string? ShipToName { get; set; }
    public string? ShipToAddress { get; set; }
    public string? ShipToCode { get; set; }

    /// <summary>The last shipment on the contract's items (the DD250 shipment number ends in "Z").</summary>
    public bool IsFinal { get; set; }

    public Guid? InvoiceId { get; set; }
    public Guid? JournalEntryId { get; set; }
    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? ConfirmedBy { get; set; }
    public DateTime? ConfirmedAtUtc { get; set; }

    public List<ShipmentLine> Lines { get; set; } = [];
    public List<ShipmentPackage> Packages { get; set; } = [];
}

/// <summary>Stock on a shipment, from one location. Cost fields are set when the shipment is confirmed.</summary>
public sealed class ShipmentLine : Entity
{
    public Guid ShipmentId { get; set; }
    public Guid SalesOrderLineId { get; set; }
    public Guid? PickListLineId { get; set; }
    public Guid PartId { get; set; }
    public Guid WarehouseId { get; set; }
    public Guid BinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public decimal Quantity { get; set; }
    public decimal UnitPrice { get; set; }

    /// <summary>The package it's packed in (its <see cref="ShipmentPackage.Number"/>).</summary>
    public int? PackageNumber { get; set; }

    public decimal UnitCost { get; set; }

    /// <summary>Cost relieved from inventory (positive).</summary>
    public decimal CostAmount { get; set; }

    /// <summary>Charged to the order's WBS element (cost-type work) rather than cost of goods sold.</summary>
    public bool ChargedToWbs { get; set; }

    public Guid? InventoryTransactionId { get; set; }

    public decimal Amount => Math.Round(Quantity * UnitPrice, 2, MidpointRounding.AwayFromZero);
}

/// <summary>A box, crate or pallet on a shipment.</summary>
public sealed class ShipmentPackage : Entity
{
    public Guid ShipmentId { get; set; }
    public int Number { get; set; }

    /// <summary>Gross weight in pounds.</summary>
    public decimal Weight { get; set; }

    /// <summary>Inches.</summary>
    public decimal? Length { get; set; }

    public decimal? Width { get; set; }
    public decimal? Height { get; set; }
    public string? TrackingNumber { get; set; }
}

/// <summary>The WAWF (PIEE) document a shipment is reported on.</summary>
public enum WawfDocumentType
{
    /// <summary>Receiving report only (the electronic DD250); the invoice goes separately.</summary>
    ReceivingReport,

    /// <summary>Invoice and receiving report together for supplies.</summary>
    Combo,

    /// <summary>Invoice and receiving report together for services.</summary>
    TwoInOne,
}

public enum WawfStatus
{
    Exported,
    Submitted,
    Accepted,
    Rejected,
}

/// <summary>A shipment's WAWF receiving report: exported, submitted (through the configured submitter) and its status.</summary>
public sealed class WawfReceivingReport : Entity
{
    public Guid ShipmentId { get; set; }
    public WawfDocumentType DocumentType { get; set; }
    public WawfStatus Status { get; set; }
    public required string FileName { get; set; }

    /// <summary>The PIEE document number or the submitter's reference.</summary>
    public string? Reference { get; set; }

    /// <summary>A rejection reason or the submitter's message.</summary>
    public string? Message { get; set; }

    public required string ExportedBy { get; set; }
    public DateTime ExportedAtUtc { get; set; }
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

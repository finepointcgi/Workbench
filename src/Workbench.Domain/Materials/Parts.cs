namespace Workbench.Domain.Materials;

/// <summary>Whether a part is bought from a vendor or built on a work order.</summary>
public enum PartSource
{
    Buy,
    Make,
}

/// <summary>How a part's inventory is valued.</summary>
public enum CostMethod
{
    /// <summary>Moving average: each costed receipt re-averages the unit cost; issues go out at the average.</summary>
    Average,

    /// <summary>Standard: stock moves at the standard cost; receipt and production differences post as variances.</summary>
    Standard,
}

/// <summary>Traceability of a part's stock.</summary>
public enum TrackingMode
{
    None,

    /// <summary>Every receipt, move and issue names a lot.</summary>
    Lot,

    /// <summary>Every unit has its own serial number (quantities of one per serial).</summary>
    Serial,
}

/// <summary>
/// The part master: one row per part number and revision. Parts are deactivated, never deleted, once anything references
/// them. <see cref="AverageCost"/> is maintained by inventory transactions, never typed.
/// </summary>
public sealed class Part : Entity
{
    /// <summary>Upper case, unique together with <see cref="Revision"/> (for example "PSU-24V").</summary>
    public required string Number { get; set; }

    /// <summary>Engineering revision, for example "A". Empty for unrevised parts.</summary>
    public string Revision { get; set; } = "";

    public required string Description { get; set; }

    /// <summary>For example "EA", "FT", "KG".</summary>
    public string UnitOfMeasure { get; set; } = "EA";

    public PartSource Source { get; set; }
    public TrackingMode Tracking { get; set; }
    public CostMethod CostMethod { get; set; }

    /// <summary>Unit standard cost (what standard-cost parts move at; the planning cost for average-cost parts).</summary>
    public decimal StandardCost { get; set; }

    /// <summary>Current moving-average unit cost of costed (non-government) stock.</summary>
    public decimal AverageCost { get; set; }

    /// <summary>
    /// Book value of the part's costed stock: the sum of its inventory transaction amounts, so it equals what the GL
    /// inventory account holds for the part (issuing the last unit relieves exactly this).
    /// </summary>
    public decimal InventoryValue { get; set; }

    public Guid? PreferredVendorId { get; set; }

    /// <summary>Calendar days from release to receipt (buy) or completion (make), used by MRP.</summary>
    public int LeadTimeDays { get; set; }

    /// <summary>MRP plans to keep at least this much on hand.</summary>
    public decimal SafetyStock { get; set; }

    /// <summary>MRP never plans an order smaller than this (0 = lot for lot).</summary>
    public decimal MinimumOrderQuantity { get; set; }

    /// <summary>MRP rounds planned orders up to a multiple of this (0 = no rounding).</summary>
    public decimal OrderMultiple { get; set; }

    /// <summary>Where receipts and completions go when no location is given.</summary>
    public Guid? DefaultWarehouseId { get; set; }

    public Guid? DefaultBinId { get; set; }

    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>The unit cost stock moves at right now.</summary>
    public decimal CurrentUnitCost => CostMethod == CostMethod.Standard ? StandardCost : AverageCost;

    public string Display => Revision.Length == 0 ? Number : $"{Number} rev {Revision}";
}

/// <summary>A stocking location: a building, cage or contract storeroom.</summary>
public sealed class Warehouse : Entity
{
    /// <summary>Upper case, unique.</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public string? Address { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>A bin (shelf, rack position) inside a warehouse.</summary>
public sealed class Bin : Entity
{
    public Guid WarehouseId { get; set; }

    /// <summary>Upper case, unique within the warehouse (for example "A-01-03").</summary>
    public required string Code { get; set; }

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
}

/// <summary>Expected independent demand for a part on a date (spares, forecast sales), netted by MRP.</summary>
public sealed class PartForecast : Entity
{
    public Guid PartId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Quantity { get; set; }
    public string? Note { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// Material a project needs on a WBS element by a date (for example spares or a kit not on a work order). Issues of the
/// part to that WBS element consume it oldest first; MRP nets the open quantity.
/// </summary>
public sealed class ProjectMaterialRequirement : Entity
{
    public Guid ProjectId { get; set; }
    public Guid WbsElementId { get; set; }
    public Guid PartId { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityIssued { get; set; }
    public DateOnly NeededBy { get; set; }
    public string? Note { get; set; }
    public bool IsClosed { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public decimal OpenQuantity => IsClosed ? 0 : Math.Max(0, Quantity - QuantityIssued);
}

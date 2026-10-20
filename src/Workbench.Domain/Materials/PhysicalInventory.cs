namespace Workbench.Domain.Materials;

public enum PhysicalInventoryStatus
{
    /// <summary>The bins are frozen and count tags are out: no stock moves in or out of them until it's posted or cancelled.</summary>
    Counting,

    /// <summary>The adjustments posted and the bins are open again. Final.</summary>
    Posted,

    /// <summary>Cancelled without adjustments; the bins are open again. Final.</summary>
    Cancelled,
}

/// <summary>
/// A wall-to-wall (or bin-range) physical inventory: freezing a warehouse or some of its bins snapshots every stock balance
/// in them as a count tag. Counts are entered by tag; counts off by more than the thresholds need a recount; posting adjusts
/// every difference with value to the inventory adjustment account (government-furnished stock by quantity only, at no value).
/// </summary>
public sealed class PhysicalInventory : Entity
{
    /// <summary>For example "PI-000001".</summary>
    public required string Number { get; set; }

    public Guid WarehouseId { get; set; }

    /// <summary>True when every bin of the warehouse is frozen; otherwise the <see cref="PhysicalInventoryBin"/> rows.</summary>
    public bool WholeWarehouse { get; set; }

    public PhysicalInventoryStatus Status { get; set; } = PhysicalInventoryStatus.Counting;

    /// <summary>The date counts are as of (and adjustments post on).</summary>
    public DateOnly CountDate { get; set; }

    /// <summary>A count off by more than this percent of the frozen quantity needs a recount (0 = no percent test).</summary>
    public decimal RecountPercent { get; set; }

    /// <summary>A count off by more than this much value needs a recount (0 = no value test).</summary>
    public decimal RecountValue { get; set; }

    /// <summary>Hide frozen quantities on count sheets (a blind count).</summary>
    public bool BlindCount { get; set; } = true;

    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime FrozenAtUtc { get; set; }
    public string? PostedBy { get; set; }
    public DateTime? PostedAtUtc { get; set; }
    public Guid? JournalEntryId { get; set; }

    public bool IsFrozen => Status == PhysicalInventoryStatus.Counting;
}

/// <summary>A bin frozen by a physical inventory that doesn't cover the whole warehouse.</summary>
public sealed class PhysicalInventoryBin : Entity
{
    public Guid PhysicalInventoryId { get; set; }
    public Guid BinId { get; set; }
}

public enum CountTagStatus
{
    NotCounted,
    Counted,

    /// <summary>The count is off by more than a threshold: count again before posting.</summary>
    RecountNeeded,

    Recounted,

    /// <summary>A spoiled or unused tag (for example a blank tag written by mistake).</summary>
    Void,
}

/// <summary>
/// One count tag: a stock balance (part, bin, lot, serial, owner) as frozen, or a blank tag for stock found that wasn't on
/// record. The final count is the recount when there is one.
/// </summary>
public sealed class CountTag : Entity
{
    public Guid PhysicalInventoryId { get; set; }

    /// <summary>1, 2, 3 … within the physical inventory.</summary>
    public int TagNumber { get; set; }

    public Guid BinId { get; set; }
    public Guid PartId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }

    /// <summary>Available, inspection hold or nonconforming, as frozen (blank tags count available stock).</summary>
    public InventoryStockStatus StockStatus { get; set; } = InventoryStockStatus.Available;

    /// <summary>On record when frozen (0 for blank tags).</summary>
    public decimal FrozenQuantity { get; set; }

    /// <summary>The unit cost when frozen (0 for government-furnished stock), for variance value and thresholds.</summary>
    public decimal UnitCost { get; set; }

    public bool IsBlankTag { get; set; }
    public CountTagStatus Status { get; set; } = CountTagStatus.NotCounted;
    public decimal? CountedQuantity { get; set; }
    public string? CountedBy { get; set; }
    public DateTime? CountedAtUtc { get; set; }
    public decimal? RecountQuantity { get; set; }
    public string? RecountedBy { get; set; }
    public DateTime? RecountedAtUtc { get; set; }
    public string? Note { get; set; }

    /// <summary>The adjustment the posting made (none when the count matched).</summary>
    public Guid? AdjustmentTransactionId { get; set; }

    public decimal? FinalQuantity => Status == CountTagStatus.Void ? null : RecountQuantity ?? CountedQuantity;
}

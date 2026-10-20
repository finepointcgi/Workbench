namespace Workbench.Domain.Materials;

public enum KitStatus
{
    /// <summary>Created; nothing (or not everything) staged yet.</summary>
    Open,

    /// <summary>Every line is staged in the kit location, ready to issue.</summary>
    Staged,

    /// <summary>The staged stock went to the work order or project. Final.</summary>
    Issued,

    /// <summary>Cancelled; anything staged went back where it came from. Final.</summary>
    Cancelled,
}

/// <summary>
/// A kit: the components of a work order (or the open material requirements of a project) picked from stock and staged
/// in a kit location (a bin set aside for kits), then issued in one step. Stock staged in a kit's bin is reserved: normal
/// picks skip the bins of open kits.
/// </summary>
public sealed class Kit : Entity
{
    /// <summary>For example "KIT-000004".</summary>
    public required string Number { get; set; }

    public Guid? WorkOrderId { get; set; }

    /// <summary>Project kits: the project (and optionally one WBS element) whose open requirements the kit covers.</summary>
    public Guid? ProjectId { get; set; }

    public Guid? WbsElementId { get; set; }

    public Guid StagingWarehouseId { get; set; }
    public Guid StagingBinId { get; set; }
    public KitStatus Status { get; set; } = KitStatus.Open;
    public DateOnly NeededBy { get; set; }
    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? IssuedAtUtc { get; set; }

    public bool IsOpen => Status is KitStatus.Open or KitStatus.Staged;

    public List<KitLine> Lines { get; } = [];
}

/// <summary>One part on a kit: a work order component or a project material requirement.</summary>
public sealed class KitLine : Entity
{
    public Guid KitId { get; set; }
    public int Sequence { get; set; }
    public Guid PartId { get; set; }
    public Guid? WorkOrderComponentId { get; set; }
    public Guid? RequirementId { get; set; }

    /// <summary>Project kits: the WBS element the line is issued to.</summary>
    public Guid? WbsElementId { get; set; }

    public decimal QuantityRequired { get; set; }
    public decimal QuantityStaged { get; set; }
    public decimal QuantityIssued { get; set; }

    /// <summary>Still to stage: the shortage while the kit is open.</summary>
    public decimal QuantityShort => Math.Max(0, QuantityRequired - QuantityStaged - QuantityIssued);
}

/// <summary>
/// Stock staged for a kit line: which lot, serial and owner went into the kit bin, and from where (so cancelling puts it back).
/// </summary>
public sealed class KitPick : Entity
{
    public Guid KitId { get; set; }
    public Guid KitLineId { get; set; }
    public Guid FromWarehouseId { get; set; }
    public Guid FromBinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public decimal Quantity { get; set; }

    /// <summary>The transfer into the kit bin.</summary>
    public Guid TransferInTransactionId { get; set; }

    /// <summary>Set when the pick was issued (or returned on cancel).</summary>
    public Guid? IssueTransactionId { get; set; }
}

/// <summary>When a backflushed component is issued.</summary>
public enum BackflushTrigger
{
    /// <summary>When finished units are reported on the work order.</summary>
    Completion,

    /// <summary>When the operation the component is consumed at reports completed units (labor reporting).</summary>
    Operation,
}

/// <summary>
/// Issue-on-completion (backflush) for a part, or for one component of one BOM revision (overriding the part's setting).
/// A BOM component is identified by its BOM and component part, because saving a BOM replaces its component rows.
/// </summary>
public sealed class BackflushSetting : Entity
{
    public Guid PartId { get; set; }

    /// <summary>Null: the part's default for every BOM. Set: this BOM revision only.</summary>
    public Guid? BomId { get; set; }

    public bool IsBackflushed { get; set; }

    /// <summary>Where backflushed stock is taken from (a bin, or any bin of a warehouse). Neither: the part's default bin, then anywhere.</summary>
    public Guid? WarehouseId { get; set; }

    public Guid? BinId { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>
/// A backflush that couldn't issue everything (no stock in the configured location). One open row per work order component,
/// kept current by each backflush; resolved when the quantity is issued.
/// </summary>
public sealed class BackflushShortage : Entity
{
    public Guid WorkOrderId { get; set; }
    public Guid WorkOrderComponentId { get; set; }
    public Guid PartId { get; set; }
    public decimal QuantityShort { get; set; }
    public BackflushTrigger Trigger { get; set; }
    public DateOnly Date { get; set; }
    public string? Message { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public DateTime? ResolvedAtUtc { get; set; }

    public bool IsOpen => ResolvedAtUtc is null;
}

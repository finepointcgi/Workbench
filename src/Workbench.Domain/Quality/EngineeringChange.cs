namespace Workbench.Domain.Quality;

public enum EngineeringChangeStatus
{
    Draft,
    PendingApproval,

    /// <summary>Approved; the new revisions become active at the effectivity.</summary>
    Approved,

    /// <summary>The new revisions are active. Final.</summary>
    Effective,

    Cancelled,
}

public enum EngineeringChangeReason
{
    DefectCorrection,
    DesignImprovement,
    CostReduction,
    CustomerRequest,
    Obsolescence,
    Regulatory,
    Other,
}

public enum EffectivityKind
{
    /// <summary>The new revision is active from <see cref="EngineeringChange.EffectiveDate"/>.</summary>
    Date,

    /// <summary>
    /// Active on approval for new work, and the as-designed configuration of end items from serial or lot
    /// <see cref="EngineeringChange.EffectiveSerialOrLot"/> on (compared in order).
    /// </summary>
    SerialOrLot,
}

/// <summary>What to do with stock built or bought to the old revision.</summary>
public enum ChangeStockDisposition
{
    UseUp,
    Rework,
    Scrap,
    ReturnToVendor,
}

/// <summary>What to do with open work orders building the old revision (they're flagged for review either way).</summary>
public enum ChangeWorkOrderDisposition
{
    FinishOnOldRevision,
    UpdateToNewRevision,
    Hold,
}

/// <summary>
/// An engineering change order: the affected parts and BOM revisions, why, from when (a date, or a serial or lot), and
/// what happens to existing stock and open work orders. Routed through the approval workflow; at its effectivity the new
/// BOM (and part) revisions become active and open work orders on the old revision are flagged for review.
/// </summary>
public sealed class EngineeringChange : Entity
{
    /// <summary>For example "ECO-000004".</summary>
    public required string Number { get; set; }

    public required string Title { get; set; }
    public EngineeringChangeReason Reason { get; set; }
    public required string Description { get; set; }
    public EffectivityKind EffectivityKind { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public string? EffectiveSerialOrLot { get; set; }
    public ChangeStockDisposition StockDisposition { get; set; }
    public ChangeWorkOrderDisposition WorkOrderDisposition { get; set; }

    /// <summary>Estimated cost of the change (what approval routing compares to its threshold).</summary>
    public decimal CostImpact { get; set; }

    public EngineeringChangeStatus Status { get; set; } = EngineeringChangeStatus.Draft;
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public DateTime? EffectiveAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<EngineeringChangeItem> Items { get; } = [];
}

/// <summary>An affected part: its BOM moves from one revision to another and, optionally, a new part revision is released.</summary>
public sealed class EngineeringChangeItem : Entity
{
    public Guid ChangeId { get; set; }
    public Guid PartId { get; set; }

    /// <summary>The BOM revision active when the change was written.</summary>
    public Guid? FromBomId { get; set; }

    /// <summary>The BOM revision that becomes active at the effectivity.</summary>
    public Guid? ToBomId { get; set; }

    /// <summary>A new part revision (its own part record, inactive until the effectivity).</summary>
    public Guid? NewPartId { get; set; }

    public string? Note { get; set; }
}

public enum WorkOrderReviewStatus
{
    Open,
    Reviewed,
}

/// <summary>An open work order on an old revision, flagged when an engineering change takes effect.</summary>
public sealed class EngineeringChangeWorkOrderReview : Entity
{
    public Guid ChangeId { get; set; }
    public Guid WorkOrderId { get; set; }
    public WorkOrderReviewStatus Status { get; set; }
    public DateTime FlaggedAtUtc { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? Note { get; set; }
}

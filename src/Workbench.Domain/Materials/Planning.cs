namespace Workbench.Domain.Materials;

/// <summary>One MRP planning run (its planned orders are kept until the next run supersedes them).</summary>
public sealed class MrpRun : Entity
{
    public DateOnly AsOf { get; set; }
    public int HorizonDays { get; set; }
    public int PlannedOrderCount { get; set; }
    public int LateCount { get; set; }

    /// <summary>Exceptions found while planning, one per line.</summary>
    public string? Messages { get; set; }

    public required string RunBy { get; set; }
    public DateTime RunAtUtc { get; set; }
}

public enum PlannedOrderKind
{
    Buy,
    Make,
}

public enum PlannedOrderStatus
{
    Planned,

    /// <summary>Turned into a requisition (buy) or a work order (make) by a planner.</summary>
    Firmed,

    /// <summary>Replaced by a later run.</summary>
    Superseded,
}

/// <summary>A suggestion from an MRP run: buy or make a quantity, released on a date so it arrives by the need date.</summary>
public sealed class PlannedOrder : Entity
{
    public Guid RunId { get; set; }
    public Guid PartId { get; set; }
    public PlannedOrderKind Kind { get; set; }
    public decimal Quantity { get; set; }
    public DateOnly NeedDate { get; set; }
    public DateOnly ReleaseDate { get; set; }

    /// <summary>The release date is before the run date: expedite.</summary>
    public bool IsLate { get; set; }

    /// <summary>What the order covers, for example "WO-000003 1.0000 (Dec 1); safety stock 4.0000".</summary>
    public string? Pegging { get; set; }

    /// <summary>When every demand it covers is one project's, that project (the order is then for contract inventory).</summary>
    public Guid? ProjectId { get; set; }

    public PlannedOrderStatus Status { get; set; }
    public Guid? RequisitionId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public string? FirmedBy { get; set; }
    public DateTime? FirmedAtUtc { get; set; }
}

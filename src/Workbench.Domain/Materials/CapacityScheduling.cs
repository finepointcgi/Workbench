namespace Workbench.Domain.Materials;

/// <summary>
/// A work center's working calendar: which weekdays it works, hours per day per unit and how many units (benches,
/// machines, crews) work in parallel. Without one, a work center works Monday to Friday at its <c>HoursPerDay</c>.
/// </summary>
public sealed class WorkCenterCalendar : Entity
{
    public Guid WorkCenterId { get; set; }

    /// <summary>Seven characters, Monday first: "Y" works, "N" doesn't (for example "YYYYYNN").</summary>
    public string WorkingDays { get; set; } = "YYYYYNN";

    public decimal HoursPerDay { get; set; } = 8m;

    /// <summary>Units working in parallel; daily capacity = hours per day × units.</summary>
    public int Units { get; set; } = 1;

    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public bool WorksOn(DayOfWeek day) => WorkingDays.Length == 7 && WorkingDays[((int)day + 6) % 7] == 'Y';
}

/// <summary>A day a work center has different capacity: a holiday or shutdown (0 hours) or overtime.</summary>
public sealed class WorkCenterCapacityException : Entity
{
    public Guid WorkCenterId { get; set; }
    public DateOnly Date { get; set; }

    /// <summary>Total hours available that day (all units).</summary>
    public decimal Hours { get; set; }

    public string? Reason { get; set; }
}

public enum ScheduleDirection
{
    /// <summary>From each work order's release (start) date, or today, as early as capacity allows.</summary>
    Forward,

    /// <summary>Back from each work order's due date, as late as capacity allows (forward from today when it doesn't fit).</summary>
    Backward,
}

/// <summary>
/// Where an open work order operation is scheduled: its place in the work center's queue, an optional planner-pinned
/// start, and the dates and hours the last scheduling run gave it. One row per open operation.
/// </summary>
public sealed class OperationSchedule : Entity
{
    public Guid WorkOrderOperationId { get; set; }
    public Guid WorkOrderId { get; set; }
    public Guid WorkCenterId { get; set; }

    /// <summary>Position in the work center's queue (planner order). Lower runs first.</summary>
    public int QueuePosition { get; set; }

    /// <summary>Set when a planner dragged the operation to a date: it starts then, whatever else is loaded.</summary>
    public DateOnly? PinnedStart { get; set; }

    public DateOnly? Start { get; set; }
    public DateOnly? End { get; set; }

    /// <summary>Open hours scheduled (planned less reported).</summary>
    public decimal Hours { get; set; }

    /// <summary>Ends after its work order's due date.</summary>
    public bool IsLate { get; set; }

    /// <summary>Why it couldn't be scheduled (for example no capacity in the horizon).</summary>
    public string? Problem { get; set; }

    public DateTime ScheduledAtUtc { get; set; }
}

/// <summary>Hours of a scheduled operation on one day (the load chart adds these up by work center and week).</summary>
public sealed class OperationLoad : Entity
{
    public Guid OperationScheduleId { get; set; }
    public Guid WorkCenterId { get; set; }
    public DateOnly Date { get; set; }
    public decimal Hours { get; set; }
}

/// <summary>The last scheduling run (direction and capacity mode reused by moves and resequencing).</summary>
public sealed class CapacityScheduleRun : Entity
{
    public ScheduleDirection Direction { get; set; }

    /// <summary>False: infinite capacity (load where wanted, show the overload).</summary>
    public bool Finite { get; set; } = true;

    public DateOnly AsOf { get; set; }
    public int OperationCount { get; set; }
    public int LateCount { get; set; }
    public string? Messages { get; set; }
    public required string RunBy { get; set; }
    public DateTime RunAtUtc { get; set; }
}

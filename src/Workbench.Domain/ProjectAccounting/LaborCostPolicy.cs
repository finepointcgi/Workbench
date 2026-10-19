namespace Workbench.Domain.ProjectAccounting;

/// <summary>How exempt employees' labor is costed when they record more (or fewer) hours than their standard week.</summary>
public enum LaborCostMethod
{
    /// <summary>Hours up to the standard week cost the hourly rate; an exempt employee's hours beyond it are uncompensated and cost nothing.</summary>
    Standard = 0,

    /// <summary>Total time accounting: the period's salary divided by every hour worked (the effective rate), spread across every charge.</summary>
    TotalTimeEffectiveRate = 1,

    /// <summary>Standard while hours stay within the standard week; the effective rate only in periods with uncompensated hours.</summary>
    Hybrid = 2,
}

/// <summary>
/// The organization's labor cost method (one tenant-level setting), effective-dated and append-only: a change is a new
/// row that starts on a timesheet period boundary. A timesheet uses the method in effect on its period start.
/// </summary>
public sealed class LaborCostPolicy : Entity
{
    public DateOnly EffectiveFrom { get; set; }
    public LaborCostMethod Method { get; set; }
    public required string Reason { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

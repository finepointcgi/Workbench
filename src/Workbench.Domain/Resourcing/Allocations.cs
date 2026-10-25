namespace Workbench.Domain.Resourcing;

public enum AllocationSource
{
    /// <summary>Entered by a project or resource manager.</summary>
    Manual,

    /// <summary>Generated from the project plan (Person assignments and task dates); regenerating replaces these.</summary>
    Plan,
}

/// <summary>
/// Planned hours for one employee on one project (optionally a WBS element) in one week, in a labor category.
/// <see cref="WeekStart"/> is always a Monday.
/// </summary>
public sealed class ResourceAllocation : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public DateOnly WeekStart { get; set; }
    public decimal Hours { get; set; }

    /// <summary>The percentage of the week's standard hours this was entered as, when entered as a percent.</summary>
    public decimal? Percent { get; set; }

    public AllocationSource Source { get; set; }

    /// <summary>The plan item it was generated from (Source = Plan).</summary>
    public Guid? SourceItemId { get; set; }

    /// <summary>The assignment these hours were spread from; null for hours entered week by week.</summary>
    public Guid? AssignmentId { get; set; }

    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

/// <summary>
/// A person assigned to a project, or one WBS element of it, for a total number of hours between two dates (like a
/// Unanet project assignment). The total is the truth: the weekly <see cref="ResourceAllocation"/> rows that point back
/// here (<see cref="ResourceAllocation.AssignmentId"/>) are spread from it over the working days, and editing a week
/// changes the total to the weeks' sum.
/// </summary>
public sealed class StaffingAssignment : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid ProjectId { get; set; }

    /// <summary>Null for the whole project.</summary>
    public Guid? WbsElementId { get; set; }

    /// <summary>The labor category the hours count in; the person's own when not picked.</summary>
    public Guid? LaborCategoryId { get; set; }

    public DateOnly From { get; set; }
    public DateOnly To { get; set; }
    public decimal TotalHours { get; set; }

    /// <summary>
    /// True when the dates are the task's on the plan, so they move when the task moves; false when someone set other
    /// dates on purpose. Null for assignments made before this was kept (decided the first time their task is reflowed).
    /// </summary>
    public bool? FollowsTask { get; set; }

    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

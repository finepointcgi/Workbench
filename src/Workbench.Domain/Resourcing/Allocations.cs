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

    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

using Workbench.Domain.Payroll;

namespace Workbench.Domain.Hr;

public enum PositionStatus
{
    /// <summary>Requested, not yet approved for hiring.</summary>
    Proposed,
    Approved,

    /// <summary>No longer budgeted; people in it keep it on their record.</summary>
    Closed,
}

/// <summary>
/// An approved position: what the organization budgets for (title, labor category, org unit, salary range, headcount).
/// Filled = active employees whose HR profile points at it; open = headcount − filled.
/// </summary>
public sealed class Position : Entity
{
    /// <summary>Unique, upper-case (for example <c>ENG-SE2-01</c>).</summary>
    public required string Code { get; set; }

    public required string Title { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public Guid? OrgUnitId { get; set; }
    public Guid? ReportsToPositionId { get; set; }
    public EeoJobCategory? EeoJobCategory { get; set; }
    public bool IsExempt { get; set; } = true;
    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;
    public decimal? SalaryMin { get; set; }
    public decimal? SalaryMax { get; set; }

    /// <summary>Budgeted headcount (usually 1).</summary>
    public int Headcount { get; set; } = 1;

    public PositionStatus Status { get; set; } = PositionStatus.Approved;
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

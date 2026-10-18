namespace Workbench.Domain.ProjectAccounting;

public enum BudgetKind
{
    /// <summary>The approved baseline (performance measurement baseline).</summary>
    Baseline,

    /// <summary>The current working budget.</summary>
    Current,

    /// <summary>A forecast of remaining work, used for ETC/EAC.</summary>
    Forecast,
}

public sealed class BudgetVersion : Entity
{
    public Guid ProjectId { get; set; }
    public required string Name { get; set; }
    public BudgetKind Kind { get; set; }
    public bool IsLocked { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public string? Description { get; set; }

    /// <summary>The version this one was copied from, if any.</summary>
    public Guid? SourceVersionId { get; set; }

    public DateTime? LockedAtUtc { get; set; }
    public string? LockedBy { get; set; }
    public List<BudgetLine> Lines { get; } = [];
}

public enum BudgetCostType
{
    Labor,
    Odc,
}

/// <summary>
/// A budget amount for a WBS element. Labor lines carry hours (and dollars at cost) by labor
/// category; ODC lines carry dollars by ODC type. <see cref="PeriodStart"/> null = whole project.
/// </summary>
public sealed class BudgetLine : Entity
{
    public Guid BudgetVersionId { get; set; }
    public Guid WbsElementId { get; set; }
    public BudgetCostType CostType { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public OdcType? OdcType { get; set; }

    /// <summary>First day of the fiscal period this amount is phased into.</summary>
    public DateOnly? PeriodStart { get; set; }

    public decimal Hours { get; set; }

    /// <summary>Hourly planning rate used to price a labor line (Amount = Hours × rate); 0 for ODC lines.</summary>
    public decimal PlanningRate { get; set; }

    public decimal Amount { get; set; }
}

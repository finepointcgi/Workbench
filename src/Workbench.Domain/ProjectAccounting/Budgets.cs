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
    public decimal Amount { get; set; }
}

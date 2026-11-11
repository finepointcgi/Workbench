using Workbench.Domain.ProjectAccounting;

namespace Workbench.Domain.Risks;

public enum RiskCategory { Technical, Cost, Schedule, Contract, Supply, Staffing, Security, Other }

public enum RiskStatus
{
    /// <summary>Identified, no response under way.</summary>
    Open,

    /// <summary>A response is being carried out.</summary>
    Mitigating,

    /// <summary>Knowingly carried; counted in the exposure.</summary>
    Accepted,

    /// <summary>It happened. Kept for the record; no longer exposure.</summary>
    Occurred,

    /// <summary>No longer a risk.</summary>
    Closed,
}

public enum RiskResponse { Mitigate, Avoid, Transfer, Accept }

/// <summary>
/// One risk on a project's register. Exposure is probability times cost impact; schedule impact is in working days.
/// <see cref="PartRefs"/> and <see cref="RequirementRefs"/> are free text for the IDs of parts, changes and requirements
/// tracked in other systems.
/// </summary>
public sealed class ProjectRisk : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>Per project, 1, 2, 3...: shown as R-1.</summary>
    public int Number { get; set; }

    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public RiskCategory Category { get; set; } = RiskCategory.Other;
    public int ProbabilityPercent { get; set; }
    public decimal CostImpact { get; set; }
    public int ScheduleImpactDays { get; set; }
    public RiskStatus Status { get; set; } = RiskStatus.Open;
    public RiskResponse Response { get; set; } = RiskResponse.Mitigate;
    public string? ResponsePlan { get; set; }
    public string? OwnerEmail { get; set; }
    public DateOnly? ReviewOn { get; set; }
    public string? PartRefs { get; set; }
    public string? RequirementRefs { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

namespace Workbench.Domain.Crm;

/// <summary>
/// A go/no-go gate: to move an opportunity into <see cref="ToStage"/> its team scores the checklist and, when
/// <see cref="RequiresApproval"/>, the decision goes through the "Opportunity gate" approval workflow.
/// </summary>
public sealed class GateDefinition : Entity
{
    public required string Name { get; set; }
    public OpportunityStage ToStage { get; set; }
    public string? Description { get; set; }

    /// <summary>The weighted score (0–100) a "Go" is expected to reach; below it the review is flagged.</summary>
    public int PassingScore { get; set; } = 60;

    public bool RequiresApproval { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public List<GateCriterion> Criteria { get; } = [];
}

/// <summary>One question on a gate's checklist, scored 0–5 and weighted 1–10.</summary>
public sealed class GateCriterion : Entity
{
    public Guid GateDefinitionId { get; set; }
    public required string Text { get; set; }
    public string? Guidance { get; set; }
    public int Weight { get; set; } = 1;
    public int Order { get; set; }
}

public enum GateRecommendation
{
    Go,
    NoGo,
}

public enum GateReviewStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>A scored gate decision for one opportunity. Scores and the gate's name are copied, so later gate edits don't change history.</summary>
public sealed class OpportunityGateReview : Entity
{
    public Guid OpportunityId { get; set; }
    public Guid GateDefinitionId { get; set; }
    public required string GateName { get; set; }
    public OpportunityStage ToStage { get; set; }
    public GateRecommendation Recommendation { get; set; }
    public decimal ScorePercent { get; set; }
    public int PassingScore { get; set; }
    public string? Notes { get; set; }
    public GateReviewStatus Status { get; set; }
    public required string SubmittedBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
    public List<OpportunityGateScore> Scores { get; } = [];
}

public sealed class OpportunityGateScore : Entity
{
    public Guid ReviewId { get; set; }
    public Guid? CriterionId { get; set; }
    public required string Text { get; set; }
    public int Weight { get; set; }

    /// <summary>0 (not at all) to 5 (fully).</summary>
    public int Score { get; set; }

    public string? Comment { get; set; }
}

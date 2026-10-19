namespace Workbench.Contracts.Crm;

public enum GateRecommendation { Go, NoGo }

public enum GateReviewStatus { Pending, Approved, Rejected, Cancelled }

public sealed record GateCriterionDto(Guid Id, string Text, string? Guidance, int Weight, int Order);

public sealed record GateDefinitionDto(
    Guid Id,
    string Name,
    OpportunityStage ToStage,
    string? Description,
    int PassingScore,
    bool RequiresApproval,
    bool IsActive,
    IReadOnlyList<GateCriterionDto> Criteria);

public sealed record SaveGateCriterion(string Text, int Weight = 1, string? Guidance = null);

public sealed record SaveGateDefinitionRequest(
    string Name,
    OpportunityStage ToStage,
    int PassingScore,
    bool RequiresApproval,
    IReadOnlyList<SaveGateCriterion> Criteria,
    string? Description = null,
    bool IsActive = true);

public sealed record GateScoreDto(Guid? CriterionId, string Text, int Weight, int Score, string? Comment);

public sealed record GateReviewDto(
    Guid Id,
    Guid OpportunityId,
    string OpportunityName,
    Guid GateDefinitionId,
    string GateName,
    OpportunityStage ToStage,
    GateRecommendation Recommendation,
    decimal ScorePercent,
    int PassingScore,
    string? Notes,
    GateReviewStatus Status,
    string SubmittedBy,
    DateTime SubmittedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,
    IReadOnlyList<GateScoreDto> Scores,
    bool CanDecide = false)
{
    public bool BelowPassing => ScorePercent < PassingScore;
}

public sealed record GateScoreInput(Guid CriterionId, int Score, string? Comment = null);

/// <summary>Scores a gate's checklist (each 0–5) with a Go or No-go recommendation.</summary>
public sealed record SubmitGateReviewRequest(Guid GateDefinitionId, GateRecommendation Recommendation, IReadOnlyList<GateScoreInput> Scores, string? Notes = null);

public sealed record DecideGateRequest(string? Comment = null);

/// <summary>
/// Go/no-go gates. Gates are configured by business development or administrators. An opportunity's editors submit a scored
/// review; with approval it goes through the "Opportunity gate" approval workflow (Setup › Approval workflows), and once
/// approved a Go moves the opportunity into the gate's stage and a No-go records it as No-bid. Without approval the
/// decision applies at once.
/// </summary>
public interface IOpportunityGateService
{
    Task<IReadOnlyList<GateDefinitionDto>> ListGatesAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<GateDefinitionDto> CreateGateAsync(SaveGateDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<GateDefinitionDto> UpdateGateAsync(Guid id, SaveGateDefinitionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GateReviewDto>> ListReviewsAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    Task<GateReviewDto> SubmitAsync(Guid opportunityId, SubmitGateReviewRequest request, CancellationToken cancellationToken = default);

    Task<GateReviewDto> ApproveAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default);

    Task<GateReviewDto> RejectAsync(Guid reviewId, DecideGateRequest request, CancellationToken cancellationToken = default);

    /// <summary>The submitter (or an administrator) withdraws a pending review.</summary>
    Task<GateReviewDto> CancelAsync(Guid reviewId, CancellationToken cancellationToken = default);

    /// <summary>Pending reviews the signed-in user may decide.</summary>
    Task<IReadOnlyList<GateReviewDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default);
}

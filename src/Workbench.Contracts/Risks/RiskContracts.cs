namespace Workbench.Contracts.Risks;

public enum RiskCategoryDto { Technical, Cost, Schedule, Contract, Supply, Staffing, Security, Other }

public enum RiskStatusDto { Open, Mitigating, Accepted, Occurred, Closed }

public enum RiskResponseDto { Mitigate, Avoid, Transfer, Accept }

/// <summary>One risk. <see cref="ExpectedCost"/> is probability times cost impact, <see cref="ExpectedDays"/> probability times schedule impact.</summary>
public sealed record RiskDto(
    Guid Id,
    Guid ProjectId,
    int Number,
    string Title,
    string? Description,
    RiskCategoryDto Category,
    int ProbabilityPercent,
    decimal CostImpact,
    int ScheduleImpactDays,
    decimal ExpectedCost,
    decimal ExpectedDays,
    RiskStatusDto Status,
    RiskResponseDto Response,
    string? ResponsePlan,
    string? OwnerEmail,
    DateOnly? ReviewOn,
    bool ReviewOverdue,
    string? PartRefs,
    string? RequirementRefs,
    DateTime CreatedAtUtc,
    DateTime? UpdatedAtUtc)
{
    public string Label => $"R-{Number}";

    /// <summary>Still counts toward exposure: open, being mitigated or accepted.</summary>
    public bool IsLive => Status is RiskStatusDto.Open or RiskStatusDto.Mitigating or RiskStatusDto.Accepted;
}

/// <summary>
/// What the live risks add up to, against the management reserve in the project's earned-value baseline (null without
/// one). <see cref="ReserveLeft"/> is the reserve less the expected cost, so a negative number means the risks outweigh it.
/// </summary>
public sealed record RiskSummaryDto(
    int LiveCount,
    int HighCount,
    int ReviewsOverdue,
    decimal ExpectedCost,
    decimal WorstCaseCost,
    decimal ExpectedDays,
    decimal? ManagementReserve,
    decimal? ReserveLeft);

public sealed record RiskRegisterDto(Guid ProjectId, RiskSummaryDto Summary, IReadOnlyList<RiskDto> Risks);

public sealed record SaveRiskRequest(
    string Title,
    RiskCategoryDto Category,
    int ProbabilityPercent,
    decimal CostImpact,
    int ScheduleImpactDays,
    RiskStatusDto Status,
    RiskResponseDto Response,
    string? Description = null,
    string? ResponsePlan = null,
    string? OwnerEmail = null,
    DateOnly? ReviewOn = null,
    string? PartRefs = null,
    string? RequirementRefs = null);

/// <summary>A project's risk register. Whoever may see the project's financials (its manager, Finance, Admin) reads and edits it.</summary>
public interface IRiskService
{
    /// <summary>The register, live risks first by expected cost; closed ones too only when asked.</summary>
    Task<RiskRegisterDto> GetRegisterAsync(Guid projectId, bool includeClosed = false, CancellationToken cancellationToken = default);

    Task<RiskDto> GetAsync(Guid riskId, CancellationToken cancellationToken = default);

    Task<RiskDto> CreateAsync(Guid projectId, SaveRiskRequest request, CancellationToken cancellationToken = default);

    Task<RiskDto> UpdateAsync(Guid riskId, SaveRiskRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid riskId, CancellationToken cancellationToken = default);
}

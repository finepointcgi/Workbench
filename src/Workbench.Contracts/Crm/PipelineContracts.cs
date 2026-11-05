namespace Workbench.Contracts.Crm;

/// <summary>One card on the pipeline board.</summary>
public sealed record PipelineCardDto(
    Guid Id,
    string Number,
    string Name,
    string? AgencyName,
    OpportunityStage Stage,
    decimal EstimatedValue,
    decimal WeightedValue,
    int Pwin,
    int Pgo,
    DateOnly? ProposalDueOn,
    string? CaptureManagerName,
    OpportunityKind Kind,
    bool CanEdit,
    bool HasPendingGate);

public sealed record PipelineLaneDto(OpportunityStage Stage, IReadOnlyList<PipelineCardDto> Cards, decimal TotalValue, decimal WeightedValue, string? GateName);

public sealed record PipelineBoardDto(IReadOnlyList<PipelineLaneDto> Lanes);

/// <summary>Open pipeline in one fiscal quarter, by expected award date, weighted by Pwin × Pgo.</summary>
public sealed record ForecastBucketDto(
    int FiscalYear,
    int Quarter,
    int Count,
    decimal TotalValue,
    decimal WeightedValue,
    decimal NewWeighted,
    decimal RecompeteWeighted,
    decimal WonValue)
{
    public string Label => $"FY{FiscalYear % 100:00} Q{Quarter}";
}

public sealed record ForecastDto(
    IReadOnlyList<ForecastBucketDto> Quarters,
    decimal TotalValue,
    decimal WeightedValue,
    decimal NewWeighted,
    decimal RecompeteWeighted,
    int UndatedCount,
    decimal UndatedWeighted);

public sealed record WinLossReasonDto(OutcomeReason Reason, int Won, int Lost, int NoBid);

public sealed record WinLossByGroupDto(string Group, int Won, int Lost, int NoBid, decimal WonValue, decimal LostValue)
{
    /// <summary>Won ÷ (won + lost) by count; null with no decisions.</summary>
    public decimal? WinRate => Won + Lost == 0 ? null : Math.Round(100m * Won / (Won + Lost), 1);
}

public sealed record WinLossDto(
    int Won,
    int Lost,
    int NoBid,
    decimal WonValue,
    decimal LostValue,
    decimal? WinRateByCount,
    decimal? WinRateByValue,
    IReadOnlyList<WinLossReasonDto> Reasons,
    IReadOnlyList<WinLossByGroupDto> ByAgency,
    IReadOnlyList<WinLossByGroupDto> ByFiscalYear,
    IReadOnlyList<WinLossByGroupDto> ByKind);

/// <summary>
/// Bid-and-proposal effort on an opportunity: hours charged to its B&amp;P charge code (approved and still in review).
/// Labor cost is shown to Finance and administrators only (it's computed from cost rates).
/// </summary>
public sealed record BpSpendDto(
    Guid OpportunityId,
    string Number,
    string Name,
    OpportunityStage Stage,
    string? ChargeCode,
    decimal ApprovedHours,
    decimal PendingHours,
    decimal? LaborCost,
    int People,
    DateOnly? FirstCharged,
    DateOnly? LastCharged);

public enum UpcomingKind { ProposalDue, RfiDue, RfpRelease, Milestone, Task, AwardExpected }

/// <summary>Something dated coming up on a pursuit the viewer sees: a due date, a schedule milestone or an open task.</summary>
public sealed record UpcomingItemDto(Guid OpportunityId, string Number, string Name, UpcomingKind Kind, string What, DateOnly DueOn, string? Owner);

public enum AttentionSeverity { Info, Warning, Critical }

/// <summary>A pursuit that needs someone to act: no capture manager, stalled in its stage, a missed date, no Pwin assessment...</summary>
public sealed record AttentionItemDto(Guid OpportunityId, string Number, string Name, OpportunityStage Stage, string Reason, AttentionSeverity Severity);

/// <summary>One step of the funnel: open pursuits in a stage.</summary>
public sealed record FunnelStepDto(OpportunityStage Stage, int Count, decimal TotalValue, decimal WeightedValue);

/// <summary>
/// The business development home: headline figures, the funnel, what's due in the next <see cref="WindowDays"/> days and what
/// needs attention. Win rate and won value cover decisions in the last 12 months.
/// </summary>
public sealed record BdHighlightsDto(
    int OpenCount,
    decimal OpenValue,
    decimal WeightedValue,
    int DueSoonCount,
    decimal? WinRate12Months,
    decimal WonValue12Months,
    int Won12Months,
    int Lost12Months,
    IReadOnlyList<FunnelStepDto> Funnel,
    IReadOnlyList<UpcomingItemDto> Upcoming,
    IReadOnlyList<AttentionItemDto> Attention,
    int WindowDays);

public sealed record CrmSettingsDto(Guid? BpParentWbsElementId, string? BpParentCode, string? BpParentName, bool BpParentFound);

/// <summary>A charge code a B&amp;P element may be (indirect elements), for the settings picker.</summary>
public sealed record BpParentOptionDto(Guid Id, string Code, string Name, string ProjectCode);

/// <summary>The pipeline views: board, forecast, win/loss and B&amp;P spending; plus the B&amp;P setting. Anyone who sees the pipeline.</summary>
public interface IPipelineService
{
    /// <summary>Open stages as lanes (outcomes left out unless <paramref name="includeOutcomes"/>).</summary>
    Task<PipelineBoardDto> GetBoardAsync(bool includeOutcomes = false, CancellationToken cancellationToken = default);

    Task<ForecastDto> GetForecastAsync(CancellationToken cancellationToken = default);

    /// <summary>Headline figures, funnel, what's coming up and what needs attention, over the pursuits the viewer sees.</summary>
    Task<BdHighlightsDto> GetHighlightsAsync(int windowDays = 30, CancellationToken cancellationToken = default);

    Task<WinLossDto> GetWinLossAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BpSpendDto>> GetBpSpendAsync(Guid? opportunityId = null, CancellationToken cancellationToken = default);

    Task<CrmSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finance or administrators: the indirect element opportunity B&amp;P charge codes go under.</summary>
    Task<CrmSettingsDto> SetBpParentAsync(Guid? wbsElementId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BpParentOptionDto>> ListBpParentOptionsAsync(CancellationToken cancellationToken = default);
}

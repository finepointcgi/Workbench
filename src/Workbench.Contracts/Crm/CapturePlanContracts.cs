namespace Workbench.Contracts.Crm;

public enum CapturePlanItemKind { WinTheme, Discriminator, HotButton, Strength, Weakness, Opportunity, Threat, Risk }

public enum MilestoneKind
{
    IndustryDay,
    DraftRfp,
    QuestionsDue,
    RfpRelease,
    BlackHat,
    BlueTeam,
    Kickoff,
    PinkTeam,
    RedTeam,
    GoldTeam,
    WhiteGlove,
    Submission,
    Orals,
    Other,
}

public static class CapturePlanLabels
{
    public static string Label(CapturePlanItemKind kind) => kind switch
    {
        CapturePlanItemKind.WinTheme => "Win theme",
        CapturePlanItemKind.HotButton => "Customer hot button",
        _ => kind.ToString(),
    };

    public static string Label(MilestoneKind kind) => kind switch
    {
        MilestoneKind.IndustryDay => "Industry day",
        MilestoneKind.DraftRfp => "Draft RFP",
        MilestoneKind.QuestionsDue => "Questions due",
        MilestoneKind.RfpRelease => "RFP release",
        MilestoneKind.BlackHat => "Black hat review",
        MilestoneKind.BlueTeam => "Blue team (solution)",
        MilestoneKind.Kickoff => "Proposal kickoff",
        MilestoneKind.PinkTeam => "Pink team",
        MilestoneKind.RedTeam => "Red team",
        MilestoneKind.GoldTeam => "Gold team",
        MilestoneKind.WhiteGlove => "White glove",
        MilestoneKind.Submission => "Submission",
        MilestoneKind.Orals => "Oral presentation",
        _ => "Milestone",
    };

    /// <summary>The color reviews, in the order a proposal goes through them.</summary>
    public static bool IsColorReview(MilestoneKind kind) =>
        kind is MilestoneKind.BlackHat or MilestoneKind.BlueTeam or MilestoneKind.PinkTeam or MilestoneKind.RedTeam or MilestoneKind.GoldTeam or MilestoneKind.WhiteGlove;
}

public sealed record PlanItemDto(Guid Id, CapturePlanItemKind Kind, string Text, string? Detail, int Order, string CreatedBy, DateTime CreatedAtUtc);

public sealed record SavePlanItemRequest(CapturePlanItemKind Kind, string Text, string? Detail = null);

public sealed record MilestoneDto(
    Guid Id,
    Guid OpportunityId,
    MilestoneKind Kind,
    string Name,
    DateOnly DueOn,
    string? OwnerEmail,
    string? OwnerName,
    string? Notes,
    bool IsDone);

public sealed record SaveMilestoneRequest(MilestoneKind Kind, string Name, DateOnly DueOn, string? OwnerEmail = null, string? Notes = null);

/// <summary>Six factors, each 0 (not at all) to 5 (fully). Weights: customer 25, solution 20, past performance 20, price 15, competition 10, teaming 10.</summary>
public sealed record PwinFactors(int CustomerRelationship, int SolutionFit, int PastPerformance, int PriceToWin, int CompetitivePosition, int Teaming)
{
    public static readonly PwinFactors Empty = new(0, 0, 0, 0, 0, 0);

    public static readonly IReadOnlyList<(string Key, string Label, string Question, int Weight)> Definitions =
    [
        ("customer", "Customer relationship", "Do we know the customer, and do they know and trust us?", 25),
        ("solution", "Solution fit", "Do we have a solution that meets their needs and hot buttons?", 20),
        ("past", "Past performance", "Do we have relevant, recent past performance with strong ratings?", 20),
        ("price", "Price to win", "Can we price at or below what wins, at a margin we accept?", 15),
        ("competition", "Competitive position", "How strong are we against the incumbent and the field?", 10),
        ("teaming", "Teaming", "Do we have the partners, set-aside status and workshare we need?", 10),
    ];

    public int this[string key] => key switch
    {
        "customer" => CustomerRelationship,
        "solution" => SolutionFit,
        "past" => PastPerformance,
        "price" => PriceToWin,
        "competition" => CompetitivePosition,
        "teaming" => Teaming,
        _ => 0,
    };

    public PwinFactors With(string key, int value) => key switch
    {
        "customer" => this with { CustomerRelationship = value },
        "solution" => this with { SolutionFit = value },
        "past" => this with { PastPerformance = value },
        "price" => this with { PriceToWin = value },
        "competition" => this with { CompetitivePosition = value },
        "teaming" => this with { Teaming = value },
        _ => this,
    };

    /// <summary>Weighted score, 0–100.</summary>
    public decimal Score => Definitions.Sum(d => d.Weight * Math.Clamp(this[d.Key], 0, 5) / 5m);

    /// <summary>The Pwin the score suggests: the score itself, kept between 5% and 90% (no pursuit is a sure thing).</summary>
    public int SuggestedPwin => (int)Math.Clamp(Math.Round(Score, MidpointRounding.AwayFromZero), 5m, 90m);
}

public sealed record PwinAssessmentDto(PwinFactors Factors, decimal Score, int SuggestedPwin, string? Notes, string AssessedBy, DateTime AssessedAtUtc);

public sealed record SavePwinAssessmentRequest(PwinFactors Factors, string? Notes = null, bool ApplyToOpportunity = true);

/// <summary>How we've done before with this buyer: decided pursuits for the opportunity's agency (and its offices).</summary>
public sealed record WinHistoryDto(string Scope, int Won, int Lost)
{
    public decimal? WinRate => Won + Lost == 0 ? null : Math.Round(100m * Won / (Won + Lost), 0);
}

/// <summary>An opportunity's capture plan, schedule and Pwin assessment.</summary>
public sealed record CapturePlanDto(
    Guid OpportunityId,
    IReadOnlyList<PlanItemDto> Items,
    IReadOnlyList<MilestoneDto> Milestones,
    PwinAssessmentDto? Assessment,
    WinHistoryDto? AgencyHistory,
    bool CanEdit);

/// <summary>
/// Capture plans: win themes, discriminators, hot buttons, SWOT and risks; the pursuit schedule with color-team reviews; and
/// the Pwin assessment. Whoever may see an opportunity reads its plan; whoever may edit it changes the plan.
/// </summary>
public interface ICapturePlanService
{
    Task<CapturePlanDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    Task<PlanItemDto> AddItemAsync(Guid opportunityId, SavePlanItemRequest request, CancellationToken cancellationToken = default);

    Task<PlanItemDto> UpdateItemAsync(Guid itemId, SavePlanItemRequest request, CancellationToken cancellationToken = default);

    Task DeleteItemAsync(Guid itemId, CancellationToken cancellationToken = default);

    Task<MilestoneDto> AddMilestoneAsync(Guid opportunityId, SaveMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<MilestoneDto> UpdateMilestoneAsync(Guid milestoneId, SaveMilestoneRequest request, CancellationToken cancellationToken = default);

    Task<MilestoneDto> SetMilestoneDoneAsync(Guid milestoneId, bool done, CancellationToken cancellationToken = default);

    Task DeleteMilestoneAsync(Guid milestoneId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Back-plans the standard color-team schedule from the proposal due date (black hat, blue team, kickoff, pink, red, gold,
    /// white glove, submission), skipping kinds already on the schedule and dates already past. Returns the whole schedule.
    /// </summary>
    Task<IReadOnlyList<MilestoneDto>> AddStandardScheduleAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    /// <summary>Saves the assessment and, when asked, sets the opportunity's Pwin to the suggested value.</summary>
    Task<PwinAssessmentDto> SaveAssessmentAsync(Guid opportunityId, SavePwinAssessmentRequest request, CancellationToken cancellationToken = default);
}

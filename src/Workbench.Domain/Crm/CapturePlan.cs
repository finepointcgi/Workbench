namespace Workbench.Domain.Crm;

/// <summary>What a capture plan entry is: a win strategy element, one quadrant of the SWOT, or a capture risk.</summary>
public enum CapturePlanItemKind
{
    WinTheme,
    Discriminator,
    HotButton,
    Strength,
    Weakness,
    Opportunity,
    Threat,
    Risk,
}

/// <summary>One line of an opportunity's capture plan (win themes, discriminators, customer hot buttons, SWOT, risks).</summary>
public sealed class OpportunityPlanItem : Entity
{
    public Guid OpportunityId { get; set; }
    public CapturePlanItemKind Kind { get; set; }
    public required string Text { get; set; }
    public string? Detail { get; set; }
    public int Order { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>A dated event on a pursuit's schedule: solicitation dates and the proposal's color-team reviews.</summary>
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

public sealed class OpportunityMilestone : Entity
{
    public Guid OpportunityId { get; set; }
    public MilestoneKind Kind { get; set; }
    public required string Name { get; set; }
    public DateOnly DueOn { get; set; }

    /// <summary>Who runs it (lower-case email).</summary>
    public string? OwnerEmail { get; set; }

    public string? Notes { get; set; }
    public bool IsDone { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>
/// The capture team's structured Pwin assessment (one per opportunity): six factors scored 0–5. The weighted score suggests
/// a Pwin; applying it sets <see cref="Opportunity.Pwin"/>.
/// </summary>
public sealed class OpportunityPwinAssessment : Entity
{
    public Guid OpportunityId { get; set; }
    public int CustomerRelationship { get; set; }
    public int SolutionFit { get; set; }
    public int PastPerformance { get; set; }
    public int PriceToWin { get; set; }
    public int CompetitivePosition { get; set; }
    public int Teaming { get; set; }
    public string? Notes { get; set; }
    public required string AssessedBy { get; set; }
    public DateTime AssessedAtUtc { get; set; }
}

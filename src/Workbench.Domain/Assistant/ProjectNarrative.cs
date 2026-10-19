namespace Workbench.Domain.Assistant;

/// <summary>
/// A project's monthly PM narrative for one fiscal period, written (or edited from a draft) and saved by the PM. The
/// assistant only drafts; nothing is stored until a person saves it. Feeds the IPMDAR Format 5 narrative.
/// </summary>
public sealed class ProjectNarrative : Entity
{
    public Guid ProjectId { get; set; }

    public Guid FiscalPeriodId { get; set; }

    public required string PeriodName { get; set; }

    public string? Summary { get; set; }

    public string? CostVariance { get; set; }

    public string? ScheduleVariance { get; set; }

    public string? EacChanges { get; set; }

    public string? Accomplishments { get; set; }

    public string? UpcomingMilestones { get; set; }

    public string? Risks { get; set; }

    /// <summary>How the text was started: "assistant", "template" (assistant off) or "manual".</summary>
    public required string DraftSource { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public required string UpdatedBy { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}

/// <summary>A cached plain-language explanation of one timesheet flag (so the same facts aren't sent twice).</summary>
public sealed class TimesheetFlagExplanation : Entity
{
    public Guid TimesheetId { get; set; }

    public required string RuleKey { get; set; }

    /// <summary>A hash of the flag's facts; a changed timesheet gets a new explanation.</summary>
    public required string FactsHash { get; set; }

    public required string Text { get; set; }

    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

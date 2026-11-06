namespace Workbench.Domain.Crm;

/// <summary>
/// A saved SAM.gov search. Running it (by hand, or daily when SAM.gov is configured) adds notices it hasn't seen as
/// <see cref="OpportunityLead"/>s for review, skipping solicitations already in the pipeline.
/// </summary>
public sealed class SavedLeadSearch : Entity
{
    public required string Name { get; set; }

    /// <summary>Comma-separated NAICS codes, agency names, set-asides (<see cref="SetAside"/> names) and keywords.</summary>
    public string? NaicsCodes { get; set; }
    public string? Agencies { get; set; }
    public string? SetAsides { get; set; }
    public string? Keywords { get; set; }

    /// <summary>How far back each run looks at posted notices.</summary>
    public int PostedWithinDays { get; set; } = 30;

    public bool IsActive { get; set; } = true;
    public required string OwnerEmail { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public int LastRunNewLeads { get; set; }
}

public enum LeadStatus
{
    New,
    Accepted,
    Dismissed,
}

/// <summary>A notice found by a search or a CSV import, waiting to become an opportunity or be dismissed.</summary>
public sealed class OpportunityLead : Entity
{
    public OpportunitySource Source { get; set; }

    /// <summary>The source's own id (SAM.gov notice id; a CSV row's notice id when it has one).</summary>
    public string? SourceId { get; set; }

    public string? SolicitationNumber { get; set; }

    /// <summary>Normalized solicitation number (see <see cref="Opportunity.SolicitationKey"/>), for de-duplication.</summary>
    public string? SolicitationKey { get; set; }

    public required string Title { get; set; }
    public string? Agency { get; set; }
    public string? Office { get; set; }
    public string? NaicsCode { get; set; }
    public SetAside SetAside { get; set; }
    public string? NoticeType { get; set; }
    public DateOnly? PostedOn { get; set; }
    public DateOnly? ResponseDueOn { get; set; }
    public string? PlaceOfPerformance { get; set; }
    public string? Url { get; set; }
    public string? Description { get; set; }
    public decimal? EstimatedValue { get; set; }

    /// <summary>How well the notice fits the company's profile (0–100) and why; null until scored. See <c>LeadFitScorer</c>.</summary>
    public int? FitScore { get; set; }
    public string? FitReason { get; set; }

    /// <summary>True when the assistant refined the rule-based score; false when the rules alone scored it.</summary>
    public bool FitByAssistant { get; set; }
    public DateTime? FitScoredAtUtc { get; set; }

    public Guid? SavedSearchId { get; set; }
    public LeadStatus Status { get; set; }
    public Guid? OpportunityId { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Business development settings (one row): the indirect element opportunity B&amp;P charge codes go under.</summary>
public sealed class CrmSettings : Entity
{
    /// <summary>The B&amp;P WBS element (e.g. <c>IND.BP</c>). Null: found by code/name in the indirect project.</summary>
    public Guid? BpParentWbsElementId { get; set; }

    /// <summary>What the company does and wins (NAICS codes, set-asides, capabilities, past performance): what lead fit scores and proposal drafts are written against.</summary>
    public string? CompanyProfile { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
}

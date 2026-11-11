namespace Workbench.Domain.Compliance;

/// <summary>Where one NIST SP 800-171 requirement stands in the organization's own assessment.</summary>
public enum CmmcStatus
{
    NotAssessed,
    Implemented,

    /// <summary>Not met, wholly or partly. Costs the requirement's points in the SPRS score.</summary>
    NotImplemented,

    /// <summary>Doesn't apply to the organization's systems, with a reason in the implementation note.</summary>
    NotApplicable,
}

/// <summary>
/// The organization's assessment of one NIST SP 800-171 requirement (the catalogue itself, with its points, lives in code).
/// Notes hold references to controlled documents (an SSP section, a ticket, a Keel record), never the controlled content.
/// </summary>
public sealed class CmmcPractice : Entity
{
    /// <summary>The requirement id, for example "3.1.1". Unique.</summary>
    public string PracticeId { get; set; } = "";

    public CmmcStatus Status { get; set; }

    /// <summary>For 3.5.3 and 3.13.11 only: partly met, so 3 points are lost instead of 5.</summary>
    public bool PartialCredit { get; set; }

    public string? Implementation { get; set; }
    public string? Evidence { get; set; }
    public string? OwnerEmail { get; set; }
    public DateOnly? AssessedOn { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum CmmcPoamStatus { Open, Done }

/// <summary>One milestone on the plan of action and milestones (POA&amp;M) for a requirement that isn't met yet.</summary>
public sealed class CmmcPoamItem : Entity
{
    public string PracticeId { get; set; } = "";
    public string Milestone { get; set; } = "";
    public string? OwnerEmail { get; set; }
    public DateOnly? DueOn { get; set; }
    public CmmcPoamStatus Status { get; set; }
    public DateOnly? CompletedOn { get; set; }
    public string? Notes { get; set; }
    public string CreatedBy { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

namespace Workbench.Domain.Crm;

/// <summary>What a solicitation line is: an instruction on how to write the proposal (Section L), how it's scored (Section M), or something to do or deliver (the work statement).</summary>
public enum RequirementKind
{
    Instruction,
    Evaluation,
    Technical,
    Administrative,
}

public enum RequirementStatus
{
    Open,
    Addressed,
    NotApplicable,
}

/// <summary>
/// One line of an opportunity's compliance matrix, read from the solicitation: its reference in the document, the
/// requirement, which proposal section answers it, and whether it has been addressed.
/// </summary>
public sealed class OpportunityRequirement : Entity
{
    public Guid OpportunityId { get; set; }

    /// <summary>Where it is in the solicitation, e.g. <c>L.3.2</c> or <c>C.4.1</c>.</summary>
    public string? Reference { get; set; }

    public required string Text { get; set; }
    public RequirementKind Kind { get; set; }
    public RequirementStatus Status { get; set; }

    /// <summary>The proposal section that answers it (a <c>ProposalTemplate</c> key), if known.</summary>
    public string? ProposalSectionKey { get; set; }

    public string? Notes { get; set; }
    public int Order { get; set; }

    /// <summary>True when the assistant cleaned up the text or chose the kind and section.</summary>
    public bool ByAssistant { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

namespace Workbench.Domain.Crm;

/// <summary>
/// One section of an opportunity's proposal draft. <see cref="Key"/> names a standard section (summary, requirement,
/// approach, management, past, risks) so a re-draft finds its own; <see cref="EditedByHand"/> protects a person's edits
/// from being replaced by the next draft.
/// </summary>
public sealed class ProposalSection : Entity
{
    public Guid OpportunityId { get; set; }
    public required string Key { get; set; }
    public required string Title { get; set; }
    public string Content { get; set; } = "";
    public int Order { get; set; }

    /// <summary>True when the assistant wrote or rewrote the current text; false for the rules-based template.</summary>
    public bool ByAssistant { get; set; }

    public bool EditedByHand { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

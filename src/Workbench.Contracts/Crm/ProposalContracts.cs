namespace Workbench.Contracts.Crm;

public sealed record ProposalSectionDto(
    Guid Id,
    string Key,
    string Title,
    string Content,
    int Order,
    bool ByAssistant,
    bool EditedByHand,
    string UpdatedBy,
    DateTime UpdatedAtUtc);

/// <summary>An opportunity's proposal draft. <paramref name="CanEdit"/>: the viewer may draft and edit it.</summary>
public sealed record ProposalDto(Guid OpportunityId, IReadOnlyList<ProposalSectionDto> Sections, bool CanEdit);

/// <summary>What a draft did: sections created, rewritten (not edited by hand) and left alone (edited by hand), and how many the assistant wrote.</summary>
public sealed record ProposalDraftResultDto(ProposalDto Proposal, int Created, int Rewritten, int KeptEdited, int WrittenByAssistant);

public sealed record SaveProposalSectionRequest(string Title, string Content);

public sealed record ProposalFileDto(string FileName, string ContentType, byte[] Content);

/// <summary>
/// A first-draft proposal built from the opportunity, its capture plan (win themes, discriminators, hot buttons, risks),
/// team and competition, and the company profile. The rules write a template with <c>[ADD: …]</c> placeholders where the
/// facts run out; the assistant, when it's on, rewrites it into prose without inventing facts. People edit, re-draft one
/// section, and export to Word. Whoever may see an opportunity reads its proposal; whoever may edit it changes it.
/// </summary>
public interface IProposalService
{
    Task<ProposalDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    /// <summary>Creates the standard sections and rewrites those nobody has edited by hand; edited sections are kept.</summary>
    Task<ProposalDraftResultDto> DraftAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    /// <summary>Re-drafts one section from the plan, replacing its text (including hand edits).</summary>
    Task<ProposalSectionDto> RedraftSectionAsync(Guid sectionId, CancellationToken cancellationToken = default);

    Task<ProposalSectionDto> SaveSectionAsync(Guid sectionId, SaveProposalSectionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The proposal as a Word document.</summary>
    Task<ProposalFileDto> ExportAsync(Guid opportunityId, CancellationToken cancellationToken = default);
}

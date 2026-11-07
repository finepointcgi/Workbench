namespace Workbench.Contracts.Crm;

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

public static class RequirementLabels
{
    /// <summary>The proposal sections a requirement can be answered in (the keys of the proposal draft).</summary>
    public static readonly IReadOnlyList<(string Key, string Title)> ProposalSections =
    [
        ("summary", "Executive summary"),
        ("requirement", "Understanding of the requirement"),
        ("approach", "Technical approach"),
        ("management", "Management approach and staffing"),
        ("past", "Past performance"),
        ("risks", "Risk management"),
    ];

    public static string Label(RequirementKind kind) => kind switch
    {
        RequirementKind.Instruction => "Instruction (Section L)",
        RequirementKind.Evaluation => "Evaluation (Section M)",
        RequirementKind.Technical => "Work to perform",
        _ => "Administrative",
    };

    public static string Label(RequirementStatus status) => status switch
    {
        RequirementStatus.Addressed => "Addressed",
        RequirementStatus.NotApplicable => "Not applicable",
        _ => "Open",
    };
}

public sealed record RequirementDto(
    Guid Id,
    string? Reference,
    string Text,
    RequirementKind Kind,
    RequirementStatus Status,
    string? ProposalSectionKey,
    string? Notes,
    int Order,
    bool ByAssistant);

/// <summary>An opportunity's compliance matrix. <paramref name="CanEdit"/>: the viewer may shred, add and change lines.</summary>
public sealed record RequirementsDto(Guid OpportunityId, IReadOnlyList<RequirementDto> Items, bool CanEdit);

/// <summary>
/// The solicitation to read: pasted text, or an uploaded file (<paramref name="FileName"/> and <paramref name="Content"/>:
/// PDF with a text layer, Word .docx or plain text). Give one or the other.
/// </summary>
public sealed record ShredRequest(string? Text, string? FileName = null, byte[]? Content = null);

public sealed record ShredDateDto(MilestoneKind Kind, string Name, DateOnly Date, bool Added);

/// <summary>What reading a solicitation did: lines added, lines skipped because the matrix already had them, dates found, how many the assistant cleaned up, and anything the person should know.</summary>
public sealed record ShredResultDto(
    RequirementsDto Requirements,
    int Added,
    int Duplicates,
    int ByAssistant,
    IReadOnlyList<ShredDateDto> Dates,
    IReadOnlyList<string> Notes);

public sealed record SaveRequirementRequest(
    string Text,
    RequirementKind Kind,
    RequirementStatus Status,
    string? Reference = null,
    string? ProposalSectionKey = null,
    string? Notes = null);

public sealed record RequirementFileDto(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Reads a solicitation into a compliance matrix: the instructions (Section L), evaluation factors (Section M) and "shall"
/// statements of the work, plus its key dates. Rules read it first so it works with the assistant off; the assistant, when
/// it's on, tidies the lines and says which proposal section answers each. It never invents a requirement: every line is
/// text from the document. Whoever may see an opportunity reads its matrix; whoever may edit it shreds and changes it.
/// </summary>
public interface IRequirementService
{
    Task<RequirementsDto> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default);

    Task<ShredResultDto> ShredAsync(Guid opportunityId, ShredRequest request, CancellationToken cancellationToken = default);

    Task<RequirementDto> AddAsync(Guid opportunityId, SaveRequirementRequest request, CancellationToken cancellationToken = default);

    Task<RequirementDto> UpdateAsync(Guid requirementId, SaveRequirementRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid requirementId, CancellationToken cancellationToken = default);

    /// <summary>The matrix as a CSV file.</summary>
    Task<RequirementFileDto> ExportAsync(Guid opportunityId, CancellationToken cancellationToken = default);
}

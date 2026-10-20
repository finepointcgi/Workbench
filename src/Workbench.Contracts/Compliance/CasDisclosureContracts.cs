namespace Workbench.Contracts.Compliance;

// CAS disclosure statement support (Finance and administrators): the DS-1 parts I–VIII as structured items with answers,
// versions with effective dates, links from items to the live configuration (indirect pools and bases, the labor cost
// method, the unallowable cost policy, capitalization and depreciation), an exported document, and a CAS 401/402
// consistency check between estimating (proposal pricing) and accounting practice.

public enum DisclosureFormType { Ds1, Ds2 }

public enum DisclosureState { Draft, Effective, Superseded }

public sealed record DisclosureChoiceDto(string Code, string Label);

public sealed record DisclosureItemDto(
    string Key,
    int Part,
    string Question,
    IReadOnlyList<DisclosureChoiceDto> Choices,

    /// <summary>What in Workbench the item describes (for example "Overhead pool base"), when it's linked.</summary>
    string? LinkedTo,

    /// <summary>The live configuration as a choice code (or amount), and readable.</summary>
    string? ConfiguredChoice,
    string? ConfiguredText,
    string? Choice,
    string? Text,
    bool Answered,

    /// <summary>Null when the item isn't linked or isn't answered; false when the answer differs from the configuration.</summary>
    bool? MatchesConfiguration,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record DisclosurePartDto(int Number, string Roman, string Title, IReadOnlyList<DisclosureItemDto> Items)
{
    public int Answered => Items.Count(i => i.Answered);
}

public sealed record DisclosureVersionDto(
    Guid Id,
    DisclosureFormType Form,
    int Version,
    string Title,
    DisclosureState Status,
    DateOnly? EffectiveFrom,
    DateOnly? EffectiveTo,
    string? ChangeSummary,
    int Answered,
    int Total,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? MadeEffectiveBy,
    DateTime? MadeEffectiveAtUtc);

public sealed record DisclosureStatementDto(DisclosureVersionDto Version, IReadOnlyList<DisclosurePartDto> Parts);

/// <summary>A new draft version, copying the answers of the latest version of the form (if there is one).</summary>
public sealed record CreateDisclosureVersionRequest(DisclosureFormType Form = DisclosureFormType.Ds1, string? Title = null, string? ChangeSummary = null);

public sealed record SaveDisclosureAnswerRequest(string ItemKey, string? Choice, string? Text);

/// <summary>Puts a draft in force from a date; the version in force until then is superseded the day before.</summary>
public sealed record MakeDisclosureEffectiveRequest(DateOnly EffectiveFrom, string? ChangeSummary = null);

public enum ConsistencySeverity { Info, Warning, Problem }

public sealed record CasConsistencyFindingDto(string Standard, ConsistencySeverity Severity, string Area, string Finding, string Estimating, string Accounting, string? Link = null);

public sealed record CasConsistencyDto(DateTime CheckedAtUtc, Guid? StatementId, int? Version, IReadOnlyList<CasConsistencyFindingDto> Findings)
{
    public int Problems => Findings.Count(f => f.Severity == ConsistencySeverity.Problem);
    public int Warnings => Findings.Count(f => f.Severity == ConsistencySeverity.Warning);
}

public interface ICasDisclosureService
{
    Task<IReadOnlyList<DisclosureVersionDto>> ListVersionsAsync(DisclosureFormType? form = null, CancellationToken cancellationToken = default);

    Task<DisclosureStatementDto> GetAsync(Guid statementId, CancellationToken cancellationToken = default);

    Task<DisclosureStatementDto> CreateVersionAsync(CreateDisclosureVersionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Saves answers on a draft (an empty choice and text clears an answer). Effective and superseded versions are read-only.</summary>
    Task<DisclosureStatementDto> SaveAnswersAsync(Guid statementId, IReadOnlyList<SaveDisclosureAnswerRequest> answers, CancellationToken cancellationToken = default);

    /// <summary>Fills the draft's unanswered linked items from the live configuration.</summary>
    Task<DisclosureStatementDto> FillFromConfigurationAsync(Guid statementId, CancellationToken cancellationToken = default);

    Task<DisclosureStatementDto> MakeEffectiveAsync(Guid statementId, MakeDisclosureEffectiveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft that was never effective.</summary>
    Task DeleteDraftAsync(Guid statementId, CancellationToken cancellationToken = default);

    /// <summary>
    /// CAS 401/402: compares the disclosed practice (the given statement, or the one in force, or the latest draft) and
    /// proposal pricing with the accounting configuration.
    /// </summary>
    Task<CasConsistencyDto> CheckConsistencyAsync(Guid? statementId = null, CancellationToken cancellationToken = default);

    /// <summary>The statement as a printable HTML document.</summary>
    Task<ComplianceExportFile> ExportAsync(Guid statementId, CancellationToken cancellationToken = default);
}

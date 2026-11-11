namespace Workbench.Contracts.Compliance;

public enum CmmcStatusDto { NotAssessed, Implemented, NotImplemented, NotApplicable }

public enum CmmcPoamStatusDto { Open, Done }

/// <summary>
/// One NIST SP 800-171 Rev 2 requirement with the organization's assessment of it. <see cref="Weight"/> is its SPRS value
/// (5, 3 or 1); <see cref="Deduction"/> is what it costs now (0 unless Not implemented; 3 for a partly met 3.5.3 or 3.13.11).
/// </summary>
public sealed record CmmcPracticeDto(
    string PracticeId,
    string Family,
    string Statement,
    int Weight,
    bool IsBasic,
    bool AllowsPartialCredit,
    CmmcStatusDto Status,
    bool PartialCredit,
    int Deduction,
    string? Implementation,
    string? Evidence,
    string? OwnerEmail,
    DateOnly? AssessedOn,
    int OpenPoamItems,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc);

public sealed record CmmcPoamItemDto(
    Guid Id,
    string PracticeId,
    string Statement,
    string Milestone,
    string? OwnerEmail,
    DateOnly? DueOn,
    bool Overdue,
    CmmcPoamStatusDto Status,
    DateOnly? CompletedOn,
    string? Notes);

public sealed record CmmcFamilySummaryDto(string Family, int Total, int Implemented, int NotImplemented, int NotApplicable, int NotAssessed);

/// <summary>
/// The assessment so far. <see cref="Score"/> is 110 less the points of every requirement marked Not implemented; requirements
/// not yet assessed don't cost anything yet, so <see cref="WorstCaseScore"/> is the score if every one of them failed.
/// <see cref="UncoveredGaps"/> counts requirements not implemented with no open POA&amp;M item.
/// </summary>
public sealed record CmmcAssessmentDto(
    int Score,
    int WorstCaseScore,
    int Implemented,
    int NotImplemented,
    int NotApplicable,
    int NotAssessed,
    int UncoveredGaps,
    int OpenPoamItems,
    int OverduePoamItems,
    IReadOnlyList<CmmcFamilySummaryDto> Families,
    IReadOnlyList<CmmcPracticeDto> Practices,
    IReadOnlyList<CmmcPoamItemDto> Poam);

public sealed record SaveCmmcPracticeRequest(
    CmmcStatusDto Status,
    bool PartialCredit = false,
    string? Implementation = null,
    string? Evidence = null,
    string? OwnerEmail = null,
    DateOnly? AssessedOn = null);

public sealed record SaveCmmcPoamRequest(
    string PracticeId,
    string Milestone,
    string? OwnerEmail = null,
    DateOnly? DueOn = null,
    CmmcPoamStatusDto Status = CmmcPoamStatusDto.Open,
    string? Notes = null);

/// <summary>The organization's CMMC Level 2 (NIST SP 800-171) self-assessment: requirements, evidence notes, POA&amp;M and the SPRS score.</summary>
public interface ICmmcService
{
    Task<CmmcAssessmentDto> GetAssessmentAsync(CancellationToken cancellationToken = default);

    Task<CmmcPracticeDto> SavePracticeAsync(string practiceId, SaveCmmcPracticeRequest request, CancellationToken cancellationToken = default);

    Task<CmmcPoamItemDto> AddPoamItemAsync(SaveCmmcPoamRequest request, CancellationToken cancellationToken = default);

    Task<CmmcPoamItemDto> UpdatePoamItemAsync(Guid itemId, SaveCmmcPoamRequest request, CancellationToken cancellationToken = default);

    Task DeletePoamItemAsync(Guid itemId, CancellationToken cancellationToken = default);
}

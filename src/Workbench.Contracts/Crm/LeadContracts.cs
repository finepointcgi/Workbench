namespace Workbench.Contracts.Crm;

public enum LeadStatus { New, Accepted, Dismissed }

public sealed record SavedLeadSearchDto(
    Guid Id,
    string Name,
    IReadOnlyList<string> NaicsCodes,
    IReadOnlyList<string> Agencies,
    IReadOnlyList<SetAside> SetAsides,
    string? Keywords,
    int PostedWithinDays,
    bool IsActive,
    string OwnerEmail,
    DateTime? LastRunAtUtc,
    int LastRunNewLeads);

public sealed record SaveLeadSearchRequest(
    string Name,
    IReadOnlyList<string>? NaicsCodes = null,
    IReadOnlyList<string>? Agencies = null,
    IReadOnlyList<SetAside>? SetAsides = null,
    string? Keywords = null,
    int PostedWithinDays = 30,
    bool IsActive = true);

public sealed record OpportunityLeadDto(
    Guid Id,
    OpportunitySource Source,
    string? SourceId,
    string? SolicitationNumber,
    string Title,
    string? Agency,
    string? Office,
    string? NaicsCode,
    SetAside SetAside,
    string? NoticeType,
    DateOnly? PostedOn,
    DateOnly? ResponseDueOn,
    string? PlaceOfPerformance,
    string? Url,
    string? Description,
    decimal? EstimatedValue,
    Guid? SavedSearchId,
    string? SavedSearchName,
    LeadStatus Status,
    Guid? OpportunityId,
    string? ReviewedBy,
    DateTime CreatedAtUtc);

/// <summary>What a run or import did: new leads, and how many were skipped as already known (by solicitation number or notice id).</summary>
public sealed record LeadImportResultDto(int Found, int Created, int Duplicates, IReadOnlyList<string> Errors);

/// <summary>Accepting a lead creates an opportunity (Identified) from it; these override what the lead says.</summary>
public sealed record AcceptLeadRequest(string? Name = null, Guid? AgencyAccountId = null, string? CaptureManagerEmail = null, decimal? EstimatedValue = null);

/// <summary>
/// Sources of new opportunities: SAM.gov saved searches (the public Opportunities API, key in <c>SamGov:ApiKey</c>; a
/// built-in sample stands in without one) and CSV import. Results become leads for review, de-duplicated by solicitation
/// number (else SAM.gov notice id) against the pipeline and earlier leads. Business development and administrators run
/// and review; Finance reads.
/// </summary>
public interface IOpportunityLeadService
{
    Task<IReadOnlyList<SavedLeadSearchDto>> ListSearchesAsync(CancellationToken cancellationToken = default);

    Task<SavedLeadSearchDto> CreateSearchAsync(SaveLeadSearchRequest request, CancellationToken cancellationToken = default);

    Task<SavedLeadSearchDto> UpdateSearchAsync(Guid id, SaveLeadSearchRequest request, CancellationToken cancellationToken = default);

    Task DeleteSearchAsync(Guid id, CancellationToken cancellationToken = default);

    Task<LeadImportResultDto> RunSearchAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a CSV with a header row. Columns (any order, case-insensitive): Title (required), Solicitation Number, Notice
    /// Id, Agency, Office, NAICS, Set-Aside, Notice Type, Posted Date, Response Date, Place of Performance, URL, Description,
    /// Estimated Value.
    /// </summary>
    Task<LeadImportResultDto> ImportCsvAsync(string csv, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpportunityLeadDto>> ListLeadsAsync(LeadStatus? status = LeadStatus.New, CancellationToken cancellationToken = default);

    Task<OpportunityDto> AcceptAsync(Guid leadId, AcceptLeadRequest request, CancellationToken cancellationToken = default);

    Task<OpportunityLeadDto> DismissAsync(Guid leadId, CancellationToken cancellationToken = default);
}

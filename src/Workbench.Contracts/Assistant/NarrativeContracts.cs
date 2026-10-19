namespace Workbench.Contracts.Assistant;

/// <summary>The sections of a monthly PM narrative. Each is plain text.</summary>
public sealed record NarrativeSectionsDto(
    string? Summary,
    string? CostVariance,
    string? ScheduleVariance,
    string? EacChanges,
    string? Accomplishments,
    string? UpcomingMilestones,
    string? Risks)
{
    public static NarrativeSectionsDto Empty { get; } = new(null, null, null, null, null, null, null);
}

/// <summary>A fact the draft was built from, with the page that shows it.</summary>
public sealed record NarrativeFactDto(string Label, string Value, string? Href = null);

/// <summary>A suggested variance analysis for one control account (EVM). The PM chooses whether to copy it into the VAR.</summary>
public sealed record NarrativeVarianceDto(
    Guid ControlAccountWbsId,
    string Code,
    string Name,
    bool NeedsVar,
    string? Cause,
    string? Impact,
    string? CorrectiveAction,
    bool VarAlreadyWritten);

public sealed record ProjectNarrativeDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    Guid FiscalPeriodId,
    string PeriodName,
    NarrativeSectionsDto Sections,
    IReadOnlyList<NarrativeVarianceDto> Variances,
    IReadOnlyList<NarrativeFactDto> Facts,
    string DraftSource,
    bool IsSaved,
    string? UpdatedBy,
    DateTime? UpdatedAtUtc,

    /// <summary>True when the project has earned value and the period is open, so VARs can be updated from the narrative.</summary>
    bool CanApplyToVariances,
    IReadOnlyList<string> Notes);

public sealed record NarrativePeriodDto(Guid FiscalPeriodId, string Name, DateOnly Start, DateOnly End, bool HasNarrative, bool IsCurrent);

/// <summary>One control account's VAR text to copy into earned value when saving.</summary>
public sealed record ApplyNarrativeVarianceRequest(Guid ControlAccountWbsId, string? Cause, string? Impact, string? CorrectiveAction);

/// <summary>
/// Saves the PM's narrative for a period. <see cref="DraftSource"/> is "assistant", "template" or "manual".
/// <see cref="ApplyToVariances"/> writes those control accounts' VAR fields through the earned value service, as the user.
/// </summary>
public sealed record SaveProjectNarrativeRequest(
    Guid FiscalPeriodId,
    NarrativeSectionsDto Sections,
    string DraftSource = "manual",
    IReadOnlyList<ApplyNarrativeVarianceRequest>? ApplyToVariances = null);

/// <summary>
/// The monthly PM narrative: drafted from earned value, financials, the plan, ceilings and funding (read as the user),
/// then edited and saved by the PM. Finance, Admin or the project's manager.
/// </summary>
public interface IProjectNarrativeService
{
    /// <summary>Fiscal periods around today (the last twelve and the current one), newest first.</summary>
    Task<IReadOnlyList<NarrativePeriodDto>> ListPeriodsAsync(Guid projectId, CancellationToken cancellationToken = default);

    /// <summary>The saved narrative for the period (default: the reporting period), or null.</summary>
    Task<ProjectNarrativeDto?> GetAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>A fresh draft (not saved). With the assistant on, Claude writes it from the facts; otherwise a template does.</summary>
    Task<ProjectNarrativeDto> DraftAsync(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<ProjectNarrativeDto> SaveAsync(Guid projectId, SaveProjectNarrativeRequest request, CancellationToken cancellationToken = default);
}

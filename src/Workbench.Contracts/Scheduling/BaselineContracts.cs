namespace Workbench.Contracts.Scheduling;

public sealed record BaselineSummary(Guid Id, string Name, DateTime CapturedAtUtc, int EntryCount);

public sealed record BaselineEntryDto(Guid ItemId, DateOnly? Start, DateOnly? Finish);

public sealed record BaselineDetail(BaselineSummary Summary, IReadOnlyList<BaselineEntryDto> Entries);

/// <summary>A blank name defaults to "Baseline yyyy-MM-dd".</summary>
public sealed record CaptureBaselineRequest(string? Name);

public interface IBaselineService
{
    /// <summary>Snapshots the start and finish of every item in the sheet.</summary>
    Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default);

    /// <summary>Baselines for the sheet, newest first.</summary>
    Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Every task's dates against the baseline, with the variance in working days on the plan's calendar (Viewer).
    /// Summary rows span their subtasks' baseline dates.
    /// </summary>
    Task<BaselineComparison> CompareAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default);

    /// <summary>Removes a saved baseline (Editor).</summary>
    Task DeleteAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default);
}

/// <summary>
/// One task against a baseline. Variances are working days on the plan's calendar: positive is later than the baseline
/// (a slip), negative earlier. Null where either side has no date; a task added after the baseline has no baseline dates.
/// </summary>
public sealed record BaselineVarianceDto(
    Guid ItemId,
    DateOnly? Start,
    DateOnly? Finish,
    DateOnly? BaselineStart,
    DateOnly? BaselineFinish,
    int? StartVarianceDays,
    int? FinishVarianceDays,
    int? DurationVarianceDays)
{
    public bool InBaseline => BaselineStart is not null || BaselineFinish is not null;
}

/// <summary>A plan against one baseline, in outline order, and how far the plan's finish has moved.</summary>
public sealed record BaselineComparison(BaselineSummary Baseline, IReadOnlyList<BaselineVarianceDto> Tasks, int? FinishVarianceDays)
{
    public BaselineVarianceDto? For(Guid itemId) => Tasks.FirstOrDefault(t => t.ItemId == itemId);
}

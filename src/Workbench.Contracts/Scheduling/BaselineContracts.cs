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
}

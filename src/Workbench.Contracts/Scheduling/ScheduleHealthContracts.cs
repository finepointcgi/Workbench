namespace Workbench.Contracts.Scheduling;

// The DCMA 14-point schedule assessment of a plan: the checks DoD primes and DCMA analysts run on an Integrated Master
// Schedule. Each check is measured on the plan's detail tasks (summary rows only group them) against DCMA's threshold.

public enum HealthCheckStatus
{
    Pass,
    Fail,

    /// <summary>The check can't be measured on this plan (no baseline, nothing due yet, no remaining work).</summary>
    NotApplicable,
}

/// <summary>A task a check flags. <paramref name="Detail"/> says why, e.g. "No predecessor" or "62 working days of float".</summary>
public sealed record HealthTaskDto(Guid ItemId, int TaskNumber, string OutlineNumber, string Title, string Detail);

/// <summary>
/// One of the 14 checks. <paramref name="Number"/> is DCMA's numbering (1 Logic … 14 BEI). <paramref name="Explanation"/> is
/// a one-line plain explanation; <paramref name="Threshold"/> DCMA's target as text ("5% or less"). <paramref name="Count"/>
/// of <paramref name="Total"/> are flagged (<paramref name="Percent"/> = Count / Total × 100); index checks (CPLI, BEI) put
/// their value in <paramref name="Value"/>. <paramref name="Result"/> is the outcome in words.
/// </summary>
public sealed record HealthCheckDto(
    int Number,
    string Key,
    string Name,
    string Explanation,
    string Threshold,
    HealthCheckStatus Status,
    int Count,
    int Total,
    decimal? Percent,
    decimal? Value,
    string Result,
    IReadOnlyList<HealthTaskDto> Tasks);

/// <summary>
/// The assessment of a plan on <paramref name="StatusDate"/>. <paramref name="BaselineName"/> is the baseline the missed-task
/// and BEI checks used (null: the plan has none). <paramref name="Notes"/> are caveats, e.g. that the plan has no % Complete
/// column so every task counts as not started.
/// </summary>
public sealed record ScheduleHealthDto(
    Guid SheetId,
    string PlanName,
    DateOnly StatusDate,
    Guid? BaselineId,
    string? BaselineName,
    int DetailTasks,
    int IncompleteTasks,
    int Links,
    IReadOnlyList<HealthCheckDto> Checks,
    IReadOnlyList<string> Notes)
{
    public int Passed => Checks.Count(c => c.Status == HealthCheckStatus.Pass);

    public int Failed => Checks.Count(c => c.Status == HealthCheckStatus.Fail);

    public HealthCheckDto? Check(string key) => Checks.FirstOrDefault(c => c.Key == key);
}

/// <summary>
/// What to assess against: <paramref name="StatusDate"/> (default today) and <paramref name="BaselineId"/> (default the newest
/// baseline).
/// </summary>
public sealed record ScheduleHealthRequest(DateOnly? StatusDate = null, Guid? BaselineId = null);

// TaskFloatDto (one task's total and free float) is in PlanFloatContracts.cs, shared with the plan grid's float columns.

public interface IScheduleHealthService
{
    /// <summary>Runs the DCMA 14-point assessment on a scheduled sheet's plan (Viewer).</summary>
    Task<ScheduleHealthDto> GetAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default);

    /// <summary>Total and free float of every dated detail task (Viewer).</summary>
    Task<IReadOnlyList<TaskFloatDto>> GetFloatAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>The assessment as a PDF report (Viewer).</summary>
    Task<PlanFile> ExportPdfAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default);

    /// <summary>The assessment as an .xlsx workbook: a summary sheet and one row per flagged task (Viewer).</summary>
    Task<PlanFile> ExportExcelAsync(Guid sheetId, ScheduleHealthRequest? request = null, CancellationToken cancellationToken = default);
}

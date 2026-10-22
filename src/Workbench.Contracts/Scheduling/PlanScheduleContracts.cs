using System.Globalization;

namespace Workbench.Contracts.Scheduling;

// The MS Project-style plan editor on a scheduled sheet: outline numbers, durations in working days, predecessors typed as
// task IDs, summary rollup, indent/outdent with date rules, and Excel export and import. New records next to the sheet
// contracts rather than new fields on ItemDto (CONVENTIONS: don't change Contracts/Sheets).

/// <summary>
/// One task as the plan editor shows it. <paramref name="TaskNumber"/> is MS Project's "ID": the 1-based position in the
/// whole outline (depth first), which predecessors refer to. <paramref name="OutlineNumber"/> is "1.2.3".
/// <paramref name="DurationDays"/> is in working days (Mon–Fri less company holidays): 0 for a milestone, null without dates.
/// <paramref name="Predecessors"/> is the MS Project text, e.g. <c>3, 5SS+2d</c>. <paramref name="PercentComplete"/> is the
/// sheet's % Complete column (rolled up, duration-weighted, on summary tasks), null when the sheet has none.
/// </summary>
public sealed record PlanTaskDto(
    Guid ItemId,
    int TaskNumber,
    string OutlineNumber,
    int OutlineLevel,
    bool IsSummary,
    int? DurationDays,
    string Predecessors,
    decimal? PercentComplete);

/// <summary>The plan of a scheduled sheet, in outline order. <paramref name="Holidays"/> are the company holidays it skips.</summary>
public sealed record PlanScheduleDto(Guid SheetId, IReadOnlyList<PlanTaskDto> Tasks, IReadOnlyList<DateOnly> Holidays)
{
    public PlanTaskDto? For(Guid itemId) => Tasks.FirstOrDefault(t => t.ItemId == itemId);
}

/// <summary>A downloadable workbook.</summary>
public sealed record PlanFile(string FileName, string ContentType, byte[] Content);

/// <summary>An uploaded .xlsx (or .csv) schedule.</summary>
public sealed record PlanImportRequest(string FileName, byte[] Content);

public enum PlanImportChangeKind
{
    Added,
    Changed,
    Removed,
}

/// <summary>One task the import adds, changes or removes. <paramref name="Row"/> is the row in the file (null when removed).</summary>
public sealed record PlanImportChange(PlanImportChangeKind Kind, int? Row, Guid? ItemId, string Title, IReadOnlyList<string> Details);

/// <summary>A problem with one row of the file. Errors block the import; warnings don't.</summary>
public sealed record PlanImportIssue(int Row, string Message, bool IsError = true);

/// <summary>
/// What an import would do. Nothing is applied until <see cref="IPlanScheduleService.ApplyImportAsync"/>, which refuses a
/// file with errors. <paramref name="Unchanged"/> counts the rows that match the plan already.
/// </summary>
public sealed record PlanImportPreview(
    string FileName,
    int Rows,
    int Unchanged,
    IReadOnlyList<PlanImportChange> Changes,
    IReadOnlyList<PlanImportIssue> Issues)
{
    public bool HasErrors => Issues.Any(i => i.IsError);

    public int Count(PlanImportChangeKind kind) => Changes.Count(c => c.Kind == kind);
}

public interface IPlanScheduleService
{
    /// <summary>Outline numbers, durations, predecessors and % complete for every task (Viewer).</summary>
    Task<PlanScheduleDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Makes the task the last child of the task above it at the same level (Editor). On a scheduled sheet it moves to
    /// start with its new previous sibling (or, as a first child, with its new parent), keeping its duration and its
    /// subtasks' offsets; the parent becomes a summary task. Predecessors can still push it later.
    /// </summary>
    Task IndentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Moves the task up one level, keeping its place in the outline (Editor): the tasks below it under the same parent
    /// become its subtasks, as in MS Project. It keeps its dates.
    /// </summary>
    Task OutdentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default);

    /// <summary>Sets the duration in working days from the task's start (Editor). 0 makes it a milestone.</summary>
    Task SetDurationAsync(Guid sheetId, Guid itemId, int days, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the task's predecessors with MS Project text: task IDs, each optionally followed by FS/SS/FF/SF and a lag in
    /// working days, e.g. <c>3, 5SS+2d</c> (Editor). Successors are pushed so they start no earlier than the links allow.
    /// </summary>
    Task SetPredecessorsAsync(Guid sheetId, Guid itemId, string? predecessors, CancellationToken cancellationToken = default);

    /// <summary>The plan as an .xlsx workbook, indented by outline level (Viewer).</summary>
    Task<PlanFile> ExportAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>A blank workbook with the import columns and instructions.</summary>
    Task<PlanFile> TemplateAsync(CancellationToken cancellationToken = default);

    /// <summary>Validates a file and lists what importing it would add, change and remove (Editor). Changes nothing.</summary>
    Task<PlanImportPreview> PreviewImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Replaces the plan with the file (Editor): its rows are the whole plan, in order, so tasks missing from the file are
    /// removed. Throws <see cref="ArgumentException"/> when the file has errors.
    /// </summary>
    Task<PlanImportPreview> ApplyImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Durations as PMs type them, shared by the grid and the Excel import.</summary>
public static class PlanDuration
{
    private static readonly (string Suffix, decimal Factor)[] Units =
        [("weeks", 5m), ("week", 5m), ("wks", 5m), ("wk", 5m), ("w", 5m), ("days", 1m), ("day", 1m), ("dys", 1m), ("dy", 1m), ("d", 1m)];

    /// <summary>
    /// Parses working days: <c>5</c>, <c>5d</c>, <c>5 days</c>, <c>2w</c> (5 working days a week), <c>0</c> for a milestone.
    /// MS Project's "?" (estimated) is ignored.
    /// </summary>
    public static bool TryParse(string? text, out int days)
    {
        days = 0;
        var t = (text ?? "").Trim().TrimEnd('?').Trim().ToLowerInvariant();
        if (t.Length == 0)
        {
            return false;
        }

        var factor = 1m;
        foreach (var (suffix, f) in Units)
        {
            if (t.EndsWith(suffix, StringComparison.Ordinal))
            {
                t = t[..^suffix.Length].Trim();
                factor = f;
                break;
            }
        }

        if (!decimal.TryParse(t, NumberStyles.Number, CultureInfo.InvariantCulture, out var value) || value < 0)
        {
            return false;
        }

        var total = value * factor;
        if (total != Math.Floor(total) || total > 3650)
        {
            return false;
        }

        days = (int)total;
        return true;
    }

    /// <summary><c>0 days</c>, <c>1 day</c>, <c>5 days</c>.</summary>
    public static string Format(int days) => days == 1 ? "1 day" : $"{days.ToString(CultureInfo.InvariantCulture)} days";
}

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
    decimal? PercentComplete,
    PlanConstraintDto? Constraint = null);

/// <summary>
/// The plan of a scheduled sheet, in outline order. <paramref name="Holidays"/> are the days off it skips (company holidays,
/// or the plan's own calendar); <paramref name="WorkWeek"/> its working weekdays (null: Monday to Friday) and
/// <paramref name="ExtraWorkingDays"/> days off it works anyway. <paramref name="Conflicts"/> lists every task whose date
/// constraint and links disagree, so they are shown rather than silently resolved.
/// </summary>
public sealed record PlanScheduleDto(
    Guid SheetId,
    IReadOnlyList<PlanTaskDto> Tasks,
    IReadOnlyList<DateOnly> Holidays,
    IReadOnlyList<PlanConflictDto>? Conflicts = null,
    IReadOnlyList<DayOfWeek>? WorkWeek = null,
    IReadOnlyList<DateOnly>? ExtraWorkingDays = null)
{
    public PlanTaskDto? For(Guid itemId) => Tasks.FirstOrDefault(t => t.ItemId == itemId);

    public IReadOnlyList<PlanConflictDto> AllConflicts => Conflicts ?? [];

    /// <summary>True when the plan works on <paramref name="date"/>.</summary>
    public bool IsWorkingDay(DateOnly date) =>
        (ExtraWorkingDays?.Contains(date) ?? false)
        || ((WorkWeek ?? PlanCalendarDto.MondayToFriday).Contains(date.DayOfWeek) && !Holidays.Contains(date));
}

/// <summary>MS Project's date constraints, numbered as in its XML. A task without one is As Soon As Possible.</summary>
public enum PlanConstraintKind
{
    AsSoonAsPossible = 0,

    /// <summary>Read from MS Project files only; Workbench schedules it as soon as possible.</summary>
    AsLateAsPossible = 1,
    MustStartOn = 2,
    MustFinishOn = 3,
    StartNoEarlierThan = 4,
    StartNoLaterThan = 5,
    FinishNoEarlierThan = 6,
    FinishNoLaterThan = 7,
}

/// <summary>A task's date constraint.</summary>
public sealed record PlanConstraintDto(PlanConstraintKind Kind, DateOnly Date)
{
    /// <summary>"Start No Earlier Than", as MS Project names it.</summary>
    public string Label => PlanConstraints.Label(Kind);
}

/// <summary>Sets (or with As Soon As Possible, clears) a task's constraint. The date is required for the other kinds.</summary>
public sealed record SetPlanConstraintRequest(PlanConstraintKind Kind, DateOnly? Date);

/// <summary>
/// A task whose constraint can't be met: a Must Start/Finish On its predecessors would push later, or a Start/Finish No
/// Later Than its links (or duration) have pushed past. <paramref name="Days"/> is how many working days it's off by;
/// <paramref name="Message"/> says it plainly, naming the driving predecessor when there is one.
/// </summary>
public sealed record PlanConflictDto(Guid ItemId, int TaskNumber, string Title, PlanConstraintKind Kind, int Days, string Message, Guid? PredecessorId = null);

public static class PlanConstraints
{
    public static string Label(PlanConstraintKind kind) => kind switch
    {
        PlanConstraintKind.AsLateAsPossible => "As Late As Possible",
        PlanConstraintKind.MustStartOn => "Must Start On",
        PlanConstraintKind.MustFinishOn => "Must Finish On",
        PlanConstraintKind.StartNoEarlierThan => "Start No Earlier Than",
        PlanConstraintKind.StartNoLaterThan => "Start No Later Than",
        PlanConstraintKind.FinishNoEarlierThan => "Finish No Earlier Than",
        PlanConstraintKind.FinishNoLaterThan => "Finish No Later Than",
        _ => "As Soon As Possible",
    };

    /// <summary>A short explanation for pickers.</summary>
    public static string Help(PlanConstraintKind kind) => kind switch
    {
        PlanConstraintKind.MustStartOn => "Pinned to start on the date. Predecessors can't move it; if they'd push it later, that's shown as a conflict.",
        PlanConstraintKind.MustFinishOn => "Pinned to finish on the date. Predecessors can't move it; if they'd push it later, that's shown as a conflict.",
        PlanConstraintKind.StartNoEarlierThan => "Starts on the date or later (predecessors can still push it later).",
        PlanConstraintKind.StartNoLaterThan => "A deadline for the start. If predecessors push it past the date, that's shown as a conflict.",
        PlanConstraintKind.FinishNoEarlierThan => "Finishes on the date or later.",
        PlanConstraintKind.FinishNoLaterThan => "A deadline. If predecessors push the finish past the date, that's shown as a conflict.",
        _ => "No date constraint: starts when its predecessors allow.",
    };

    /// <summary>The kinds people can choose (As Late As Possible is only read from files).</summary>
    public static IReadOnlyList<PlanConstraintKind> Choices { get; } =
    [
        PlanConstraintKind.AsSoonAsPossible, PlanConstraintKind.StartNoEarlierThan, PlanConstraintKind.FinishNoLaterThan,
        PlanConstraintKind.MustStartOn, PlanConstraintKind.MustFinishOn, PlanConstraintKind.StartNoLaterThan, PlanConstraintKind.FinishNoEarlierThan,
    ];
}

/// <summary>A day the plan's calendar treats differently: a holiday or day off, or (IsWorking) a working weekend day.</summary>
public sealed record PlanCalendarDayDto(DateOnly Date, string Name, bool IsWorking = false);

/// <summary>
/// The working calendar of a plan. <paramref name="IsCustom"/> is false while the plan follows the company calendar (Monday
/// to Friday less <paramref name="CompanyHolidays"/>). A custom calendar sets its own working weekdays, whether company
/// holidays count, and its own days off or working days.
/// </summary>
public sealed record PlanCalendarDto(
    bool IsCustom,
    IReadOnlyList<DayOfWeek> WorkWeek,
    bool UseCompanyHolidays,
    IReadOnlyList<PlanCalendarDayDto> Exceptions,
    IReadOnlyList<PlanCalendarDayDto> CompanyHolidays)
{
    public static IReadOnlyList<DayOfWeek> MondayToFriday { get; } =
        [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
}

/// <summary>Gives the plan its own calendar. Durations are kept: finish dates move to fit the new working days.</summary>
public sealed record SetPlanCalendarRequest(IReadOnlyList<DayOfWeek> WorkWeek, bool UseCompanyHolidays, IReadOnlyList<PlanCalendarDayDto> Exceptions);

/// <summary>
/// What a Gantt PDF shows. <paramref name="From"/>/<paramref name="To"/> limit the dates (null: the whole plan);
/// <paramref name="FitToPage"/> scales the timeline to one page wide; <paramref name="IncludeTable"/> prints the task table
/// (ID, name, duration, start, finish) beside the bars; <paramref name="BaselineId"/> draws that baseline under the bars.
/// </summary>
public sealed record GanttPdfRequest(
    DateOnly? From = null,
    DateOnly? To = null,
    bool FitToPage = true,
    bool IncludeTable = true,
    GanttPaperSize Paper = GanttPaperSize.Letter,
    Guid? BaselineId = null);

public enum GanttPaperSize
{
    /// <summary>8.5 × 11 in, landscape.</summary>
    Letter,

    /// <summary>11 × 17 in, landscape.</summary>
    Tabloid,

    /// <summary>A4, landscape.</summary>
    A4,
}

/// <summary>A downloadable workbook.</summary>
public sealed record PlanFile(string FileName, string ContentType, byte[] Content);

/// <summary>An uploaded schedule: .xlsx or .csv, or MS Project XML (.xml).</summary>
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

    /// <summary>
    /// Sets the task's date constraint (Editor); As Soon As Possible clears it. The plan is rescheduled; conflicts with
    /// predecessors are returned (and listed in <see cref="PlanScheduleDto.Conflicts"/>), never resolved silently.
    /// </summary>
    Task<IReadOnlyList<PlanConflictDto>> SetConstraintAsync(Guid sheetId, Guid itemId, SetPlanConstraintRequest request, CancellationToken cancellationToken = default);

    /// <summary>The plan's working calendar (Viewer).</summary>
    Task<PlanCalendarDto> GetCalendarAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Gives the plan its own calendar, or with null goes back to the company calendar (Editor). Durations stay the same in
    /// working days, so finish dates move to fit.
    /// </summary>
    Task<PlanCalendarDto> SetCalendarAsync(Guid sheetId, SetPlanCalendarRequest? request, CancellationToken cancellationToken = default);

    /// <summary>
    /// The plan as MS Project XML (Viewer): the .xml format MS Project opens and saves (File › Save As › XML). Workbench
    /// doesn't write .mpp, which is MS Project's own closed format.
    /// </summary>
    Task<PlanFile> ExportMsProjectAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>The Gantt chart as a PDF (Viewer).</summary>
    Task<PlanFile> ExportGanttPdfAsync(Guid sheetId, GanttPdfRequest request, CancellationToken cancellationToken = default);
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

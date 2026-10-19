using Workbench.Contracts.Time;

namespace Workbench.Contracts.Assistant;

/// <summary>The deterministic timesheet checks. Flags inform reviewers; they never block submitting or approving.</summary>
public static class TimesheetFlagRules
{
    public const string HistoryPattern = "pattern.history";
    public const string AllocationPattern = "pattern.allocation";
    public const string Weekend = "calendar.weekend";
    public const string Holiday = "calendar.holiday";
    public const string RoundNumbers = "pattern.round-numbers";
    public const string LateEntry = "timing.late-entry";
    public const string Revisions = "timing.revisions";
    public const string ClosedCode = "code.closed";
    public const string CeilingLimited = "code.ceiling";
    public const string IndirectMix = "mix.indirect-direct";

    public static readonly IReadOnlyList<string> All =
        [HistoryPattern, AllocationPattern, Weekend, Holiday, RoundNumbers, LateEntry, Revisions, ClosedCode, CeilingLimited, IndirectMix];

    public static string Label(string rule) => rule switch
    {
        HistoryPattern => "Differs from usual charging",
        AllocationPattern => "Differs from allocations",
        Weekend => "Weekend hours",
        Holiday => "Hours on a holiday",
        RoundNumbers => "Round-number hours",
        LateEntry => "Entered late",
        Revisions => "Revised after submitting",
        ClosedCode => "Closed charge code",
        CeilingLimited => "Ceiling-limited charge code",
        IndirectMix => "Indirect/direct mix changed",
        _ => rule,
    };
}

public enum TimesheetFlagSeverity
{
    Info,
    Warning,
}

public sealed record TimesheetFlagFactDto(string Label, string Value);

public sealed record TimesheetFlagDto(
    string RuleKey,
    string Title,
    TimesheetFlagSeverity Severity,
    string Detail,
    IReadOnlyList<TimesheetFlagFactDto> Facts,
    IReadOnlyList<DateOnly> Dates,
    string FactsHash);

public sealed record TimesheetFlagsDto(
    Guid TimesheetId,
    Guid EmployeeId,
    string EmployeeName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    TimesheetState Status,
    decimal TotalHours,
    string Link,
    IReadOnlyList<TimesheetFlagDto> Flags);

public sealed record TimesheetFlagReportQuery(DateOnly From, DateOnly To, string? RuleKey = null);

public sealed record TimesheetFlagExplanationDto(Guid TimesheetId, string RuleKey, string Text, bool FromAssistant);

/// <summary>
/// Odd-timesheet flags for supervisors, timekeepers and Finance: rules plus a statistical baseline of each person's own
/// history, computed deterministically. The assistant only explains a flag in plain language. Flags never block anything.
/// Visible for the timesheets of the user's direct reports (Timekeeper, Finance and Admin: everyone's).
/// </summary>
public interface ITimesheetFlagService
{
    /// <summary>Flags for the timesheets the user may review; others are left out (no error).</summary>
    Task<IReadOnlyList<TimesheetFlagsDto>> ListForTimesheetsAsync(IReadOnlyCollection<Guid> timesheetIds, CancellationToken cancellationToken = default);

    Task<TimesheetFlagsDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default);

    /// <summary>Flagged timesheets whose period overlaps [from, to], most flags first.</summary>
    Task<IReadOnlyList<TimesheetFlagsDto>> ReportAsync(TimesheetFlagReportQuery query, CancellationToken cancellationToken = default);

    /// <summary>A plain-language explanation of one flag (from the assistant when it's on; otherwise the rule's own text).</summary>
    Task<TimesheetFlagExplanationDto> ExplainAsync(Guid timesheetId, string ruleKey, CancellationToken cancellationToken = default);
}

public sealed record TimesheetSubmissionRowDto(
    Guid EmployeeId,
    string EmployeeName,
    string? SupervisorName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    Guid? TimesheetId,
    TimesheetState? Status,
    decimal Hours,
    decimal StandardHours,
    bool IsMissing);

/// <summary>Who has (and hasn't) submitted a timesheet for a period: direct reports, or everyone for Timekeeper/Finance/Admin.</summary>
public interface ITimesheetSubmissionService
{
    Task<IReadOnlyList<TimesheetSubmissionRowDto>> GetStatusAsync(DateOnly date, CancellationToken cancellationToken = default);
}

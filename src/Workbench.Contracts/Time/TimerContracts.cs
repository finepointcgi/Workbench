using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Time;

// A start/stop timer that fills the signed-in employee's timesheet. One per person, kept on the server, so it survives
// reloads and shows on every device. Stopping it adds the elapsed time (rounded to the company's increment) to that day's
// cell for the charge code, on top of any hours already there, following the same rules as typing them.

/// <summary>
/// The running timer. <paramref name="StartDate"/> is the local day it started; <paramref name="ElapsedHours"/> the time so
/// far and <paramref name="RoundedHours"/> what stopping now would add. <paramref name="CrossesMidnight"/>: it started on an
/// earlier day, so stopping asks how to split it.
/// </summary>
public sealed record RunningTimerDto(
    Guid Id,
    ChargeCodeDto ChargeCode,
    DateTime StartedAtUtc,
    DateOnly StartDate,
    string? Note,
    decimal ElapsedHours,
    decimal RoundedHours,
    bool CrossesMidnight);

/// <summary>A code the timer can run on today. <paramref name="Staffed"/>: the person is staffed on it this week.</summary>
public sealed record TimerChargeCodeDto(ChargeCodeDto ChargeCode, bool Staffed, bool RecentlyUsed);

/// <summary>
/// Everything the timer needs to show: the running timer (if any), the codes it can start on today with
/// <paramref name="DefaultWbsElementId"/> (the last code charged, else one the person is staffed on), the rounding
/// increment in hours, and whether today's timesheet is submitted or approved (stopping will ask to recall it).
/// </summary>
public sealed record TimerStateDto(
    RunningTimerDto? Running,
    IReadOnlyList<TimerChargeCodeDto> ChargeCodes,
    Guid? DefaultWbsElementId,
    decimal IncrementHours,
    bool TodayIsLocked);

public sealed record StartTimerRequest(Guid WbsElementId, string? Note = null);

public sealed record TimerNoteRequest(string? Note);

/// <summary>Hours to put on one day when a timer that ran over midnight is split.</summary>
public sealed record TimerDayHours(DateOnly Date, decimal Hours);

/// <summary>
/// Stops the timer. <paramref name="Note"/> replaces the timer's note (added to the cell's comment);
/// <paramref name="WbsElementId"/> charges another code instead; <paramref name="Split"/> says how to split a timer that ran
/// over midnight (required then). <paramref name="Reason"/> is recorded with the change when the timesheet was submitted
/// before (a default is written otherwise).
/// </summary>
public sealed record StopTimerRequest(string? Note = null, IReadOnlyList<TimerDayHours>? Split = null, Guid? WbsElementId = null, string? Reason = null);

public enum TimerStopOutcome
{
    /// <summary>The hours were added and the timer is gone.</summary>
    Added,

    /// <summary>The timer ran over midnight: nothing was added, <see cref="StopTimerResult.Days"/> suggests a split; stop again with one.</summary>
    NeedsSplit,

    /// <summary>
    /// A day's timesheet is submitted or approved: nothing was added and the timer keeps running. Recall (or reopen) the
    /// timesheet with a reason, then stop again.
    /// </summary>
    NeedsRecall,

    /// <summary>Less than half an increment: the timer is gone and nothing was added.</summary>
    TooShort,
}

/// <summary>
/// What stopping did. <paramref name="Days"/>: the hours added per day (or, for <see cref="TimerStopOutcome.NeedsSplit"/>,
/// the suggested split by actual time on each day). <paramref name="TimesheetId"/>/<paramref name="TimesheetStatus"/>
/// name the locked timesheet for <see cref="TimerStopOutcome.NeedsRecall"/>.
/// </summary>
public sealed record StopTimerResult(
    TimerStopOutcome Outcome,
    IReadOnlyList<TimerDayHours> Days,
    string Message,
    Guid? TimesheetId = null,
    TimesheetState? TimesheetStatus = null);

/// <summary>The signed-in employee's own timer.</summary>
public interface ITimesheetTimerService
{
    Task<TimerStateDto> GetAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a timer on a code the person can charge today (an open period). Throws <see cref="ArgumentException"/> when a
    /// timer is already running: one per person.
    /// </summary>
    Task<RunningTimerDto> StartAsync(StartTimerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes the running timer's note.</summary>
    Task<RunningTimerDto> SetNoteAsync(string? note, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops the timer and adds the elapsed time, rounded to <see cref="TimerStateDto.IncrementHours"/>, to the day's cell for
    /// the code: on top of the hours already there, with the note appended to the cell's comment.
    /// </summary>
    Task<StopTimerResult> StopAsync(StopTimerRequest request, CancellationToken cancellationToken = default);

    /// <summary>Throws the running timer away without adding anything.</summary>
    Task DiscardAsync(CancellationToken cancellationToken = default);
}

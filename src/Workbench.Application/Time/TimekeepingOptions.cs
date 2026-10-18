using Microsoft.Extensions.Options;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>Configuration section <c>Timekeeping</c>.</summary>
public sealed class TimekeepingOptions
{
    /// <summary>
    /// The timesheet period until an Admin saves the organization's setting on <c>/setup/organization</c> (stored in the
    /// tenant database, which then wins). <c>Weekly</c> (the default), <c>Biweekly</c>, <c>SemiMonthly</c> or <c>Monthly</c>.
    /// </summary>
    public TimesheetCadence Cadence { get; set; } = TimesheetCadence.Weekly;

    /// <summary>Default first day of a weekly period (Monday).</summary>
    public DayOfWeek WeekStartDay { get; set; } = DayOfWeek.Monday;

    /// <summary>Default biweekly anchor (the first day of any period).</summary>
    public DateOnly? BiweeklyAnchor { get; set; }

    public TimesheetPeriodSettings DefaultPeriodSettings =>
        new(Cadence, WeekStartDay, Cadence == TimesheetCadence.Biweekly ? BiweeklyAnchor ?? new DateOnly(2026, 1, 5) : BiweeklyAnchor);

    /// <summary>
    /// IANA or Windows time zone id that decides what "today" is for future-date and late-entry checks.
    /// Defaults to the server's local time zone.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>A day's hours are late when first entered more than this many business days after the work date.</summary>
    public int LateEntryBusinessDays { get; set; } = 1;

    /// <summary>
    /// Local hour (0–23, in <see cref="TimeZone"/>) from which the daily "no hours entered for the previous business day"
    /// reminder goes out.
    /// </summary>
    public int MissingTimeReminderHour { get; set; } = 9;
}

/// <summary>Today's date in the configured time zone, and the derived timesheet periods (periods are never stored).</summary>
public sealed class TimekeepingClock(TimeProvider clock, IOptions<TimekeepingOptions> options)
{
    private readonly TimeZoneInfo _zone = string.IsNullOrWhiteSpace(options.Value.TimeZone)
        ? TimeZoneInfo.Local
        : TimeZoneInfo.FindSystemTimeZoneById(options.Value.TimeZone);

    public TimekeepingOptions Options => options.Value;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public TimeProvider Provider => clock;

    public DateOnly Today => LocalDate(UtcNow);

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(LocalTime(utc));

    public DateTime LocalTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    /// <summary>The timesheet period containing <paramref name="date"/> under the organization's setting (tenant database).</summary>
    public async Task<(DateOnly Start, DateOnly End)> PeriodForAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken cancellationToken = default) =>
        TimesheetPeriods.For(date, await TimesheetPeriods.LoadScheduleAsync(db, options.Value.DefaultPeriodSettings, cancellationToken));

    /// <summary>The period containing <paramref name="date"/> for one cadence with default options (weekly periods start Monday).</summary>
    public static (DateOnly Start, DateOnly End) PeriodFor(DateOnly date, TimesheetCadence cadence) =>
        TimesheetPeriods.Natural(date, new TimekeepingOptions { Cadence = cadence }.DefaultPeriodSettings);

    /// <summary>
    /// Standard hours expected for the period: the weekly standard spread over the weekdays the employee was employed.
    /// Used for the DCAA total-time-accounting warning on submit.
    /// </summary>
    public static decimal StandardHours(Employee employee, DateOnly start, DateOnly end)
    {
        var weekdays = 0;
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            if (IsBusinessDay(d) && employee.IsActiveOn(d))
            {
                weekdays++;
            }
        }

        return decimal.Round(employee.StandardHoursPerWeek / 5m * weekdays, 2);
    }

    public static bool IsBusinessDay(DateOnly date) => date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    /// <summary>Business days after <paramref name="workDate"/> up to and including <paramref name="enteredOn"/> (0 when entered the same day or earlier).</summary>
    public static int BusinessDaysAfter(DateOnly workDate, DateOnly enteredOn)
    {
        var count = 0;
        for (var d = workDate.AddDays(1); d <= enteredOn; d = d.AddDays(1))
        {
            if (IsBusinessDay(d))
            {
                count++;
            }
        }

        return count;
    }

    public bool IsLate(DateOnly workDate, DateTime firstEnteredAtUtc) =>
        BusinessDaysAfter(workDate, LocalDate(firstEnteredAtUtc)) > options.Value.LateEntryBusinessDays;
}

public enum TimesheetEvent { Submitted, Approved, Rejected, Reopened }

/// <summary>
/// A timesheet event worth telling someone about. <see cref="Recipients"/> are already worked out by the service
/// (the approvers on submit, the employee on approve or reject, both on reopen) and never include the actor.
/// </summary>
public sealed record TimesheetNotification(
    TimesheetEvent Event,
    Guid TimesheetId,
    string EmployeeName,
    string EmployeeEmail,
    IReadOnlyList<string> Recipients,
    DateOnly PeriodStart,
    decimal Hours,
    string ActorName,
    string? Reason);

/// <summary>Sends timesheet notifications. The registered implementation is <see cref="PublishingTimesheetNotifier"/>.</summary>
public interface ITimesheetNotifier
{
    Task NotifyAsync(TimesheetNotification notification, CancellationToken cancellationToken = default);
}

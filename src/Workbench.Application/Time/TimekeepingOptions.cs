using Microsoft.Extensions.Options;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>Configuration section <c>Timekeeping</c>.</summary>
public sealed class TimekeepingOptions
{
    /// <summary><c>Weekly</c> (Monday–Sunday, the default) or <c>SemiMonthly</c> (1st–15th, 16th–end of month).</summary>
    public TimesheetCadence Cadence { get; set; } = TimesheetCadence.Weekly;

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

    public DateOnly Today => LocalDate(UtcNow);

    public DateOnly LocalDate(DateTime utc) => DateOnly.FromDateTime(LocalTime(utc));

    public DateTime LocalTime(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _zone);

    public (DateOnly Start, DateOnly End) PeriodFor(DateOnly date) => PeriodFor(date, options.Value.Cadence);

    public static (DateOnly Start, DateOnly End) PeriodFor(DateOnly date, TimesheetCadence cadence)
    {
        if (cadence == TimesheetCadence.SemiMonthly)
        {
            return date.Day <= 15
                ? (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 15))
                : (new DateOnly(date.Year, date.Month, 16), new DateOnly(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month)));
        }

        var start = date.AddDays(-(((int)date.DayOfWeek + 6) % 7));
        return (start, start.AddDays(6));
    }

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

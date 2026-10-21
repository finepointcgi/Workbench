namespace Workbench.Application.Scheduling;

/// <summary>
/// The plan's working calendar: Monday to Friday, less company holidays. Durations count working days inclusive, so a task
/// from Monday to Friday is 5 days; a milestone is 0. A non-working start counts from the next working day.
/// </summary>
public sealed class WorkCalendar(IEnumerable<DateOnly>? holidays = null)
{
    private readonly HashSet<DateOnly> _holidays = (holidays ?? []).ToHashSet();

    public static WorkCalendar Weekdays { get; } = new();

    public IReadOnlyCollection<DateOnly> Holidays => _holidays;

    public bool IsWorkingDay(DateOnly date) =>
        date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday) && !_holidays.Contains(date);

    /// <summary>The date itself when it's a working day, else the next working day.</summary>
    public DateOnly OnOrAfter(DateOnly date)
    {
        while (!IsWorkingDay(date))
        {
            date = date.AddDays(1);
        }

        return date;
    }

    /// <summary>Moves <paramref name="days"/> working days from a working day (negative moves back).</summary>
    public DateOnly AddWorkingDays(DateOnly date, int days)
    {
        var step = Math.Sign(days);
        for (var left = Math.Abs(days); left > 0;)
        {
            date = date.AddDays(step);
            if (IsWorkingDay(date))
            {
                left--;
            }
        }

        return date;
    }

    /// <summary>Working days from start to finish inclusive; at least 1 for a task (a weekend-only task is a day), 0 for a milestone.</summary>
    public int Duration(DateOnly start, DateOnly finish, bool isMilestone = false)
    {
        if (isMilestone)
        {
            return 0;
        }

        var days = 0;
        for (var d = start; d <= finish; d = d.AddDays(1))
        {
            if (IsWorkingDay(d))
            {
                days++;
            }
        }

        return Math.Max(days, 1);
    }

    /// <summary>
    /// The finish of a task starting on <paramref name="start"/> that lasts <paramref name="days"/> working days. One day (or
    /// a milestone) finishes on its start.
    /// </summary>
    public DateOnly Finish(DateOnly start, int days) => days <= 1 ? start : AddWorkingDays(OnOrAfter(start), days - 1);

    /// <summary>Working days from <paramref name="from"/> to <paramref name="to"/> (negative when earlier), both moved onto working days.</summary>
    public int Between(DateOnly from, DateOnly to)
    {
        var a = OnOrAfter(from);
        var b = OnOrAfter(to);
        if (a == b)
        {
            return 0;
        }

        var sign = b > a ? 1 : -1;
        var (lo, hi) = sign > 0 ? (a, b) : (b, a);
        var count = 0;
        for (var d = lo; d < hi; d = d.AddDays(1))
        {
            if (IsWorkingDay(d))
            {
                count++;
            }
        }

        return sign * count;
    }
}

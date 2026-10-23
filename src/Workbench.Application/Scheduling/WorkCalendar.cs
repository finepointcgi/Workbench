namespace Workbench.Application.Scheduling;

/// <summary>
/// The plan's working calendar: its working weekdays (Monday to Friday unless the plan says otherwise), less holidays, plus
/// any extra working days. Durations count working days inclusive, so a task from Monday to Friday is 5 days; a milestone
/// is 0. A non-working start counts from the next working day.
/// </summary>
public sealed class WorkCalendar
{
    /// <summary>Monday to Friday: bit <c>(int)DayOfWeek</c> per working weekday.</summary>
    public const int MondayToFriday = 0b0111110;

    private readonly HashSet<DateOnly> _holidays;
    private readonly HashSet<DateOnly> _workingDays;
    private readonly bool[] _weekday = new bool[7];

    // For Index/FromIndex: the working weekdays in Monday-first order, and the sorted exceptions that change a weekday's status.
    private readonly int[] _weekdayOffsets;
    private readonly DateOnly[] _offDays;
    private readonly DateOnly[] _onDays;

    /// <param name="holidays">Non-working days (company or project holidays).</param>
    /// <param name="workDays">The working weekdays as a mask (bit <c>(int)DayOfWeek</c>); at least one day.</param>
    /// <param name="workingDays">Extra working days, e.g. a working Saturday; they win over <paramref name="holidays"/>.</param>
    public WorkCalendar(IEnumerable<DateOnly>? holidays = null, int workDays = MondayToFriday, IEnumerable<DateOnly>? workingDays = null)
    {
        if ((workDays & 0b1111111) == 0)
        {
            throw new ArgumentException("A calendar needs at least one working weekday.");
        }

        WorkDays = workDays & 0b1111111;
        for (var d = 0; d < 7; d++)
        {
            _weekday[d] = (WorkDays & (1 << d)) != 0;
        }

        _workingDays = (workingDays ?? []).ToHashSet();
        _holidays = (holidays ?? []).Where(h => !_workingDays.Contains(h)).ToHashSet();

        // DayNumber 0 (0001-01-01) is a Monday, so offset 0 is Monday and 6 is Sunday.
        _weekdayOffsets = Enumerable.Range(0, 7).Where(offset => _weekday[(offset + 1) % 7]).ToArray();
        _offDays = _holidays.Where(d => _weekday[(int)d.DayOfWeek]).Order().ToArray();
        _onDays = _workingDays.Where(d => !_weekday[(int)d.DayOfWeek]).Order().ToArray();
    }

    public static WorkCalendar Weekdays { get; } = new();

    /// <summary>The working weekdays mask (bit <c>(int)DayOfWeek</c>).</summary>
    public int WorkDays { get; }

    /// <summary>Holidays that fall on what would be working days, and those on other days.</summary>
    public IReadOnlyCollection<DateOnly> Holidays => _holidays;

    /// <summary>Extra working days on what would be days off.</summary>
    public IReadOnlyCollection<DateOnly> ExtraWorkingDays => _workingDays;

    public bool IsWorkingDay(DateOnly date) =>
        _workingDays.Contains(date) || (_weekday[(int)date.DayOfWeek] && !_holidays.Contains(date));

    /// <summary>The date itself when it's a working day, else the next working day.</summary>
    public DateOnly OnOrAfter(DateOnly date)
    {
        while (!IsWorkingDay(date))
        {
            date = date.AddDays(1);
        }

        return date;
    }

    /// <summary>The date itself when it's a working day, else the previous working day.</summary>
    public DateOnly OnOrBefore(DateOnly date)
    {
        while (!IsWorkingDay(date))
        {
            date = date.AddDays(-1);
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

    /// <summary>Working days from start to finish inclusive; at least 1 for a task (a task on days off only is a day), 0 for a milestone.</summary>
    public int Duration(DateOnly start, DateOnly finish, bool isMilestone = false)
    {
        if (isMilestone)
        {
            return 0;
        }

        return Math.Max(Index(finish.AddDays(1)) - Index(start), 1);
    }

    /// <summary>
    /// The finish of a task starting on <paramref name="start"/> that lasts <paramref name="days"/> working days. One day (or
    /// a milestone) finishes on its start.
    /// </summary>
    public DateOnly Finish(DateOnly start, int days) => days <= 1 ? start : AddWorkingDays(OnOrAfter(start), days - 1);

    /// <summary>The start of a task finishing on <paramref name="finish"/> that lasts <paramref name="days"/> working days.</summary>
    public DateOnly StartFor(DateOnly finish, int days) => days <= 1 ? finish : AddWorkingDays(OnOrBefore(finish), -(days - 1));

    /// <summary>Working days from <paramref name="from"/> to <paramref name="to"/> (negative when earlier), both moved onto working days.</summary>
    public int Between(DateOnly from, DateOnly to) => Index(to) - Index(from);

    /// <summary>
    /// The number of working days before <paramref name="date"/> since 0001-01-01. A working day's index is its position; a day
    /// off has the index of the next working day. Differences are working days, as the critical path uses them.
    /// </summary>
    public int Index(DateOnly date)
    {
        var n = date.DayNumber;
        var weeks = n / 7;
        var rest = n % 7;
        var index = weeks * _weekdayOffsets.Length;
        foreach (var offset in _weekdayOffsets)
        {
            if (offset < rest)
            {
                index++;
            }
        }

        return index - CountBefore(_offDays, date) + CountBefore(_onDays, date);
    }

    /// <summary>The working day with this <see cref="Index"/>.</summary>
    public DateOnly FromIndex(int index)
    {
        // The weekly pattern alone, then corrected for the exceptions; each correction step lands exactly (indexes count working days).
        var perWeek = _weekdayOffsets.Length;
        var weeks = Math.DivRem(index, perWeek, out var rest);
        if (rest < 0)
        {
            weeks--;
            rest += perWeek;
        }

        var date = OnOrAfter(DateOnly.FromDayNumber(Math.Max(weeks * 7 + _weekdayOffsets[rest], 0)));
        for (var guard = 0; guard < 64; guard++)
        {
            var delta = index - Index(date);
            if (delta == 0)
            {
                return date;
            }

            date = AddWorkingDays(date, delta);
        }

        return date;
    }

    private static int CountBefore(DateOnly[] sorted, DateOnly date)
    {
        var at = Array.BinarySearch(sorted, date);
        return at >= 0 ? at : ~at;
    }
}

using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Calendar;

/// <summary>An item placed on the calendar with an inclusive date range.</summary>
public sealed record CalendarEntry(ItemDto Item, DateOnly Start, DateOnly End);

/// <summary>
/// The part of an entry that falls in one week. <see cref="Column"/> is the weekday (0 = Sunday),
/// <see cref="Lane"/> the row within the week so overlapping bars stack.
/// </summary>
public sealed record CalendarSegment(CalendarEntry Entry, DateOnly Start, DateOnly End, int Column, int Span, int Lane)
{
    /// <summary>The entry continues from the previous week.</summary>
    public bool ContinuesBefore => Entry.Start < Start;

    /// <summary>The entry continues into the next week.</summary>
    public bool ContinuesAfter => Entry.End > End;

    public bool Covers(DateOnly day) => day >= Start && day <= End;
}

public sealed record CalendarWeek(DateOnly Start, IReadOnlyList<CalendarSegment> Segments, int LaneCount)
{
    public IEnumerable<DateOnly> Days => Enumerable.Range(0, 7).Select(Start.AddDays);

    public bool Covers(DateOnly day) => day >= Start && day <= Start.AddDays(6);
}

/// <summary>Builds a Sunday-first month grid and lays multi-day entries out as bars, one segment per week.</summary>
public static class CalendarLayout
{
    public static DateOnly FirstOfMonth(DateOnly day) => new(day.Year, day.Month, 1);

    /// <summary>The Sunday on or before the first of the month through the Saturday on or after its last day.</summary>
    public static (DateOnly First, DateOnly Last) GridRange(DateOnly month)
    {
        var first = FirstOfMonth(month);
        var last = first.AddMonths(1).AddDays(-1);
        return (first.AddDays(-(int)first.DayOfWeek), last.AddDays(6 - (int)last.DayOfWeek));
    }

    public static IReadOnlyList<CalendarWeek> Build(DateOnly month, IEnumerable<CalendarEntry> entries)
    {
        var (gridStart, gridEnd) = GridRange(month);
        var visible = entries
            .Where(e => e.End >= gridStart && e.Start <= gridEnd)
            .OrderBy(e => e.Start)
            .ThenByDescending(e => e.End.DayNumber - e.Start.DayNumber)
            .ThenBy(e => e.Item.Title, StringComparer.CurrentCulture)
            .ToList();

        var weeks = new List<CalendarWeek>();
        for (var weekStart = gridStart; weekStart <= gridEnd; weekStart = weekStart.AddDays(7))
        {
            var weekEnd = weekStart.AddDays(6);
            var segments = new List<CalendarSegment>();
            // laneEnds[i] = last day occupied in lane i this week.
            var laneEnds = new List<DateOnly>();
            foreach (var entry in visible.Where(e => e.End >= weekStart && e.Start <= weekEnd))
            {
                var start = entry.Start < weekStart ? weekStart : entry.Start;
                var end = entry.End > weekEnd ? weekEnd : entry.End;
                var lane = laneEnds.FindIndex(occupiedUntil => occupiedUntil < start);
                if (lane < 0)
                {
                    lane = laneEnds.Count;
                    laneEnds.Add(end);
                }
                else
                {
                    laneEnds[lane] = end;
                }

                segments.Add(new CalendarSegment(entry, start, end, start.DayNumber - weekStart.DayNumber, end.DayNumber - start.DayNumber + 1, lane));
            }

            weeks.Add(new CalendarWeek(weekStart, segments, laneEnds.Count));
        }

        return weeks;
    }

    /// <summary>
    /// Entries from the item schedule. Items with only one of Start/Finish show on that day;
    /// items with neither aren't placed.
    /// </summary>
    public static IEnumerable<CalendarEntry> FromSchedule(IEnumerable<ItemDto> items)
    {
        foreach (var item in items)
        {
            var start = item.Start ?? item.Finish;
            var end = item.Finish ?? item.Start;
            if (start is { } s && end is { } e)
            {
                yield return e < s ? new CalendarEntry(item, e, s) : new CalendarEntry(item, s, e);
            }
        }
    }

    /// <summary>Single-day entries from a Date column (<c>yyyy-MM-dd</c> values).</summary>
    public static IEnumerable<CalendarEntry> FromDateColumn(IEnumerable<ItemDto> items, ColumnDto column)
    {
        var key = column.Id.ToString();
        foreach (var item in items)
        {
            if (item.Values.TryGetValue(key, out var raw)
                && DateOnly.TryParseExact(raw, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
            {
                yield return new CalendarEntry(item, day, day);
            }
        }
    }
}

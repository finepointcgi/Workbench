namespace Workbench.Application.Scheduling;

/// <summary>
/// Maps dates onto a Mon–Fri working-day index. <see cref="DateOnly.DayNumber"/> 0 (0001-01-01) is a Monday,
/// so a Saturday or Sunday maps to the index of the following Monday.
/// </summary>
public static class WorkingDays
{
    public static int Index(DateOnly date)
    {
        var n = date.DayNumber;
        return n / 7 * 5 + Math.Min(n % 7, 5);
    }

    public static DateOnly FromIndex(int index) => DateOnly.FromDayNumber(index / 5 * 7 + index % 5);

    /// <summary>Working days from start to finish inclusive. A milestone (start == finish) has no duration.</summary>
    public static int Duration(DateOnly start, DateOnly finish) =>
        start == finish ? 0 : Math.Max(Index(finish.AddDays(1)) - Index(start), 0);
}

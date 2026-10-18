namespace Workbench.Application.Leave;

/// <summary>
/// The eleven US federal holidays (5 U.S.C. 6103) for a year, on their observed dates: a holiday on a Saturday is
/// observed the Friday before and one on a Sunday the Monday after. New Year's Day of the following year can be observed
/// on December 31; it belongs to the year it is observed in.
/// </summary>
public static class FederalHolidays
{
    public static IReadOnlyList<(DateOnly Date, string Name)> For(int year)
    {
        var list = new List<(DateOnly, string)>
        {
            (Observed(new DateOnly(year, 1, 1)), "New Year's Day"),
            (Nth(year, 1, DayOfWeek.Monday, 3), "Birthday of Martin Luther King, Jr."),
            (Nth(year, 2, DayOfWeek.Monday, 3), "Washington's Birthday"),
            (Last(year, 5, DayOfWeek.Monday), "Memorial Day"),
            (Observed(new DateOnly(year, 6, 19)), "Juneteenth National Independence Day"),
            (Observed(new DateOnly(year, 7, 4)), "Independence Day"),
            (Nth(year, 9, DayOfWeek.Monday, 1), "Labor Day"),
            (Nth(year, 10, DayOfWeek.Monday, 2), "Columbus Day"),
            (Observed(new DateOnly(year, 11, 11)), "Veterans Day"),
            (Nth(year, 11, DayOfWeek.Thursday, 4), "Thanksgiving Day"),
            (Observed(new DateOnly(year, 12, 25)), "Christmas Day"),
        };

        // New Year's Day falling on a Saturday is observed on December 31 of the year before.
        if (Observed(new DateOnly(year + 1, 1, 1)) is { Year: var y } nextNewYear && y == year)
        {
            list.Add((nextNewYear, "New Year's Day (observed)"));
        }

        return list.Where(h => h.Item1.Year == year).OrderBy(h => h.Item1).ToList();
    }

    public static DateOnly Observed(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Saturday => date.AddDays(-1),
        DayOfWeek.Sunday => date.AddDays(1),
        _ => date,
    };

    private static DateOnly Nth(int year, int month, DayOfWeek day, int n)
    {
        var first = new DateOnly(year, month, 1);
        return first.AddDays(((int)day - (int)first.DayOfWeek + 7) % 7 + 7 * (n - 1));
    }

    private static DateOnly Last(int year, int month, DayOfWeek day)
    {
        var last = new DateOnly(year, month, DateTime.DaysInMonth(year, month));
        return last.AddDays(-(((int)last.DayOfWeek - (int)day + 7) % 7));
    }
}

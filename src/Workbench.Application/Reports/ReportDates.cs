using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Reports;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Reports;

/// <summary>Resolves relative date ranges against today (the organization's time zone) and the fiscal calendar.</summary>
public static class ReportDates
{
    /// <summary>
    /// The [from, to] dates for a range; null ends are open. <see cref="ReportDateRange.Custom"/> uses the given dates.
    /// Weeks start on Monday. Without a fiscal year covering today, fiscal years run October–September.
    /// </summary>
    public static async Task<(DateOnly? From, DateOnly? To)> ResolveAsync(
        IWorkbenchDbContext db, ReportDateRange range, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        (DateOnly? Start, DateOnly? End) span = range switch
        {
            ReportDateRange.Custom => (from, to),
            ReportDateRange.All => ((DateOnly?)null, (DateOnly?)null),
            ReportDateRange.Today => (today, today),
            ReportDateRange.Yesterday => (today.AddDays(-1), today.AddDays(-1)),
            ReportDateRange.ThisWeek => (Monday(today), Monday(today).AddDays(6)),
            ReportDateRange.LastWeek => (Monday(today).AddDays(-7), Monday(today).AddDays(-1)),
            ReportDateRange.ThisMonth => (FirstOfMonth(today), FirstOfMonth(today).AddMonths(1).AddDays(-1)),
            ReportDateRange.LastMonth => (FirstOfMonth(today).AddMonths(-1), FirstOfMonth(today).AddDays(-1)),
            ReportDateRange.Last30Days => (today.AddDays(-29), today),
            ReportDateRange.Last90Days => (today.AddDays(-89), today),
            ReportDateRange.YearToDate => (new DateOnly(today.Year, 1, 1), today),
            ReportDateRange.ThisFiscalYear => await FiscalYearAsync(db, today, ct),
            ReportDateRange.LastFiscalYear => await FiscalYearAsync(db, (await FiscalYearAsync(db, today, ct)).Start.AddDays(-1), ct),
            _ => throw new ArgumentException($"Unknown date range {range}."),
        };

        var (start, end) = span;
        if (start is { } s && end is { } e && e < s)
        {
            throw new ArgumentException("The end date can't be before the start date.");
        }

        return (start, end);
    }

    public static string Describe(DateOnly? from, DateOnly? to) => (from, to) switch
    {
        (null, null) => "All dates",
        ({ } f, null) => $"From {f:yyyy-MM-dd}",
        (null, { } t) => $"Through {t:yyyy-MM-dd}",
        ({ } f, { } t) when f == t => $"{f:yyyy-MM-dd}",
        ({ } f, { } t) => $"{f:yyyy-MM-dd} to {t:yyyy-MM-dd}",
    };

    private static DateOnly Monday(DateOnly date) => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    private static DateOnly FirstOfMonth(DateOnly date) => new(date.Year, date.Month, 1);

    private static async Task<(DateOnly Start, DateOnly End)> FiscalYearAsync(IWorkbenchDbContext db, DateOnly date, CancellationToken ct)
    {
        var year = await db.Set<FiscalYear>().AsNoTracking()
            .Where(y => y.Start <= date && y.End >= date)
            .Select(y => new { y.Start, y.End })
            .FirstOrDefaultAsync(ct);
        if (year is not null)
        {
            return (year.Start, year.End);
        }

        var start = new DateOnly(date.Month >= 10 ? date.Year : date.Year - 1, 10, 1);
        return (start, start.AddYears(1).AddDays(-1));
    }
}

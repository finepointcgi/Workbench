using Workbench.ApiClient;
using Workbench.Contracts;
using Workbench.Tests.Identity;

namespace Workbench.Tests.GoldenPath;

/// <summary>"What changed since last month" on the real host and demo seed: Finance gets sentences and numbers that tie to the income statement; nobody else does.</summary>
public sealed class MonthReviewTests
{
    [Fact]
    public async Task Finance_gets_the_month_against_the_one_before_and_it_ties_to_the_income_statement()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        var calendar = new HttpFiscalCalendarService(finance);
        var reports = new HttpAccountingReportService(finance);

        var periods = (await calendar.ListPeriodsAsync()).OrderBy(p => p.Start).ToList();
        var october = periods.Single(p => p.Name == "2026-10");

        var review = await reports.GetMonthReviewAsync(october.Id);
        var statement = await reports.GetIncomeStatementAsync(october.Id);

        Assert.Equal("2026-10", review.PeriodName);
        Assert.Equal("2026-09", review.PreviousPeriodName);
        Assert.NotEmpty(review.Highlights);

        // The totals are the income statement's, not a second calculation.
        var revenue = review.Metrics.Single(m => m.Label == "Total revenue");
        Assert.Equal(statement.Lines.Single(l => l.IsTotal && l.Label == "Total revenue").Period, revenue.Current);
        var net = review.Metrics.Single(m => m.Label == "Net income");
        Assert.Equal(statement.Lines.Single(l => l.IsTotal && l.Label == "Net income").Period, net.Current);
        Assert.All(review.Metrics.Where(m => m.Previous is not null), m => Assert.Equal(m.Current - m.Previous!.Value, m.Change));

        // Movers are the biggest changes first, and each one's change is this month minus last.
        Assert.InRange(review.Movers.Count, 0, 5);
        Assert.Equal(review.Movers.OrderByDescending(m => Math.Abs(m.Change)).Select(m => m.ProjectCode), review.Movers.Select(m => m.ProjectCode));
        Assert.All(review.Movers, m => Assert.Equal(m.Current - m.Previous, m.Change));
    }

    [Fact]
    public async Task The_first_period_says_there_is_nothing_to_compare_and_an_employee_cannot_see_any_of_it()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        var first = (await new HttpFiscalCalendarService(finance).ListPeriodsAsync()).OrderBy(p => p.Start).First();

        var review = await new HttpAccountingReportService(finance).GetMonthReviewAsync(first.Id);

        Assert.Null(review.PreviousPeriodName);
        Assert.Empty(review.Movers);
        Assert.Contains("nothing to compare", review.Highlights.Single());

        using var employee = web.CreateClientAs("analyst@example.com");
        await Assert.ThrowsAnyAsync<Exception>(() => new HttpAccountingReportService(employee).GetMonthReviewAsync(first.Id));
    }
}

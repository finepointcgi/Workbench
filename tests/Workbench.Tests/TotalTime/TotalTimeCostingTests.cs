using Workbench.Application.TotalTime;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.TotalTime;

/// <summary>The labor cost methods' arithmetic (<see cref="TotalTimeCosting"/>), without a database.</summary>
public sealed class TotalTimeCostingTests
{
    private static readonly DateOnly Monday = new(2026, 10, 19);

    /// <summary>A 50-hour week: six hours a day on project A and four on project B, Monday to Friday.</summary>
    private static List<TimeCostInput> FiftyHourWeek(out HashSet<Guid> projectA)
    {
        var entries = new List<TimeCostInput>();
        projectA = [];
        for (var d = 0; d < 5; d++)
        {
            var a = new TimeCostInput(Guid.NewGuid(), Monday.AddDays(d), 6m);
            projectA.Add(a.EntryId);
            entries.Add(a);
            entries.Add(new TimeCostInput(Guid.NewGuid(), Monday.AddDays(d), 4m));
        }

        return entries;
    }

    [Fact]
    public void Effective_rate_is_the_salary_over_all_hours_worked()
    {
        var entries = FiftyHourWeek(out var projectA);

        var cost = TotalTimeCosting.Cost(LaborCostMethod.TotalTimeEffectiveRate, true, 40m, entries, _ => 60m);

        Assert.True(cost.EffectiveRateApplied);
        Assert.Equal(50m, cost.WorkedHours);
        Assert.Equal(10m, cost.UncompensatedHours);
        Assert.Equal(2400m, cost.PeriodSalary);   // 40 standard hours × $60
        Assert.Equal(2400m, cost.LaborCost);      // the salary, not 50 × $60 = 3,000
        Assert.Equal(48m, cost.EffectiveRate);    // 2,400 ÷ 50
        Assert.Equal(1440m, cost.Entries.Where(e => projectA.Contains(e.EntryId)).Sum(e => e.Cost));   // 30 h × $48
        Assert.Equal(960m, cost.Entries.Where(e => !projectA.Contains(e.EntryId)).Sum(e => e.Cost));   // 20 h × $48
        Assert.All(cost.Entries, e => Assert.Equal(e.Hours * 48m, e.Cost));

        // Uncompensated hours are spread in proportion to the hours charged: 6 of 30 on A, 4 of 20 on B.
        Assert.Equal(6m, cost.Entries.Where(e => projectA.Contains(e.EntryId)).Sum(e => e.UncompensatedHours));
        Assert.Equal(4m, cost.Entries.Where(e => !projectA.Contains(e.EntryId)).Sum(e => e.UncompensatedHours));
    }

    [Theory]
    [InlineData(57.37, new[] { 7.3, 8.1, 9.25, 6.6, 10.15, 4.85 })]
    [InlineData(83.3333, new[] { 9.0, 9.0, 9.0, 9.0, 9.0, 1.5, 0.75 })]
    [InlineData(41.1111, new[] { 11.0, 13.0, 0.25, 12.75, 7.0, 3.0 })]
    public void Distribution_adds_up_to_the_salary_to_the_cent(double rate, double[] hours)
    {
        var entries = hours.Select((h, i) => new TimeCostInput(Guid.NewGuid(), Monday.AddDays(i % 5), (decimal)h)).ToList();
        var salary = Math.Round((decimal)rate * 40m, 2, MidpointRounding.AwayFromZero);

        foreach (var method in new[] { LaborCostMethod.TotalTimeEffectiveRate, LaborCostMethod.Hybrid })
        {
            var cost = TotalTimeCosting.Cost(method, true, 40m, entries, _ => (decimal)rate);

            Assert.True(cost.WorkedHours > 40m);
            Assert.Equal(salary, cost.LaborCost);
            Assert.Equal(salary, cost.Entries.Sum(e => e.Cost));
            Assert.Equal(cost.WorkedHours - 40m, cost.Entries.Sum(e => e.UncompensatedHours));
            Assert.All(cost.Entries, e => Assert.Equal(Math.Round(e.Cost, 2), e.Cost));
        }
    }

    [Theory]
    [InlineData(LaborCostMethod.Standard)]
    [InlineData(LaborCostMethod.TotalTimeEffectiveRate)]
    [InlineData(LaborCostMethod.Hybrid)]
    public void Non_exempt_employees_are_costed_hours_times_rate_whatever_the_method(LaborCostMethod method)
    {
        var entries = FiftyHourWeek(out _);

        var cost = TotalTimeCosting.Cost(method, false, 40m, entries, d => d.DayOfWeek == DayOfWeek.Monday ? 33.33m : 35.55m);

        Assert.False(cost.EffectiveRateApplied);
        Assert.Equal(0m, cost.UncompensatedHours);
        Assert.Null(cost.PeriodSalary);
        Assert.All(cost.Entries, e => Assert.Equal(Math.Round(e.Hours * e.BaseRate!.Value, 2, MidpointRounding.AwayFromZero), e.Cost));
        Assert.All(cost.Entries, e => Assert.Equal(0m, e.UncompensatedHours));
        Assert.Equal(10m * 33.33m + 40m * 35.55m, cost.LaborCost);   // Every hour costs: their overtime is paid.
    }

    [Fact]
    public void Standard_records_hours_beyond_the_standard_week_at_no_cost_in_the_order_worked()
    {
        // Ten hours a day on one charge code: Thursday's last two hours and all of Friday are past 40.
        var entries = Enumerable.Range(0, 5).Select(d => new TimeCostInput(Guid.NewGuid(), Monday.AddDays(d), 10m)).ToList();

        var cost = TotalTimeCosting.Cost(LaborCostMethod.Standard, true, 40m, entries, _ => 60m);

        Assert.False(cost.EffectiveRateApplied);
        Assert.Equal(2400m, cost.LaborCost);
        Assert.Equal([600m, 600m, 600m, 600m, 0m], cost.Entries.Select(e => e.Cost));
        Assert.Equal([0m, 0m, 0m, 0m, 10m], cost.Entries.Select(e => e.UncompensatedHours));
        Assert.Equal(48m, cost.EffectiveRate);

        var split = TotalTimeCosting.Cost(LaborCostMethod.Standard, true, 35m, entries, _ => 60m);
        Assert.Equal([600m, 600m, 600m, 300m, 0m], split.Entries.Select(e => e.Cost));   // Thursday: 5 paid, 5 uncompensated.
        Assert.Equal([0m, 0m, 0m, 5m, 10m], split.Entries.Select(e => e.UncompensatedHours));
    }

    [Fact]
    public void Within_the_standard_week_hybrid_matches_standard_and_total_time_spreads_the_salary()
    {
        // 32 hours (a short week with no leave recorded).
        var entries = Enumerable.Range(0, 4).Select(d => new TimeCostInput(Guid.NewGuid(), Monday.AddDays(d), 8m)).ToList();

        var standard = TotalTimeCosting.Cost(LaborCostMethod.Standard, true, 40m, entries, _ => 60m);
        var hybrid = TotalTimeCosting.Cost(LaborCostMethod.Hybrid, true, 40m, entries, _ => 60m);
        var tta = TotalTimeCosting.Cost(LaborCostMethod.TotalTimeEffectiveRate, true, 40m, entries, _ => 60m);

        Assert.Equal(1920m, standard.LaborCost);
        Assert.Equal(standard.Entries.Select(e => e.Cost), hybrid.Entries.Select(e => e.Cost));
        Assert.False(hybrid.EffectiveRateApplied);
        Assert.Equal(2400m, tta.LaborCost);   // The whole salary over 32 hours: $75/h.
        Assert.Equal(75m, tta.EffectiveRate);
        Assert.All([standard, hybrid, tta], c => Assert.Equal(0m, c.UncompensatedHours));
    }

    [Fact]
    public void A_rate_change_inside_the_period_keeps_each_days_rate_in_the_spread()
    {
        // $50 on Monday, $60 from Tuesday; 10 hours a day.
        var entries = Enumerable.Range(0, 5).Select(d => new TimeCostInput(Guid.NewGuid(), Monday.AddDays(d), 10m)).ToList();

        var cost = TotalTimeCosting.Cost(LaborCostMethod.TotalTimeEffectiveRate, true, 40m, entries, d => d == Monday ? 50m : 60m);

        Assert.Equal(2320m, cost.LaborCost);   // (10 × 50 + 40 × 60) × 40 / 50
        Assert.Equal(400m, cost.Entries[0].Cost);
        Assert.Equal(480m, cost.Entries[1].Cost);
    }
}

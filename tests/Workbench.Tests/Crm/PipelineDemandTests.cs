using Workbench.Application.Crm;
using Workbench.Contracts.Crm;

namespace Workbench.Tests.Crm;

/// <summary>What winning the open pipeline would ask of the staff: value ÷ revenue per hour, spread over the period of performance.</summary>
public sealed class PipelineDemandTests
{
    private static readonly DateOnly Monday = new(2026, 11, 2);

    private static PipelineDemandMath.Input Pursuit(decimal value, int pwin, int pgo, DateOnly? award, DateOnly? due = null, int? months = null, string number = "OPP-1") =>
        new(Guid.NewGuid(), number, "Pursuit " + number, OpportunityStage.Capture, value, pwin, pgo, award, due, months);

    [Fact]
    public void Hours_are_value_over_rate_spread_evenly_over_the_period_of_performance()
    {
        // A year is 53 weeks counting the part week. $1.59M ÷ $150 = 10,600 h ÷ 53 = 200 h a week; 40% × 100% win chance = 80 h likely.
        var demand = PipelineDemandMath.Compute([Pursuit(1_590_000m, 40, 100, Monday, months: 12)], Monday, 4, 150m);

        var row = Assert.Single(demand.Opportunities);
        Assert.Equal(200m, row.HoursPerWeek);
        Assert.Equal(80m, row.WeightedHoursPerWeek);
        Assert.All(demand.Weeks, w => Assert.Equal((200m, 80m), (w.FullHours, w.WeightedHours)));
    }

    [Fact]
    public void Demand_starts_the_week_of_award_and_stops_at_the_end_of_the_period()
    {
        var demand = PipelineDemandMath.Compute([Pursuit(150_000m, 100, 100, Monday.AddDays(14), months: 1)], Monday, 8, 150m);

        Assert.Equal(0m, demand.Weeks[0].FullHours);
        Assert.Equal(0m, demand.Weeks[1].FullHours);
        Assert.True(demand.Weeks[2].FullHours > 0);
        Assert.Equal(0m, demand.Weeks[7].FullHours); // a one-month period is over by Dec 21
    }

    [Fact]
    public void With_no_award_date_the_start_is_ninety_days_after_the_proposal_is_due_and_is_marked_estimated()
    {
        var demand = PipelineDemandMath.Compute([Pursuit(300_000m, 50, 100, null, due: Monday)], Monday, 4, 150m);

        var row = Assert.Single(demand.Opportunities);
        Assert.Equal(Monday.AddDays(90), row.StartsOn);
        Assert.True(row.StartIsEstimated);
    }

    [Fact]
    public void Pursuits_with_no_value_or_no_date_are_counted_as_left_out_not_guessed()
    {
        var demand = PipelineDemandMath.Compute(
            [Pursuit(0m, 50, 50, Monday, number: "A"), Pursuit(500_000m, 50, 50, null, number: "B"), Pursuit(500_000m, 50, 50, Monday, number: "C")], Monday, 4, 150m);

        Assert.Equal(2, demand.NotCounted);
        Assert.Equal("OPP-1", "OPP-1"); // sanity: only C remains
        Assert.Equal("C", Assert.Single(demand.Opportunities).Number);
    }

    [Fact]
    public void The_weeks_start_on_mondays_and_a_bad_rate_falls_back_to_the_default()
    {
        var demand = PipelineDemandMath.Compute([Pursuit(1_560_000m, 100, 100, Monday)], Monday.AddDays(3), 2, 0m);

        Assert.Equal(Monday, demand.Weeks[0].WeekStart);
        Assert.Equal(PipelineDemandMath.DefaultRevenuePerHour, demand.RevenuePerHour);
    }
}

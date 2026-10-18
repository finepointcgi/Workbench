using Workbench.Application.Evm;
using Workbench.Domain.Evm;

namespace Workbench.Tests.Evm;

public sealed class EvmCalculatorTests
{
    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 0)]
    [InlineData(true, true, 100)]
    public void Zero_hundred_earns_only_at_completion(bool started, bool complete, decimal expected) =>
        Assert.Equal(expected, EvmCalculator.EarnedPercent(EarnedValueTechnique.ZeroHundred, new EvProgress(started, complete, 80), 60));

    [Theory]
    [InlineData(false, false, 0)]
    [InlineData(true, false, 50)]
    [InlineData(true, true, 100)]
    public void Fifty_fifty_earns_half_at_start_and_the_rest_at_completion(bool started, bool complete, decimal expected) =>
        Assert.Equal(expected, EvmCalculator.EarnedPercent(EarnedValueTechnique.FiftyFifty, new EvProgress(started, complete, 10), 60));

    [Fact]
    public void Percent_complete_earns_the_percent_and_clamps_it()
    {
        Assert.Equal(40m, EvmCalculator.EarnedPercent(EarnedValueTechnique.PercentComplete, new EvProgress(true, false, 40), 0));
        Assert.Equal(100m, EvmCalculator.EarnedPercent(EarnedValueTechnique.PercentComplete, new EvProgress(true, false, 140), 0));
        Assert.Equal(100m, EvmCalculator.EarnedPercent(EarnedValueTechnique.PercentComplete, new EvProgress(true, true, 30), 0));
    }

    [Fact]
    public void Milestones_earn_the_weights_met()
    {
        Assert.Equal(35m, EvmCalculator.EarnedPercent(EarnedValueTechnique.Milestones, new EvProgress(MilestoneWeightDone: 35), 90));
        Assert.Equal(1_750m, EvmCalculator.Earned(5_000, 35));
    }

    [Fact]
    public void Level_of_effort_earns_as_planned_and_apportioned_follows_its_base()
    {
        Assert.Equal(62.5m, EvmCalculator.EarnedPercent(EarnedValueTechnique.LevelOfEffort, new EvProgress(true, true, 0), 62.5m));
        Assert.Equal(30m, EvmCalculator.EarnedPercent(EarnedValueTechnique.Apportioned, new EvProgress(ApportionedBasePercent: 30), 80));
    }

    [Fact]
    public void Planned_value_spreads_the_budget_over_working_days()
    {
        // Mon 2026-11-02 .. Fri 2026-11-13: 10 working days.
        var start = new DateOnly(2026, 11, 2);
        var finish = new DateOnly(2026, 11, 13);
        Assert.Equal(0m, EvmCalculator.PlannedValue(1_000, start, finish, new DateOnly(2026, 11, 1)));
        Assert.Equal(500m, EvmCalculator.PlannedValue(1_000, start, finish, new DateOnly(2026, 11, 6)));
        Assert.Equal(500m, EvmCalculator.PlannedValue(1_000, start, finish, new DateOnly(2026, 11, 8))); // the weekend adds nothing
        Assert.Equal(1_000m, EvmCalculator.PlannedValue(1_000, start, finish, new DateOnly(2026, 12, 1)));
    }

    [Fact]
    public void Indices_and_eac_methods_follow_the_standard_formulas()
    {
        var m = EvmCalculator.Metrics(bac: 1_000, bcws: 400, bcwp: 300, acwp: 500);

        Assert.Equal(-100m, m.Sv);
        Assert.Equal(-200m, m.Cv);
        Assert.Equal(-25m, m.SvPercent);
        Assert.Equal(-66.67m, m.CvPercent);
        Assert.Equal(0.75m, m.Spi);
        Assert.Equal(0.6m, m.Cpi);
        Assert.Equal(1_666.67m, m.EacCpi); // 500 + 700 / 0.6
        Assert.Equal(2_055.56m, m.EacCpiSpi); // 500 + 700 / 0.45
        Assert.Equal(m.EacCpi, m.EacManager);
        Assert.Equal(1_166.67m, m.Etc);
        Assert.Equal(-666.67m, m.Vac);
        Assert.Equal(1.4m, m.Tcpi); // 700 / 500
        Assert.Equal(0.6m, m.TcpiEac); // 700 / 1166.67
        Assert.Equal(30m, m.PercentComplete);
        Assert.Equal(50m, m.PercentSpent);
    }

    [Fact]
    public void A_managers_estimate_drives_eac_etc_and_vac()
    {
        var m = EvmCalculator.Metrics(1_000, 400, 300, 500, managerEac: 1_200);

        Assert.Equal(1_200m, m.Eac);
        Assert.Equal(700m, m.Etc);
        Assert.Equal(-200m, m.Vac);
        Assert.Equal(1_666.67m, m.EacCpi);
    }

    [Fact]
    public void Indices_are_empty_without_a_denominator()
    {
        var m = EvmCalculator.Metrics(1_000, 0, 0, 0);

        Assert.Null(m.Spi);
        Assert.Null(m.Cpi);
        Assert.Null(m.SvPercent);
        Assert.Equal(1_000m, m.Eac);
        Assert.Equal(1m, m.Tcpi);
    }

    [Fact]
    public void Variances_need_analysis_only_beyond_both_thresholds()
    {
        var big = EvmCalculator.Metrics(100_000, 40_000, 30_000, 31_000);
        var reasons = EvmCalculator.VarianceReasons(big, 10, 5_000);
        Assert.Contains(reasons, r => r.StartsWith("SV -$10,000 (-25.0%)", StringComparison.Ordinal));
        Assert.DoesNotContain(reasons, r => r.StartsWith("CV", StringComparison.Ordinal)); // -1,000 is under $5,000

        var smallDollars = EvmCalculator.Metrics(10_000, 4_000, 3_000, 3_000);
        Assert.Empty(EvmCalculator.VarianceReasons(smallDollars, 10, 5_000)); // -25% but only $1,000
    }
}

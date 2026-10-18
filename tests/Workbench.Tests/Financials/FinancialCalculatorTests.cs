using Workbench.Application.Financials;
using Workbench.Contracts.Financials;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Tests.Financials;

public sealed class FinancialCalculatorTests
{
    private static readonly Guid Alex = Guid.NewGuid();
    private static readonly Guid Engineer = Guid.NewGuid();

    // ---- Effective-dated cost rates ----

    [Fact]
    public void Cost_rate_lookup_uses_the_rate_in_effect_on_the_work_date_including_a_mid_period_change()
    {
        var rates = new EffectiveRateTable<Guid>()
            .Add(Alex, new DateOnly(2026, 9, 16), 60m) // raise in the middle of the September period
            .Add(Alex, new DateOnly(2026, 1, 1), 50m);

        Assert.Null(rates.Find(Alex, new DateOnly(2025, 12, 31)));
        Assert.Equal(50m, rates.Find(Alex, new DateOnly(2026, 1, 1)));
        Assert.Equal(50m, rates.Find(Alex, new DateOnly(2026, 9, 15)));
        Assert.Equal(60m, rates.Find(Alex, new DateOnly(2026, 9, 16)));
        Assert.Equal(60m, rates.Find(Alex, new DateOnly(2027, 3, 1)));
        Assert.Null(rates.Find(Guid.NewGuid(), new DateOnly(2026, 9, 16)));
    }

    [Fact]
    public void A_correction_with_the_same_start_date_replaces_the_earlier_rate()
    {
        var rates = new EffectiveRateTable<Guid>()
            .Add(Alex, new DateOnly(2026, 1, 1), 50m)
            .Add(Alex, new DateOnly(2026, 1, 1), 52.25m);

        Assert.Equal(52.25m, rates.Find(Alex, new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void Labor_is_costed_per_entry_at_the_rate_on_its_work_date()
    {
        var wbs = Guid.NewGuid();
        var category = Guid.NewGuid();
        var costRates = new EffectiveRateTable<Guid>().Add(Alex, new DateOnly(2026, 1, 1), 50m).Add(Alex, new DateOnly(2026, 9, 16), 60m);
        var billRates = new EffectiveRateTable<Guid>().Add(category, new DateOnly(2026, 1, 1), 120m);
        LaborActual[] labor =
        [
            new(wbs, Alex, category, new DateOnly(2026, 9, 15), 8m),
            new(wbs, Alex, category, new DateOnly(2026, 9, 16), 8m),
            new(wbs, Alex, category, new DateOnly(2025, 6, 1), 2m), // before any rate
        ];

        var costed = LaborCosting.Cost(labor, costRates, billRates, out var missing);

        Assert.Equal([400m, 480m, 0m], costed.Select(c => c.Cost));
        Assert.Equal([960m, 960m, 0m], costed.Select(c => c.BillableValue));
        Assert.Equal(2m, missing);
    }

    // ---- Rollups, ETC and EAC ----

    private static readonly WbsNode Root = new(Guid.NewGuid(), null, "1001.01", "Root", 0);
    private static readonly WbsNode A = new(Guid.NewGuid(), Root.Id, "1001.01.01", "A", 0);
    private static readonly WbsNode B = new(Guid.NewGuid(), Root.Id, "1001.01.02", "B", 1);
    private static readonly WbsNode B1 = new(Guid.NewGuid(), B.Id, "1001.01.02.01", "B1", 0);
    private static readonly WbsNode B2 = new(Guid.NewGuid(), B.Id, "1001.01.02.02", "B2", 1);
    private static readonly WbsNode[] Tree = [B2, B1, B, A, Root];

    private static CostedLabor Labor(WbsNode node, decimal hours, decimal cost, DateOnly? on = null) =>
        new(node.Id, on ?? new DateOnly(2026, 10, 5), hours, cost, 0);

    [Fact]
    public void Figures_roll_up_the_wbs_tree_in_tree_order()
    {
        var result = FinancialsCalculator.Compute(new FinancialInputs(
            Tree,
            [new(A.Id, null, 100, 10_000), new(B1.Id, null, 40, 4_000), new(B2.Id, null, 60, 6_000), new(B.Id, null, 0, 500)],
            [Labor(A, 10, 1_000), Labor(B1, 50, 5_000), Labor(B2, 6, 600)],
            [new(B2.Id, new DateOnly(2026, 10, 7), 250)]));

        Assert.Equal(["1001.01", "1001.01.01", "1001.01.02", "1001.01.02.01", "1001.01.02.02"], result.Rows.Select(r => r.Code));
        Assert.Equal([0, 1, 1, 2, 2], result.Rows.Select(r => r.Depth));
        Assert.Equal([false, true, false, true, true], result.Rows.Select(r => r.IsLeaf));

        var b = result.Rows.Single(r => r.WbsElementId == B.Id).Figures;
        Assert.Equal(100m, b.BudgetHours);
        Assert.Equal(10_500m, b.BudgetAmount); // B's own ODC budget plus both children
        Assert.Equal(56m, b.ActualHours);
        Assert.Equal(5_600m, b.ActualLaborCost);
        Assert.Equal(250m, b.ActualOdcCost);
        Assert.Equal(5_850m, b.ActualCost);

        // B1 overran (5,000 vs 4,000) but that doesn't eat into B2's or B's own remaining budget.
        Assert.Equal(0m, result.Rows.Single(r => r.WbsElementId == B1.Id).Figures.EtcAmount);
        Assert.Equal(6_000m - 850m + 500m, b.EtcAmount);
        Assert.Equal(-1_000m, result.Rows.Single(r => r.WbsElementId == B1.Id).Figures.VacAmount);

        var root = result.Rows[0].Figures;
        Assert.Equal(result.Totals, root);
        Assert.Equal(20_500m, root.BudgetAmount);
        Assert.Equal(6_850m, root.ActualCost);
        Assert.Equal(0.3341m, root.PercentSpent);
        Assert.Equal(13_650m, root.RemainingBudget);
    }

    [Fact]
    public void Eac_without_a_forecast_is_actual_plus_the_remaining_budget_floored_at_zero()
    {
        var onTrack = FinancialsCalculator.Compute(new FinancialInputs([A], [new(A.Id, null, 10, 1_000)], [Labor(A, 4, 400)], [])).Totals;
        Assert.Equal(600m, onTrack.EtcAmount);
        Assert.Equal(1_000m, onTrack.EacAmount);
        Assert.Equal(0m, onTrack.VacAmount);
        Assert.Equal(6m, onTrack.EtcHours);
        Assert.Equal(10m, onTrack.EacHours);

        var overrun = FinancialsCalculator.Compute(new FinancialInputs([A], [new(A.Id, null, 10, 1_000)], [Labor(A, 12, 1_200)], [])).Totals;
        Assert.Equal(0m, overrun.EtcAmount);
        Assert.Equal(1_200m, overrun.EacAmount);
        Assert.Equal(-200m, overrun.VacAmount);
        Assert.Equal(-200m, overrun.RemainingBudget);
        Assert.Equal(1.2m, overrun.PercentSpent);
    }

    [Fact]
    public void Eac_with_a_forecast_uses_the_forecasts_remaining_amounts_as_etc()
    {
        var october = new DateOnly(2026, 10, 1);
        var totals = FinancialsCalculator.Compute(new FinancialInputs(
            [A],
            [new(A.Id, null, 10, 1_000)],
            [Labor(A, 4, 400)],
            [],
            Forecast: [new(A.Id, new DateOnly(2026, 9, 1), 3, 300), new(A.Id, october, 5, 500), new(A.Id, null, 2, 200)],
            RemainingFrom: october)).Totals;

        // September is behind us; October onward plus the unphased line are the estimate to complete.
        Assert.Equal(700m, totals.EtcAmount);
        Assert.Equal(7m, totals.EtcHours);
        Assert.Equal(1_100m, totals.EacAmount);
        Assert.Equal(-100m, totals.VacAmount);
        Assert.Equal(11m, totals.EacHours);
    }

    [Fact]
    public void Wbs_elements_without_a_forecast_line_have_no_etc_when_a_forecast_is_selected()
    {
        var totals = FinancialsCalculator.Compute(new FinancialInputs([A], [new(A.Id, null, 10, 1_000)], [Labor(A, 4, 400)], [], Forecast: [])).Totals;
        Assert.Equal(0m, totals.EtcAmount);
        Assert.Equal(400m, totals.EacAmount);
        Assert.Equal(600m, totals.VacAmount);
    }

    // ---- Time phasing ----

    [Fact]
    public void Time_phased_burn_buckets_by_fiscal_period_with_cumulative_series()
    {
        var sep = new PeriodInfo(Guid.NewGuid(), "2026-09", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));
        var oct = new PeriodInfo(Guid.NewGuid(), "2026-10", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        var nov = new PeriodInfo(Guid.NewGuid(), "2026-11", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));

        var result = TimePhasing.Build([nov, sep, oct], new DateOnly(2026, 9, 1),
            [new(A.Id, sep.Start, 0, 1_000), new(A.Id, oct.Start, 0, 2_000), new(A.Id, nov.Start, 0, 3_000), new(A.Id, null, 0, 750)],
            [Labor(A, 8, 800, new DateOnly(2026, 9, 10)), Labor(A, 4, 400, new DateOnly(2026, 10, 2))],
            [new(A.Id, new DateOnly(2026, 10, 3), 150)],
            asOf: new DateOnly(2026, 10, 17));

        Assert.Equal(["2026-09", "2026-10", "2026-11"], result.Periods.Select(p => p.PeriodName));
        Assert.Equal([1_000m, 2_000m, 3_000m], result.Periods.Select(p => p.BudgetAmount));
        Assert.Equal([800m, 550m, 0m], result.Periods.Select(p => p.ActualCost));
        Assert.Equal([1_000m, 3_000m, 6_000m], result.Periods.Select(p => p.CumulativeBudget));
        Assert.Equal([800m, 1_350m, null], result.Periods.Select(p => p.CumulativeActual));
        Assert.Equal(750m, result.UnphasedBudgetAmount);
    }

    // ---- Contract funding ----

    private static readonly PeriodInfo[] Fy26Q4 =
    [
        new(Guid.NewGuid(), "2026-06", new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30)),
        new(Guid.NewGuid(), "2026-07", new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31)),
        new(Guid.NewGuid(), "2026-08", new DateOnly(2026, 8, 1), new DateOnly(2026, 8, 31)),
        new(Guid.NewGuid(), "2026-09", new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
        new(Guid.NewGuid(), "2026-10", new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)),
    ];

    private static ContractTerms Terms(decimal funded, decimal ceiling = 1_000_000m, DateOnly? end = null) =>
        new(Guid.NewGuid(), "W56KGU-26-C-0001", "Support", ContractKind.CostPlusFixedFee, end ?? new DateOnly(2028, 9, 30), ceiling, funded);

    [Fact]
    public void Funding_exhaustion_date_uses_the_average_burn_over_the_last_three_periods()
    {
        SpendEntry[] spend =
        [
            new(new DateOnly(2026, 6, 10), 50_000), // outside the burn window, but still spent
            new(new DateOnly(2026, 7, 15), 30_000),
            new(new DateOnly(2026, 8, 15), 30_000),
            new(new DateOnly(2026, 9, 15), 30_000),
        ];

        var funding = FundingForecaster.Forecast(Terms(funded: 250_000), 1, spend, Fy26Q4, asOf: new DateOnly(2026, 9, 30));

        Assert.Equal(140_000m, funding.Spent);
        Assert.Equal(110_000m, funding.FundedRemaining);
        Assert.Equal(860_000m, funding.CeilingRemaining);
        Assert.Equal(30_000m, funding.AverageBurnPerPeriod);

        // 90,000 over the 92 days from Jul 1 to Sep 30; 110,000 remaining lasts ceil(112.4) = 113 days.
        Assert.Equal(new DateOnly(2027, 1, 21), funding.FundingExhaustionDate);
    }

    [Fact]
    public void Funding_exhaustion_date_is_null_without_burn_or_after_funds_run_out()
    {
        Assert.Null(FundingForecaster.Forecast(Terms(100_000), 1, [], Fy26Q4, new DateOnly(2026, 9, 30)).FundingExhaustionDate);

        var exhausted = FundingForecaster.Forecast(Terms(100_000), 1, [new(new DateOnly(2026, 9, 1), 120_000)], Fy26Q4, new DateOnly(2026, 9, 30));
        Assert.Null(exhausted.FundingExhaustionDate);
        Assert.Equal(-20_000m, exhausted.FundedRemaining);
        Assert.Contains(exhausted.Alerts, a => a.Level == FundingAlertLevel.Critical && a.ThresholdPercent is null);
    }

    [Theory]
    [InlineData(74_999, null)]
    [InlineData(75_000, 75)]
    [InlineData(89_999, 75)]
    [InlineData(90_000, 90)]
    [InlineData(99_000, 90)]
    public void Funding_alerts_fire_at_75_and_90_percent_of_funded_value(int spent, int? expectedThreshold)
    {
        var funding = FundingForecaster.Forecast(Terms(100_000), 1, [new(new DateOnly(2026, 9, 1), spent)], Fy26Q4, new DateOnly(2026, 9, 30));

        var thresholds = funding.Alerts.Where(a => a.ThresholdPercent is not null).ToList();
        if (expectedThreshold is null)
        {
            Assert.Empty(thresholds);
        }
        else
        {
            var alert = Assert.Single(thresholds);
            Assert.Equal(expectedThreshold, alert.ThresholdPercent);
            Assert.Equal(expectedThreshold == 90 ? FundingAlertLevel.Critical : FundingAlertLevel.Warning, alert.Level);
        }
    }

    [Fact]
    public void Funding_that_runs_out_before_the_period_of_performance_ends_is_flagged()
    {
        var funding = FundingForecaster.Forecast(Terms(100_000, end: new DateOnly(2027, 9, 30)), 1,
            [new(new DateOnly(2026, 9, 15), 30_000)], Fy26Q4, new DateOnly(2026, 9, 30));

        Assert.Contains(funding.Alerts, a => a.Level == FundingAlertLevel.Info && a.Message.Contains("before the period of performance", StringComparison.Ordinal));
    }

    // ---- Planning rates and the plan sheet ----

    [Fact]
    public void Planning_rate_averages_assigned_employees_in_the_category_then_falls_back_to_the_category()
    {
        var wbs = Guid.NewGuid();
        var engineering = Guid.NewGuid();
        var analysis = Guid.NewGuid();
        var other = Guid.NewGuid();
        var asOf = new DateOnly(2026, 10, 1);
        var costRates = new EffectiveRateTable<Guid>()
            .Add(Alex, new DateOnly(2025, 1, 1), 50m)
            .Add(Engineer, new DateOnly(2025, 1, 1), 70m)
            .Add(other, new DateOnly(2025, 1, 1), 90m);
        var calculator = new PlanningRateCalculator(
            [
                new PlanningAssignment(wbs, Alex, engineering, null, null), // assignment overrides Alex's default category
                new PlanningAssignment(wbs, Engineer, null, null, null),
            ],
            [
                new PlanningEmployee(Alex, analysis, new DateOnly(2020, 1, 1), null),
                new PlanningEmployee(Engineer, engineering, new DateOnly(2020, 1, 1), null),
                new PlanningEmployee(other, analysis, new DateOnly(2020, 1, 1), null),
            ],
            costRates);

        Assert.Equal(60m, calculator.RateFor(wbs, engineering, asOf));            // Alex and the engineer on this WBS
        Assert.Equal(70m, calculator.RateFor(wbs, analysis, asOf));               // nobody assigned as analyst: everyone whose default is analysis
        Assert.Equal(70m, calculator.RateFor(Guid.NewGuid(), engineering, asOf)); // other WBS: the category average
        Assert.Equal(0m, calculator.RateFor(wbs, Guid.NewGuid(), asOf));          // nobody in that category
    }

    [Fact]
    public void Plan_sheet_sums_take_subtasks_over_the_parents_own_rollup_value()
    {
        var phase = Guid.NewGuid();
        var kickoff = Guid.NewGuid();
        var review = Guid.NewGuid();
        var standalone = Guid.NewGuid();
        var empty = Guid.NewGuid();
        PlanSheetRow[] rows =
        [
            new(phase, null, 999, 999), // a rollup typed by hand: ignored because its subtasks have values
            new(kickoff, phase, 8, 1_200),
            new(review, phase, 60, null),
            new(standalone, null, 40, 6_000),
            new(empty, standalone, null, null),
        ];

        Assert.Equal((68m, 1_200m), PlanSheetBudget.Sum(rows, phase));
        Assert.Equal((8m, 1_200m), PlanSheetBudget.Sum(rows, kickoff));
        Assert.Equal((40m, 6_000m), PlanSheetBudget.Sum(rows, standalone));
        Assert.Equal((0m, 0m), PlanSheetBudget.Sum(rows, Guid.NewGuid()));
    }
}

using Workbench.Application.Crm;
using Workbench.Contracts.Crm;

namespace Workbench.Tests.Crm;

/// <summary>The pipeline's arithmetic: weighted value, gate scores, fiscal quarters and the forecast buckets.</summary>
public sealed class PipelineMathTests
{
    [Theory]
    [InlineData(1_000_000, 50, 80, 400_000)]
    [InlineData(1_000_000, 100, 100, 1_000_000)]
    [InlineData(1_000_000, 0, 80, 0)]
    [InlineData(333_333.33, 33, 67, 73_700)]
    [InlineData(1_000_000, 150, -5, 0)]
    public void Weighted_value_is_value_times_pwin_times_pgo(decimal value, int pwin, int pgo, decimal expected) =>
        Assert.Equal(expected, PipelineMath.Weighted(value, pwin, pgo));

    [Fact]
    public void Gate_score_is_the_weighted_share_of_the_maximum()
    {
        // (4×3 + 2×1) / (5×3 + 5×1) = 14 / 20 = 70%.
        Assert.Equal(70m, PipelineMath.GateScore([(4, 3), (2, 1)]));
        Assert.Equal(100m, PipelineMath.GateScore([(5, 2), (5, 7)]));
        Assert.Equal(0m, PipelineMath.GateScore([]));
    }

    [Theory]
    [InlineData("2026-10-01", 2027, 1)]
    [InlineData("2026-12-31", 2027, 1)]
    [InlineData("2027-01-01", 2027, 2)]
    [InlineData("2027-06-15", 2027, 3)]
    [InlineData("2027-09-30", 2027, 4)]
    [InlineData("2027-10-01", 2028, 1)]
    public void Federal_fiscal_quarters_start_in_October(string date, int year, int quarter) =>
        Assert.Equal((year, quarter), PipelineMath.FiscalQuarter(DateOnly.Parse(date)));

    [Fact]
    public void A_calendar_year_organization_and_a_known_fiscal_year_are_honored()
    {
        Assert.Equal((2027, 2), PipelineMath.FiscalQuarter(new DateOnly(2027, 5, 1), startMonth: 1));
        Assert.Equal((2027, 3), PipelineMath.FiscalQuarter(new DateOnly(2027, 4, 2), yearStart: (new DateOnly(2026, 10, 1), 2027)));
    }

    [Fact]
    public void The_forecast_weights_open_work_by_quarter_and_splits_new_from_recompete()
    {
        var inputs = new[]
        {
            new PipelineMath.ForecastInput(OpportunityStage.Capture, OpportunityKind.New, 1_000_000m, 50, 80, new DateOnly(2026, 11, 15)),
            new PipelineMath.ForecastInput(OpportunityStage.Proposal, OpportunityKind.Recompete, 2_000_000m, 25, 100, new DateOnly(2026, 12, 1)),
            new PipelineMath.ForecastInput(OpportunityStage.Identified, OpportunityKind.New, 500_000m, 10, 50, new DateOnly(2027, 2, 1)),
            new PipelineMath.ForecastInput(OpportunityStage.Qualified, OpportunityKind.New, 800_000m, 50, 50, null),
            new PipelineMath.ForecastInput(OpportunityStage.Lost, OpportunityKind.New, 9_000_000m, 50, 50, new DateOnly(2026, 11, 1)),
            new PipelineMath.ForecastInput(OpportunityStage.Won, OpportunityKind.New, 3_000_000m, 100, 100, new DateOnly(2026, 10, 1), 2_750_000m, new DateOnly(2026, 10, 20)),
        };

        var forecast = PipelineMath.Forecast(inputs, d => PipelineMath.FiscalQuarter(d));

        var q1 = forecast.Quarters.Single(q => q is { FiscalYear: 2027, Quarter: 1 });
        Assert.Equal(2, q1.Count);
        Assert.Equal(3_000_000m, q1.TotalValue);
        Assert.Equal(400_000m + 500_000m, q1.WeightedValue);
        Assert.Equal(400_000m, q1.NewWeighted);
        Assert.Equal(500_000m, q1.RecompeteWeighted);
        Assert.Equal(2_750_000m, q1.WonValue);
        Assert.Equal("FY27 Q1", q1.Label);

        var q2 = forecast.Quarters.Single(q => q is { FiscalYear: 2027, Quarter: 2 });
        Assert.Equal(25_000m, q2.WeightedValue);

        // The lost one is left out; the undated one is counted separately but in the weighted total.
        Assert.Equal(1, forecast.UndatedCount);
        Assert.Equal(200_000m, forecast.UndatedWeighted);
        Assert.Equal(400_000m + 500_000m + 25_000m + 200_000m, forecast.WeightedValue);
        Assert.Equal(3_500_000m, forecast.TotalValue);
    }

    [Theory]
    [InlineData("W91QUZ-26-R-0042", "W91QUZ26R0042")]
    [InlineData(" w91quz 26 r 0042 ", "W91QUZ26R0042")]
    [InlineData("", null)]
    [InlineData("--", null)]
    public void Solicitation_numbers_are_compared_without_punctuation_or_case(string number, string? key) =>
        Assert.Equal(key, PipelineMath.SolicitationKey(number));
}

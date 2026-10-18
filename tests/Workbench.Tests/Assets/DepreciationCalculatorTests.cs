using Workbench.Application.Assets;
using Workbench.Domain.Assets;

namespace Workbench.Tests.Assets;

/// <summary>Each method against hand calculations.</summary>
public sealed class DepreciationCalculatorTests
{
    [Fact]
    public void Straight_line_spreads_cost_less_salvage_evenly_and_the_last_month_takes_the_rounding()
    {
        // (10,000 − 1,000) / 36 = 250.00 a month.
        var even = DepreciationCalculator.MonthlySchedule(10_000m, 1_000m, 3, DepreciationMethod.StraightLine);
        Assert.Equal(36, even.Count);
        Assert.All(even, m => Assert.Equal(250m, m));

        // 1,000 / 36 = 27.777…: 35 × 27.78 = 972.30, so the last month is 27.70.
        var uneven = DepreciationCalculator.MonthlySchedule(1_000m, 0, 3, DepreciationMethod.StraightLine);
        Assert.Equal(27.78m, uneven[0]);
        Assert.Equal(27.70m, uneven[^1]);
        Assert.Equal(1_000m, uneven.Sum());
    }

    [Fact]
    public void Declining_balance_is_double_the_straight_line_rate_switching_to_straight_line_and_stopping_at_salvage()
    {
        // Cost 10,000, salvage 1,000, 5 years: rate 40% of opening book value.
        //   Y1 10,000 × 40% = 4,000 · Y2 6,000 × 40% = 2,400 · Y3 3,600 × 40% = 1,440
        //   Y4 2,160 × 40% = 864 (straight line would be 580) · Y5 the rest down to salvage: 1,296 − 1,000 = 296.
        var annual = DepreciationCalculator.AnnualAmounts(10_000m, 1_000m, 5, DepreciationMethod.DecliningBalance);
        Assert.Equal([4_000m, 2_400m, 1_440m, 864m, 296m], annual);

        var monthly = DepreciationCalculator.MonthlySchedule(10_000m, 1_000m, 5, DepreciationMethod.DecliningBalance);
        Assert.Equal(333.33m, monthly[0]);
        Assert.Equal(333.37m, monthly[11]);
        Assert.Equal(4_000m, monthly.Take(12).Sum());
        Assert.Equal(9_000m, monthly.Sum());

        // With no salvage the switch to straight line happens: 8,000 / 4 years.
        //   Y1 3,200 · Y2 1,920 · Y3 1,152 · Y4 max(691.20, 1,728/2 = 864) = 864 · Y5 864.
        Assert.Equal([3_200m, 1_920m, 1_152m, 864m, 864m], DepreciationCalculator.AnnualAmounts(8_000m, 0, 5, DepreciationMethod.DecliningBalance));
    }

    [Fact]
    public void Sum_of_years_digits_weights_the_early_years()
    {
        // 9,000 depreciable over 5 years; digits 15: 5/15, 4/15, 3/15, 2/15, 1/15.
        Assert.Equal([3_000m, 2_400m, 1_800m, 1_200m, 600m], DepreciationCalculator.AnnualAmounts(10_000m, 1_000m, 5, DepreciationMethod.SumOfYearsDigits));
        var monthly = DepreciationCalculator.MonthlySchedule(10_000m, 1_000m, 5, DepreciationMethod.SumOfYearsDigits);
        Assert.Equal(250m, monthly[0]);
        Assert.Equal(200m, monthly[12]);
        Assert.Equal(50m, monthly[^1]);
    }

    [Fact]
    public void Macrs_follows_the_irs_half_year_table_and_ignores_salvage()
    {
        // 5-year property, 10,000: 20%, 32%, 19.2%, 11.52%, 11.52%, 5.76%.
        Assert.Equal([2_000m, 3_200m, 1_920m, 1_152m, 1_152m, 576m], DepreciationCalculator.Macrs(10_000m, 5));
        Assert.Equal(1_234.56m, DepreciationCalculator.Macrs(1_234.56m, 7).Sum());
        Assert.Throws<ArgumentException>(() => DepreciationCalculator.Macrs(1_000m, 4));
    }

    [Fact]
    public void Cumulative_and_month_index_count_whole_months_from_the_in_service_month()
    {
        var inService = new DateOnly(2025, 5, 20);
        Assert.Equal(0, DepreciationCalculator.MonthIndex(inService, new DateOnly(2025, 5, 1)));
        Assert.Equal(17, DepreciationCalculator.MonthIndex(inService, new DateOnly(2026, 10, 1)));
        var schedule = DepreciationCalculator.MonthlySchedule(12_000m, 0, 5, DepreciationMethod.StraightLine);
        Assert.Equal(3_600m, DepreciationCalculator.CumulativeThrough(schedule, 17));
        Assert.Equal(0m, DepreciationCalculator.CumulativeThrough(schedule, -1));
        Assert.Equal(12_000m, DepreciationCalculator.CumulativeThrough(schedule, 500));
    }
}

using Workbench.Application.Assets;
using Workbench.Domain.Assets;

namespace Workbench.Tests.Assets;

/// <summary>MACRS rates against the IRS tables (Rev. Proc. 87-57 / Pub. 946 appendix A), conventions, Section 179, bonus and the mid-quarter test.</summary>
public sealed class TaxDepreciationTests
{
    private static decimal[] Percent(IReadOnlyList<decimal> rates) => rates.Select(r => decimal.Round(r * 100m, 2, MidpointRounding.AwayFromZero)).ToArray();

    private static void Near(decimal[] expected, IReadOnlyList<decimal> rates, decimal tolerance = 0.011m)
    {
        var actual = rates.Select(r => r * 100m).ToArray();
        Assert.Equal(expected.Length, actual.Length);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.True(Math.Abs(expected[i] - actual[i]) <= tolerance, $"Year {i + 1}: expected {expected[i]}%, got {actual[i]:0.####}%");
        }

        Assert.Equal(1m, decimal.Round(rates.Sum(), 10));
    }

    private static readonly DateOnly Jan = new(2026, 1, 15);

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(20)]
    public void Half_year_GDS_uses_the_IRS_table_and_the_formula_agrees_with_it(int years)
    {
        var table = DepreciationCalculator.MacrsTables[years];
        var rates = TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, years, TaxConvention.HalfYear, Jan, 1);
        Assert.Equal(table, rates.Select(r => r * 100m).ToArray());

        // The statutory formula reproduces the published table to the hundredth of a percent.
        Near(table, TaxDepreciation.FormulaRates(TaxDepreciationMethod.MacrsGds, years, TaxConvention.HalfYear, Jan, 1));
    }

    [Theory]
    [InlineData(2, new[] { 35.00, 26.00, 15.60, 11.01, 11.01, 1.38 })] // Placed in service in quarter 1 (February).
    [InlineData(5, new[] { 25.00, 30.00, 18.00, 11.37, 11.37, 4.26 })]
    [InlineData(8, new[] { 15.00, 34.00, 20.40, 12.24, 11.30, 7.06 })]
    [InlineData(11, new[] { 5.00, 38.00, 22.80, 13.68, 10.94, 9.58 })]
    public void Mid_quarter_five_year_property_matches_the_IRS_tables(int month, double[] table) =>
        Near(table.Select(t => (decimal)t).ToArray(), TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, 5, TaxConvention.MidQuarter, new DateOnly(2026, month, 10), 1));

    [Fact]
    public void Mid_quarter_seven_year_property_matches_the_IRS_tables()
    {
        Near([25.00m, 21.43m, 15.31m, 10.93m, 8.75m, 8.74m, 8.75m, 1.09m], TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, 7, TaxConvention.MidQuarter, new DateOnly(2026, 3, 1), 1));
        Near([3.57m, 27.55m, 19.68m, 14.06m, 10.04m, 8.73m, 8.73m, 7.64m], TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, 7, TaxConvention.MidQuarter, new DateOnly(2026, 12, 1), 1));
    }

    [Fact]
    public void Residential_rental_property_uses_mid_month_straight_line()
    {
        var rates = TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, 27.5m, TaxConvention.MidMonth, new DateOnly(2026, 1, 5), 1);
        Assert.Equal(3.485m, decimal.Round(rates[0] * 100, 3));
        Assert.Equal(3.636m, decimal.Round(rates[1] * 100, 3));
        Assert.Equal(28, rates.Count); // 27.5 years from mid-January ends mid-July of year 28.
        Assert.Equal(1m, decimal.Round(rates.Sum(), 10));

        // Placed in service in December: half a month in year 1.
        var december = TaxDepreciation.Rates(TaxDepreciationMethod.MacrsGds, 27.5m, TaxConvention.MidMonth, new DateOnly(2026, 12, 5), 1);
        Assert.Equal(0.152m, decimal.Round(december[0] * 100, 3));
    }

    [Fact]
    public void ADS_and_straight_line_spread_evenly_with_half_years_at_each_end()
    {
        Assert.Equal([10m, 20m, 20m, 20m, 20m, 10m], Percent(TaxDepreciation.Rates(TaxDepreciationMethod.MacrsAds, 5, TaxConvention.HalfYear, Jan, 1)));
        Near([6.25m, 12.5m, 12.5m, 12.5m, 12.5m, 12.5m, 12.5m, 12.5m, 6.25m], TaxDepreciation.Rates(TaxDepreciationMethod.StraightLine, 8, TaxConvention.HalfYear, Jan, 1));

        // 150% DB (the AMT method) for 7-year property, half-year: the IRS 150% table.
        Near([10.71m, 19.13m, 15.03m, 12.25m, 12.25m, 12.25m, 12.25m, 6.13m], TaxDepreciation.Rates(TaxDepreciationMethod.Macrs150Db, 7, TaxConvention.HalfYear, Jan, 1));
    }

    [Fact]
    public void Section_179_comes_off_first_then_bonus_on_the_rest_and_MACRS_on_what_is_left()
    {
        var s = TaxDepreciation.Schedule(new TaxBookTerms(100_000m, Jan, TaxDepreciationMethod.MacrsGds, 5, TaxConvention.HalfYear, Section179: 20_000m, BonusPercent: 60m));
        Assert.Equal(32_000m, s.DepreciableBasis); // 100,000 − 20,000 − 60% × 80,000.
        var first = s.Years[0];
        Assert.Equal((2026, 20_000m, 48_000m, 6_400m, 74_400m), (first.TaxYear, first.Section179, first.Bonus, first.Depreciation, first.Total));
        Assert.Equal(25_600m, first.EndingBasis);
        Assert.Equal([6_400m, 10_240m, 6_144m, 3_686.40m, 3_686.40m, 1_843.20m], s.Years.Select(y => y.Depreciation).ToArray());
        Assert.Equal(100_000m, s.Years.Sum(y => y.Total));
        Assert.Equal(0m, s.Years[^1].EndingBasis);

        // 100% bonus leaves nothing for MACRS.
        var full = TaxDepreciation.Schedule(new TaxBookTerms(10_000m, Jan, TaxDepreciationMethod.MacrsGds, 7, TaxConvention.HalfYear, BonusPercent: 100m));
        Assert.Equal(10_000m, full.Years[0].Total);
        Assert.All(full.Years.Skip(1), y => Assert.Equal(0m, y.Total));
    }

    [Fact]
    public void A_schedule_adds_up_to_the_cent_with_odd_amounts()
    {
        var s = TaxDepreciation.Schedule(new TaxBookTerms(12_345.67m, new DateOnly(2026, 11, 3), TaxDepreciationMethod.MacrsGds, 7, TaxConvention.MidQuarter));
        Assert.Equal(12_345.67m, s.Years.Sum(y => y.Total));
        Assert.All(s.Years, y => Assert.Equal(decimal.Round(y.Depreciation, 2), y.Depreciation));
    }

    [Fact]
    public void A_disposal_takes_the_conventions_share_of_that_year_and_none_in_the_first_year()
    {
        var terms = new TaxBookTerms(10_000m, new DateOnly(2024, 3, 1), TaxDepreciationMethod.MacrsGds, 5, TaxConvention.HalfYear);

        // Year 3 (2026) is 19.20% for a full year; half-year convention on disposal gives half: 960.
        var s = TaxDepreciation.Schedule(terms, new DateOnly(2026, 6, 30));
        Assert.Equal([2_000m, 3_200m, 960m], s.Years.Select(y => y.Total).ToArray());
        Assert.Equal(3_840m, s.BasisAfter(2026));

        // Mid-quarter: disposed in quarter 2 of year 3 → 3/8 (37.5%) of that year's amount.
        var mq = TaxDepreciation.Schedule(terms with { Convention = TaxConvention.MidQuarter }, new DateOnly(2026, 5, 10));
        var full = TaxDepreciation.Schedule(terms with { Convention = TaxConvention.MidQuarter });
        Assert.Equal(decimal.Round(full.Years[2].Depreciation * 0.375m, 2, MidpointRounding.AwayFromZero), mq.Years[2].Depreciation);

        // Disposed in the year placed in service: no depreciation, Section 179 or bonus.
        var same = TaxDepreciation.Schedule(terms with { Section179 = 1_000m, BonusPercent = 40m }, new DateOnly(2024, 9, 1));
        Assert.Equal(0m, Assert.Single(same.Years).Total);
        Assert.Equal(10_000m, same.BasisAfter(2024));
    }

    [Fact]
    public void Tax_years_follow_the_fiscal_year()
    {
        // October fiscal year: November is month 2 and quarter 1; August is quarter 4 of the year that began the October before.
        Assert.Equal((2026, 2, 1), (TaxDepreciation.TaxYearOf(new DateOnly(2026, 11, 2), 10), TaxDepreciation.MonthOfTaxYear(new DateOnly(2026, 11, 2), 10), TaxDepreciation.QuarterOfTaxYear(new DateOnly(2026, 11, 2), 10)));
        Assert.Equal((2026, 4), (TaxDepreciation.TaxYearOf(new DateOnly(2027, 8, 20), 10), TaxDepreciation.QuarterOfTaxYear(new DateOnly(2027, 8, 20), 10)));
        Assert.Equal(0.875m, TaxDepreciation.FirstYearFraction(TaxConvention.MidQuarter, new DateOnly(2026, 11, 2), 10));
    }

    [Fact]
    public void The_mid_quarter_test_is_more_than_40_percent_in_the_last_quarter()
    {
        TaxBookTerms T(decimal basis, int month, decimal s179 = 0, decimal years = 5, TaxConvention c = TaxConvention.HalfYear) =>
            new(basis, new DateOnly(2026, month, 1), years == 39m ? TaxDepreciationMethod.MacrsGds : TaxDepreciationMethod.MacrsGds, years, years == 39m ? TaxConvention.MidMonth : c, s179);

        var required = TaxDepreciation.MidQuarterTest(2026, 1, [(T(10_000m, 2), null), (T(10_000m, 5), null), (T(25_000m, 11), null)]);
        Assert.Equal(new[] { 10_000m, 10_000m, 0m, 25_000m }, required.BasisByQuarter);
        Assert.True(required.MidQuarterRequired);
        Assert.Equal(0.5556m, required.LastQuarterShare);

        // Exactly 40% isn't more than 40%; real property and same-year disposals are left out; Section 179 reduces the basis.
        var notRequired = TaxDepreciation.MidQuarterTest(2026, 1,
        [
            (T(30_000m, 2), null),
            (T(20_000m, 12), null),
            (T(500_000m, 12, years: 39m), null),
            (T(90_000m, 10), new DateOnly(2026, 12, 20)),
            (T(15_000m, 10, s179: 15_000m), null),
        ]);
        Assert.Equal(50_000m, notRequired.Total);
        Assert.False(notRequired.MidQuarterRequired);
    }

    [Fact]
    public void Combinations_MACRS_does_not_allow_are_refused()
    {
        var ok = new TaxBookTerms(1_000m, Jan, TaxDepreciationMethod.MacrsGds, 5, TaxConvention.HalfYear);
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { RecoveryYears = 6 }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { Convention = TaxConvention.MidMonth }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { RecoveryYears = 27.5m }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { Section179 = 1_500m }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { BonusPercent = 120m }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { Method = TaxDepreciationMethod.MacrsAds, BonusPercent = 50m }));
        Assert.Throws<ArgumentException>(() => TaxDepreciation.Validate(ok with { RecoveryYears = 39m, Convention = TaxConvention.MidMonth, Section179 = 100m }));
        TaxDepreciation.Validate(ok with { Method = TaxDepreciationMethod.MacrsAds, RecoveryYears = 9.5m });
    }
}

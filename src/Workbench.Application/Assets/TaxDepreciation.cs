using Workbench.Domain.Assets;

namespace Workbench.Application.Assets;

/// <summary>What a tax book depreciates and how.</summary>
public sealed record TaxBookTerms(
    decimal Basis,
    DateOnly PlacedInService,
    TaxDepreciationMethod Method,
    decimal RecoveryYears,
    TaxConvention Convention,
    decimal Section179 = 0,
    decimal BonusPercent = 0,
    int TaxYearStartMonth = 1);

/// <summary>One tax year of a tax book: beginning adjusted basis, the first-year Section 179 and bonus, MACRS, and the ending adjusted basis.</summary>
public sealed record TaxYearAmount(
    int TaxYear,
    DateOnly Start,
    DateOnly End,
    decimal BeginningBasis,
    decimal Section179,
    decimal Bonus,
    decimal Depreciation,
    decimal EndingBasis)
{
    public decimal Total => Section179 + Bonus + Depreciation;
}

/// <summary>A tax book's schedule, cut off at a disposal when there was one.</summary>
public sealed record TaxSchedule(TaxBookTerms Terms, decimal DepreciableBasis, IReadOnlyList<TaxYearAmount> Years, DateOnly? DisposedOn)
{
    public decimal TotalThrough(int taxYear) => Years.Where(y => y.TaxYear <= taxYear).Sum(y => y.Total);

    public TaxYearAmount? For(int taxYear) => Years.FirstOrDefault(y => y.TaxYear == taxYear);

    /// <summary>Adjusted basis at the end of <paramref name="taxYear"/> (the full basis before the asset was placed in service).</summary>
    public decimal BasisAfter(int taxYear) => Terms.Basis - TotalThrough(taxYear);
}

/// <summary>The mid-quarter convention test for one tax year (IRC 168(d)(3)).</summary>
public sealed record MidQuarterTestResult(int TaxYear, decimal[] BasisByQuarter, decimal Total)
{
    public decimal LastQuarterShare => Total == 0 ? 0 : decimal.Round(BasisByQuarter[3] / Total, 4);

    /// <summary>More than 40% of the year's depreciable basis was placed in service in the last three months.</summary>
    public bool MidQuarterRequired => Total > 0 && BasisByQuarter[3] * 10 > Total * 4;
}

/// <summary>
/// MACRS tax depreciation (IRC 168, Rev. Proc. 87-57), computed per tax year:
/// <list type="bullet">
/// <item>Section 179 first, then bonus depreciation as a percentage of what's left, both in the year placed in service.
/// MACRS depreciates the rest.</item>
/// <item>GDS: 200% declining balance for 3, 5, 7 and 10 year property, 150% for 15 and 20 year, switching to straight line
/// when that's larger; straight line for 27.5 and 39 year real property. 150% DB, straight line and ADS are elections.</item>
/// <item>Conventions: half-year (half a year in the first year), mid-quarter (from the middle of the quarter placed in
/// service) and mid-month (from the middle of the month), with the matching fraction in the year of disposal.</item>
/// <item>GDS half-year uses the IRS table percentages (Rev. Proc. 87-57 table 1), which is how Form 4562 is usually
/// prepared. Every other combination uses the formula the tables come from, so it can differ from a table by cents.</item>
/// <item>Property disposed of in the year it was placed in service gets no depreciation, Section 179 or bonus. In a later
/// year of disposal it gets the convention's share of that year (in the schedule's last, already partial year: all of it).</item>
/// </list>
/// Dollar amounts round to the cent on the running total, so a schedule adds up to the depreciable basis exactly.
/// </summary>
public static class TaxDepreciation
{
    /// <summary>GDS recovery periods: personal property (3–20) and real property (27.5 residential rental, 39 nonresidential).</summary>
    public static readonly decimal[] GdsPeriods = [3m, 5m, 7m, 10m, 15m, 20m, 27.5m, 39m];

    public static bool IsRealProperty(decimal recoveryYears) => recoveryYears is 27.5m or 39m;

    /// <summary>Throws <see cref="ArgumentException"/> for a combination MACRS doesn't allow.</summary>
    public static void Validate(TaxBookTerms t)
    {
        if (t.Basis < 0)
        {
            throw new ArgumentException("The tax basis can't be negative.");
        }

        if (t.TaxYearStartMonth is < 1 or > 12)
        {
            throw new ArgumentException("The tax year starts in a month from 1 to 12.");
        }

        switch (t.Method)
        {
            case TaxDepreciationMethod.MacrsGds when !GdsPeriods.Contains(t.RecoveryYears):
                throw new ArgumentException("MACRS GDS recovery periods are 3, 5, 7, 10, 15, 20, 27.5 or 39 years.");
            case TaxDepreciationMethod.Macrs150Db when t.RecoveryYears is not (3m or 5m or 7m or 10m or 15m or 20m):
                throw new ArgumentException("150% declining balance applies to 3, 5, 7, 10, 15 and 20 year property.");
            case TaxDepreciationMethod.StraightLine or TaxDepreciationMethod.MacrsAds when t.RecoveryYears is < 1 or > 50 || t.RecoveryYears * 2 != decimal.Truncate(t.RecoveryYears * 2):
                throw new ArgumentException("Straight-line and ADS recovery periods are 1 to 50 years, in half years.");
        }

        var real = IsRealProperty(t.RecoveryYears) && t.Method is TaxDepreciationMethod.MacrsGds;
        if (real && t.Convention != TaxConvention.MidMonth)
        {
            throw new ArgumentException("27.5 and 39 year real property uses the mid-month convention.");
        }

        if (t.Convention == TaxConvention.MidMonth && t.Method is TaxDepreciationMethod.MacrsGds or TaxDepreciationMethod.Macrs150Db && !real)
        {
            throw new ArgumentException("The mid-month convention is for real property (straight line); personal property uses half-year or mid-quarter.");
        }

        if (t.Section179 < 0 || t.Section179 > t.Basis)
        {
            throw new ArgumentException("Section 179 expense is between zero and the basis.");
        }

        if (t.BonusPercent is < 0 or > 100)
        {
            throw new ArgumentException("The bonus depreciation percentage is 0 to 100.");
        }

        if (t.Convention == TaxConvention.MidMonth && (t.Section179 > 0 || t.BonusPercent > 0))
        {
            throw new ArgumentException("Real property doesn't qualify for Section 179 or bonus depreciation.");
        }

        if (t.Method == TaxDepreciationMethod.MacrsAds && t.BonusPercent > 0)
        {
            throw new ArgumentException("Property depreciated under ADS doesn't qualify for bonus depreciation.");
        }
    }

    /// <summary>The first day of the tax year containing <paramref name="date"/>.</summary>
    public static DateOnly TaxYearStart(DateOnly date, int startMonth) =>
        new(date.Month >= startMonth ? date.Year : date.Year - 1, startMonth, 1);

    /// <summary>The tax year (the calendar year it begins in) containing <paramref name="date"/>.</summary>
    public static int TaxYearOf(DateOnly date, int startMonth) => TaxYearStart(date, startMonth).Year;

    /// <summary>The month of the tax year, 1–12.</summary>
    public static int MonthOfTaxYear(DateOnly date, int startMonth) => ((date.Month - startMonth + 12) % 12) + 1;

    /// <summary>The quarter of the tax year, 1–4.</summary>
    public static int QuarterOfTaxYear(DateOnly date, int startMonth) => (MonthOfTaxYear(date, startMonth) - 1) / 3 + 1;

    /// <summary>How much of a full year's depreciation the year placed in service gets.</summary>
    public static decimal FirstYearFraction(TaxConvention convention, DateOnly placed, int startMonth) => convention switch
    {
        TaxConvention.HalfYear => 0.5m,
        TaxConvention.MidQuarter => (12m - (3m * (QuarterOfTaxYear(placed, startMonth) - 1) + 1.5m)) / 12m,
        _ => (12m - MonthOfTaxYear(placed, startMonth) + 0.5m) / 12m,
    };

    /// <summary>How much of a full year's depreciation the year of disposal gets.</summary>
    public static decimal DisposalYearFraction(TaxConvention convention, DateOnly disposed, int startMonth) => convention switch
    {
        TaxConvention.HalfYear => 0.5m,
        TaxConvention.MidQuarter => (3m * (QuarterOfTaxYear(disposed, startMonth) - 1) + 1.5m) / 12m,
        _ => (MonthOfTaxYear(disposed, startMonth) - 1 + 0.5m) / 12m,
    };

    /// <summary>The declining-balance factor: 2 (200%), 1.5 (150%) or 0 (straight line).</summary>
    public static decimal Factor(TaxDepreciationMethod method, decimal recoveryYears) => method switch
    {
        TaxDepreciationMethod.MacrsGds when IsRealProperty(recoveryYears) => 0m,
        TaxDepreciationMethod.MacrsGds => recoveryYears <= 10 ? 2m : 1.5m,
        TaxDepreciationMethod.Macrs150Db => 1.5m,
        _ => 0m,
    };

    /// <summary>
    /// The share of the depreciable basis taken in each tax year from the one placed in service (fractions adding to 1):
    /// the IRS table for GDS half-year, otherwise declining balance on the remaining basis switching to straight line over
    /// the remaining recovery period when that's larger, with the convention's fraction in the first year.
    /// </summary>
    public static IReadOnlyList<decimal> Rates(TaxDepreciationMethod method, decimal recoveryYears, TaxConvention convention, DateOnly placed, int startMonth)
    {
        if (method == TaxDepreciationMethod.MacrsGds && convention == TaxConvention.HalfYear
            && recoveryYears == decimal.Truncate(recoveryYears) && DepreciationCalculator.MacrsTables.TryGetValue((int)recoveryYears, out var table))
        {
            return table.Select(p => p / 100m).ToList();
        }

        return FormulaRates(method, recoveryYears, convention, placed, startMonth);
    }

    /// <summary>The statutory formula behind the IRS tables (used for every combination except GDS half-year).</summary>
    public static IReadOnlyList<decimal> FormulaRates(TaxDepreciationMethod method, decimal recoveryYears, TaxConvention convention, DateOnly placed, int startMonth)
    {
        var factor = Factor(method, recoveryYears);
        var rate = factor / recoveryYears;
        var result = new List<decimal>();
        decimal remaining = 1m, used = 0m;
        for (var year = 1; remaining > 0 && year < 100; year++)
        {
            var fraction = year == 1 ? FirstYearFraction(convention, placed, startMonth) : Math.Min(1m, recoveryYears - used);
            if (fraction <= 0)
            {
                break;
            }

            var lifeLeft = recoveryYears - used;
            var straight = remaining / lifeLeft * fraction;
            var declining = remaining * rate * fraction;
            var amount = fraction >= lifeLeft ? remaining : Math.Min(remaining, Math.Max(straight, declining));
            result.Add(amount);
            remaining -= amount;
            used += fraction;
        }

        return result;
    }

    /// <summary>The book's schedule by tax year, through the year of <paramref name="disposedOn"/> when given.</summary>
    public static TaxSchedule Schedule(TaxBookTerms t, DateOnly? disposedOn = null)
    {
        Validate(t);
        var section179 = Round(t.Section179);
        var bonus = Round((t.Basis - section179) * t.BonusPercent / 100m);
        var depreciable = t.Basis - section179 - bonus;
        var firstYear = TaxYearOf(t.PlacedInService, t.TaxYearStartMonth);
        var disposalYear = disposedOn is { } d ? TaxYearOf(d, t.TaxYearStartMonth) : (int?)null;
        var rates = Rates(t.Method, t.RecoveryYears, t.Convention, t.PlacedInService, t.TaxYearStartMonth);
        var years = new List<TaxYearAmount>();
        if (disposalYear == firstYear)
        {
            // Disposed of in the year placed in service: nothing is allowed.
            var start = TaxYearStart(t.PlacedInService, t.TaxYearStartMonth);
            years.Add(new TaxYearAmount(firstYear, start, start.AddYears(1).AddDays(-1), t.Basis, 0, 0, 0, t.Basis));
            return new TaxSchedule(t, t.Basis, years, disposedOn);
        }

        decimal cumulativeRate = 0, taken = 0, basis = t.Basis;
        for (var i = 0; i < rates.Count; i++)
        {
            var year = firstYear + i;
            cumulativeRate += rates[i];
            var target = i == rates.Count - 1 ? depreciable : Round(depreciable * cumulativeRate);
            var amount = target - taken;
            if (disposalYear == year && i < rates.Count - 1)
            {
                // A full year's amount times the convention's share; the schedule's last (already partial) year is taken whole.
                amount = Round(amount * DisposalYearFraction(t.Convention, disposedOn!.Value, t.TaxYearStartMonth));
            }

            taken += amount;
            var start = new DateOnly(year, t.TaxYearStartMonth, 1);
            var first = i == 0;
            var row = new TaxYearAmount(year, start, start.AddYears(1).AddDays(-1), basis, first ? section179 : 0, first ? bonus : 0, amount, 0);
            basis -= row.Total;
            years.Add(row with { EndingBasis = basis });
            if (disposalYear == year)
            {
                break;
            }
        }

        return new TaxSchedule(t, depreciable, years, disposedOn);
    }

    /// <summary>
    /// The mid-quarter test over the year's personal property: basis after Section 179 (bonus doesn't reduce it), leaving
    /// out real property and property disposed of in the same year.
    /// </summary>
    public static MidQuarterTestResult MidQuarterTest(int taxYear, int startMonth, IEnumerable<(TaxBookTerms Terms, DateOnly? DisposedOn)> books)
    {
        var quarters = new decimal[4];
        foreach (var (terms, disposed) in books)
        {
            if (TaxYearOf(terms.PlacedInService, startMonth) != taxYear || terms.Convention == TaxConvention.MidMonth
                || (IsRealProperty(terms.RecoveryYears) && terms.Method == TaxDepreciationMethod.MacrsGds)
                || (disposed is { } d && TaxYearOf(d, startMonth) == taxYear))
            {
                continue;
            }

            quarters[QuarterOfTaxYear(terms.PlacedInService, startMonth) - 1] += terms.Basis - terms.Section179;
        }

        return new MidQuarterTestResult(taxYear, quarters, quarters.Sum());
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

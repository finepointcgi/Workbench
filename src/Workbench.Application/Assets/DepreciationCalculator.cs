using Workbench.Domain.Assets;

namespace Workbench.Application.Assets;

/// <summary>
/// Book depreciation schedules, monthly, with the full-month convention (a whole month in the in-service month). Amounts
/// are rounded to the cent and the last month of each year (and of the life) absorbs the rounding, so the schedule sums
/// exactly to cost less salvage.
/// <list type="bullet">
/// <item>Straight line: (cost − salvage) / (life × 12) per month.</item>
/// <item>Declining balance: 200% of the straight-line rate on the opening book value of each year of life, switching to
/// straight line over the remaining life when that's larger, never below salvage; each year's amount is spread evenly
/// over its 12 months.</item>
/// <item>Sum-of-years digits: (cost − salvage) × (remaining life ÷ sum of the years' digits) per year, spread evenly.</item>
/// </list>
/// Other conventions (<see cref="WithConvention"/>): mid-month takes half a month in the in-service month and the other
/// half after the last month of life; following-month starts the month after the in-service month.
/// MACRS (tax book) uses the IRS half-year convention tables and is never posted.
/// </summary>
public static class DepreciationCalculator
{
    public static IReadOnlyList<decimal> MonthlySchedule(decimal cost, decimal salvage, int lifeYears, DepreciationMethod method)
    {
        if (lifeYears <= 0)
        {
            throw new ArgumentException("The useful life must be at least one year.");
        }

        var depreciable = Math.Max(0, cost - salvage);
        var months = lifeYears * 12;
        var schedule = new List<decimal>(months);
        if (method == DepreciationMethod.StraightLine)
        {
            var monthly = Round(depreciable / months);
            for (var m = 0; m < months - 1; m++)
            {
                schedule.Add(monthly);
            }

            schedule.Add(depreciable - monthly * (months - 1));
            return schedule;
        }

        foreach (var annual in AnnualAmounts(cost, salvage, lifeYears, method))
        {
            var monthly = Round(annual / 12m);
            for (var m = 0; m < 11; m++)
            {
                schedule.Add(monthly);
            }

            schedule.Add(annual - monthly * 11);
        }

        return schedule;
    }

    /// <summary>The monthly schedule with <paramref name="convention"/> applied (month 0 is the in-service month).</summary>
    public static IReadOnlyList<decimal> MonthlySchedule(decimal cost, decimal salvage, int lifeYears, DepreciationMethod method, DepreciationConvention convention) =>
        WithConvention(MonthlySchedule(cost, salvage, lifeYears, method), convention);

    /// <summary>
    /// Shifts a full-month schedule for <paramref name="convention"/>. Following month: a zero month first. Mid-month: each
    /// month's cumulative total is the average of the full-month cumulative totals either side, so the first month gets
    /// half, one extra month at the end gets the last half, and the total is unchanged.
    /// </summary>
    public static IReadOnlyList<decimal> WithConvention(IReadOnlyList<decimal> fullMonth, DepreciationConvention convention)
    {
        switch (convention)
        {
            case DepreciationConvention.FollowingMonth:
                return [0m, .. fullMonth];
            case DepreciationConvention.MidMonth:
                var result = new List<decimal>(fullMonth.Count + 1);
                decimal previous = 0, cumulative = 0, taken = 0;
                for (var i = 0; i <= fullMonth.Count; i++)
                {
                    var next = i < fullMonth.Count ? cumulative + fullMonth[i] : cumulative;
                    var target = i < fullMonth.Count ? Round((previous + next) / 2m) : next;
                    result.Add(target - taken);
                    taken = target;
                    previous = cumulative = next;
                }

                return result;
            default:
                return fullMonth;
        }
    }

    /// <summary>
    /// The schedule of an asset brought over with an opening balance: nothing before <paramref name="firstMonth"/> except
    /// the opening accumulated depreciation as one amount in the month before it (so cumulative totals through any month
    /// from then on include it), then the original schedule's remaining months scaled so they add up to exactly what's
    /// left (cost − salvage − opening). When the opening balance matches the original schedule, that's the original schedule.
    /// Throws when something is left to depreciate but the life has run out.
    /// </summary>
    public static IReadOnlyList<decimal> OpeningBalanceSchedule(IReadOnlyList<decimal> original, int firstMonth, decimal opening, decimal depreciable)
    {
        if (opening < 0 || opening > depreciable)
        {
            throw new ArgumentException($"The accumulated depreciation ({opening:N2}) must be between zero and cost less salvage ({depreciable:N2}).");
        }

        if (firstMonth <= 0)
        {
            return opening == 0 ? original : throw new ArgumentException("There's accumulated depreciation, but the asset wasn't in service before the cut-over.");
        }

        var schedule = new List<decimal>(Math.Max(original.Count, firstMonth + 1));
        for (var m = 0; m < firstMonth; m++)
        {
            schedule.Add(m == firstMonth - 1 ? opening : 0);
        }

        var remaining = depreciable - opening;
        var tail = original.Skip(firstMonth).ToList();
        var tailTotal = tail.Sum();
        if (remaining == 0)
        {
            return schedule;
        }

        if (tailTotal <= 0)
        {
            throw new ArgumentException($"The useful life ended before the cut-over, but {remaining:N2} is still to depreciate; correct the life, salvage value or accumulated depreciation.");
        }

        decimal running = 0, taken = 0;
        foreach (var amount in tail)
        {
            running += amount;
            var target = running == tailTotal ? remaining : Round(remaining * running / tailTotal);
            schedule.Add(target - taken);
            taken = target;
        }

        return schedule;
    }

    /// <summary>Annual book amounts by year of life (declining balance and sum-of-years digits).</summary>
    public static IReadOnlyList<decimal> AnnualAmounts(decimal cost, decimal salvage, int lifeYears, DepreciationMethod method)
    {
        var depreciable = Math.Max(0, cost - salvage);
        var result = new List<decimal>(lifeYears);
        var taken = 0m;
        var digits = lifeYears * (lifeYears + 1) / 2m;
        for (var y = 1; y <= lifeYears; y++)
        {
            decimal amount;
            if (y == lifeYears)
            {
                amount = depreciable - taken;
            }
            else if (method == DepreciationMethod.SumOfYearsDigits)
            {
                amount = Round(depreciable * (lifeYears - y + 1) / digits);
            }
            else if (method == DepreciationMethod.DecliningBalance)
            {
                var book = cost - taken;
                var declining = Round(book * 2m / lifeYears);
                var straight = Round((book - salvage) / (lifeYears - y + 1));
                amount = Math.Min(Math.Max(declining, straight), Math.Max(0, book - salvage));
            }
            else
            {
                amount = Round(depreciable / lifeYears);
            }

            result.Add(amount);
            taken += amount;
        }

        return result;
    }

    /// <summary>Scheduled depreciation through month <paramref name="monthIndex"/> of life (0 = the in-service month), inclusive.</summary>
    public static decimal CumulativeThrough(IReadOnlyList<decimal> schedule, int monthIndex) =>
        monthIndex < 0 ? 0 : schedule.Take(Math.Min(monthIndex + 1, schedule.Count)).Sum();

    /// <summary>Months from the in-service month to <paramref name="date"/>'s month (0 in the in-service month).</summary>
    public static int MonthIndex(DateOnly inService, DateOnly date) => (date.Year * 12 + date.Month) - (inService.Year * 12 + inService.Month);

    /// <summary>IRS MACRS GDS percentages (half-year convention) by recovery period.</summary>
    public static IReadOnlyDictionary<int, decimal[]> MacrsTables { get; } = new Dictionary<int, decimal[]>
    {
        [3] = [33.33m, 44.45m, 14.81m, 7.41m],
        [5] = [20.00m, 32.00m, 19.20m, 11.52m, 11.52m, 5.76m],
        [7] = [14.29m, 24.49m, 17.49m, 12.49m, 8.93m, 8.92m, 8.93m, 4.46m],
        [10] = [10.00m, 18.00m, 14.40m, 11.52m, 9.22m, 7.37m, 6.55m, 6.55m, 6.56m, 6.55m, 3.28m],
        [15] = [5.00m, 9.50m, 8.55m, 7.70m, 6.93m, 6.23m, 5.90m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 5.90m, 5.91m, 2.95m],
        [20] = [3.750m, 7.219m, 6.677m, 6.177m, 5.713m, 5.285m, 4.888m, 4.522m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 4.462m, 4.461m, 2.231m],
    };

    /// <summary>Tax depreciation by tax year: cost (salvage is ignored for MACRS) × the table percentage; the last year takes the rounding.</summary>
    public static IReadOnlyList<decimal> Macrs(decimal cost, int recoveryYears)
    {
        if (!MacrsTables.TryGetValue(recoveryYears, out var table))
        {
            throw new ArgumentException("MACRS recovery periods are 3, 5, 7, 10, 15 or 20 years.");
        }

        var result = table.Take(table.Length - 1).Select(p => Round(cost * p / 100m)).ToList();
        result.Add(cost - result.Sum());
        return result;
    }

    private static decimal Round(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);
}

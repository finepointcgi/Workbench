using Workbench.Contracts.PerDiem;

namespace Workbench.Application.PerDiem;

/// <summary>The rate that applies on one day, with its M&amp;IE breakdown.</summary>
public sealed record PerDiemRateForDay(decimal LodgingMax, decimal MieRate, ParsedMieTier Tier, string Description, bool IsStandard, IReadOnlyList<string> Notes);

/// <summary>
/// Computes the per diem allowance for a trip, day by day (FTR chapter 301-11, JTR chapter 2, FAR 31.205-46):
/// <list type="bullet">
/// <item>Lodging is allowed for each night (every day but the last) up to the destination's maximum; the excess needs a
/// justification (allowable up to the cap, 300% by default) or is unallowable, by policy. Lodging taxes claimed
/// separately are allowable in full.</item>
/// <item>M&amp;IE follows where the traveler sleeps (the last day uses the last stop); the first and last travel day get 75%;
/// provided meals are deducted at their full value from the tier's breakdown; claims above the allowance are unallowable.</item>
/// </list>
/// </summary>
public static class PerDiemCalculator
{
    public const decimal TravelDayFactor = 0.75m;

    /// <summary>Throws <see cref="ArgumentException"/> unless the stops are in order and contiguous.</summary>
    public static void Validate(IReadOnlyList<TravelDestinationDto> stops)
    {
        if (stops.Count == 0)
        {
            throw new ArgumentException("Add at least one destination.");
        }

        for (var i = 0; i < stops.Count; i++)
        {
            var s = stops[i];
            if (string.IsNullOrWhiteSpace(s.Destination))
            {
                throw new ArgumentException($"Destination {i + 1} has no name.");
            }

            if (s.DepartOn < s.ArriveOn)
            {
                throw new ArgumentException($"{s.Destination}: the departure date is before the arrival date.");
            }

            if (i > 0 && s.ArriveOn != stops[i - 1].DepartOn)
            {
                throw new ArgumentException($"{s.Destination} must start on {stops[i - 1].DepartOn:yyyy-MM-dd}, the day the traveler leaves {stops[i - 1].Destination}.");
            }
        }

        if (stops[^1].DepartOn.DayNumber - stops[0].ArriveOn.DayNumber > 180)
        {
            throw new ArgumentException("A trip can't be longer than 180 days.");
        }
    }

    public static IReadOnlyList<PerDiemDayDto> Compute(
        IReadOnlyList<TravelDestinationDto> stops,
        IReadOnlyDictionary<DateOnly, TravelDayInput> claims,
        Func<TravelDestinationDto, DateOnly, PerDiemRateForDay> rateFor,
        bool lodgingTaxesSeparate,
        LodgingOveragePolicy policy,
        decimal justifiedCapPercent = 300m)
    {
        Validate(stops);
        var start = stops[0].ArriveOn;
        var end = stops[^1].DepartOn;
        var days = new List<PerDiemDayDto>();
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            var stop = date == end ? stops[^1] : stops.Last(s => s.ArriveOn <= date && date < s.DepartOn);
            var rate = rateFor(stop, date);
            var claim = claims.GetValueOrDefault(date) ?? new TravelDayInput(date);
            var notes = new List<string>(rate.Notes);

            var isFirst = date == start;
            var isLast = date == end;
            var factor = isFirst || isLast ? TravelDayFactor : 1m;
            var mieBase = factor == 1m ? rate.MieRate
                : rate.Tier.Total == rate.MieRate && rate.Tier.FirstLastDay > 0 ? rate.Tier.FirstLastDay : Money(rate.MieRate * factor);
            var deduction = (claim.BreakfastProvided ? rate.Tier.Breakfast : 0) + (claim.LunchProvided ? rate.Tier.Lunch : 0)
                + (claim.DinnerProvided ? rate.Tier.Dinner : 0);
            var mieAllowable = Math.Max(0, mieBase - deduction);
            var mieClaimed = Math.Max(0, claim.MieClaimed ?? mieAllowable);
            var mieUnallowable = Math.Max(0, mieClaimed - mieAllowable);
            if (mieUnallowable > 0)
            {
                notes.Add($"M&IE claimed is {mieUnallowable:C} more than allowed.");
            }

            var hasLodging = date < end;
            decimal lodgingClaimed = 0, taxClaimed = 0, lodgingAllowable = 0, taxAllowable = 0, overage = 0, justified = 0, lodgingUnallowable = 0;
            var status = PerDiemAmountStatus.Ok;
            if (hasLodging)
            {
                lodgingClaimed = Math.Max(0, claim.LodgingClaimed);
                taxClaimed = lodgingTaxesSeparate ? Math.Max(0, claim.LodgingTaxClaimed) : 0;
                taxAllowable = taxClaimed;
                lodgingAllowable = Math.Min(lodgingClaimed, rate.LodgingMax);
                overage = lodgingClaimed - lodgingAllowable;
                if (overage > 0)
                {
                    if (policy == LodgingOveragePolicy.Unallowable)
                    {
                        (lodgingUnallowable, status) = (overage, PerDiemAmountStatus.Unallowable);
                    }
                    else if (!string.IsNullOrWhiteSpace(claim.Justification))
                    {
                        var cap = Math.Max(0, Money(rate.LodgingMax * justifiedCapPercent / 100m) - rate.LodgingMax);
                        justified = Math.Min(overage, cap);
                        lodgingUnallowable = overage - justified;
                        status = lodgingUnallowable > 0 ? PerDiemAmountStatus.Unallowable : PerDiemAmountStatus.Justified;
                        if (lodgingUnallowable > 0)
                        {
                            notes.Add($"Lodging above {justifiedCapPercent:0}% of the maximum isn't allowable even with a justification.");
                        }
                    }
                    else
                    {
                        (lodgingUnallowable, status) = (overage, PerDiemAmountStatus.RequiresJustification);
                    }
                }
            }
            else if (claim.LodgingClaimed > 0 || claim.LodgingTaxClaimed > 0)
            {
                notes.Add("No lodging is allowed on the last day of travel; the amount is ignored.");
            }

            days.Add(new PerDiemDayDto(
                date, stop.Destination + (stop.State.Length > 0 ? $", {stop.State}" : stop.Country is "US" or "" ? "" : $", {stop.Country}"),
                rate.Description, rate.IsStandard, isFirst, isLast && end > start, hasLodging,
                rate.LodgingMax, rate.MieRate, factor, mieBase, deduction, mieAllowable, mieClaimed, mieUnallowable,
                lodgingClaimed, taxClaimed, lodgingAllowable, taxAllowable, overage, justified, lodgingUnallowable, status,
                claim.BreakfastProvided, claim.LunchProvided, claim.DinnerProvided, string.IsNullOrWhiteSpace(claim.Justification) ? null : claim.Justification.Trim(),
                notes));
        }

        return days;
    }

    public static PerDiemTotalsDto Totals(IReadOnlyList<PerDiemDayDto> days) => new(
        days.Sum(d => d.LodgingClaimed),
        days.Sum(d => d.LodgingAllowable),
        days.Sum(d => d.LodgingTaxAllowable),
        days.Sum(d => d.LodgingJustified),
        days.Sum(d => d.MieClaimed),
        days.Sum(d => d.MieAllowable),
        days.Sum(d => d.LodgingUnallowable + d.MieUnallowable),
        days.Sum(d => d.Claimed),
        days.Sum(d => d.Allowable));

    private static decimal Money(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

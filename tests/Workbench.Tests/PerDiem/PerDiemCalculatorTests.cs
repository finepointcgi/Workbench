using Workbench.Application.PerDiem;
using Workbench.Contracts.PerDiem;

namespace Workbench.Tests.PerDiem;

public sealed class PerDiemCalculatorTests
{
    // Washington, DC: lodging max $258, M&IE $92 (B $23, L $26, D $38, I $5; 75% = $69).
    private static readonly ParsedMieTier Tier92 = new(92m, 23m, 26m, 38m, 5m, 69m);

    private static PerDiemRateForDay Dc(TravelDestinationDto stop, DateOnly date) => new(258m, 92m, Tier92, "District of Columbia, DC FY2027", false, []);

    private static readonly DateOnly Mon = new(2026, 10, 19);

    private static readonly TravelDestinationDto DcTrip = new(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(2));

    [Fact]
    public void Three_day_trip_matches_the_hand_computation()
    {
        // Night 1: $300 claimed (over the $258 max by $42, no justification); night 2: $250. Taxes $40 and $33.
        // Lunch is provided on day 2.
        var claims = new Dictionary<DateOnly, TravelDayInput>
        {
            [Mon] = new(Mon, LodgingClaimed: 300m, LodgingTaxClaimed: 40m),
            [Mon.AddDays(1)] = new(Mon.AddDays(1), LodgingClaimed: 250m, LodgingTaxClaimed: 33m, LunchProvided: true),
            [Mon.AddDays(2)] = new(Mon.AddDays(2)),
        };

        var days = PerDiemCalculator.Compute([DcTrip], claims, Dc, lodgingTaxesSeparate: true, LodgingOveragePolicy.RequiresJustification);

        Assert.Equal(3, days.Count);

        // Day 1 (first travel day): 75% of $92 = $69; lodging allowed $258, $42 needs a justification.
        Assert.Equal((0.75m, 69m, 0m, 69m), (days[0].MieFactor, days[0].MieBase, days[0].MealDeduction, days[0].MieAllowable));
        Assert.Equal((258m, 42m, 42m, PerDiemAmountStatus.RequiresJustification), (days[0].LodgingAllowable, days[0].LodgingOverage, days[0].LodgingUnallowable, days[0].LodgingStatus));
        Assert.Equal(40m, days[0].LodgingTaxAllowable);

        // Day 2: full $92 less the $26 lunch = $66; lodging within the max.
        Assert.Equal((1m, 92m, 26m, 66m), (days[1].MieFactor, days[1].MieBase, days[1].MealDeduction, days[1].MieAllowable));
        Assert.Equal((250m, 0m, PerDiemAmountStatus.Ok), (days[1].LodgingAllowable, days[1].LodgingOverage, days[1].LodgingStatus));

        // Day 3 (last travel day): $69, no lodging.
        Assert.Equal((0.75m, 69m, false), (days[2].MieFactor, days[2].MieAllowable, days[2].HasLodging));

        var totals = PerDiemCalculator.Totals(days);
        Assert.Equal(204m, totals.MieAllowable); // 69 + 66 + 69
        Assert.Equal(508m, totals.LodgingAllowable); // 258 + 250
        Assert.Equal(73m, totals.LodgingTax);
        Assert.Equal(42m, totals.Unallowable);
        Assert.Equal(785m, totals.Allowable); // 508 + 73 + 204
        Assert.Equal(827m, totals.Claimed); // 300 + 40 + 250 + 33 + 204
    }

    [Fact]
    public void Justified_overage_is_allowable_up_to_300_percent_and_the_policy_can_make_it_unallowable()
    {
        var claims = new Dictionary<DateOnly, TravelDayInput>
        {
            [Mon] = new(Mon, LodgingClaimed: 300m, Justification: "Only hotel near the site"),
            [Mon.AddDays(1)] = new(Mon.AddDays(1), LodgingClaimed: 900m, Justification: "Event week"),
        };

        var justified = PerDiemCalculator.Compute([DcTrip], claims, Dc, true, LodgingOveragePolicy.RequiresJustification);
        Assert.Equal((42m, 0m, PerDiemAmountStatus.Justified), (justified[0].LodgingJustified, justified[0].LodgingUnallowable, justified[0].LodgingStatus));

        // 300% of $258 = $774 at most: $516 justified above the max, $126 still unallowable.
        Assert.Equal((516m, 126m, PerDiemAmountStatus.Unallowable), (justified[1].LodgingJustified, justified[1].LodgingUnallowable, justified[1].LodgingStatus));

        var strict = PerDiemCalculator.Compute([DcTrip], claims, Dc, true, LodgingOveragePolicy.Unallowable);
        Assert.Equal((0m, 42m, PerDiemAmountStatus.Unallowable), (strict[0].LodgingJustified, strict[0].LodgingUnallowable, strict[0].LodgingStatus));
    }

    [Fact]
    public void Taxes_counted_against_the_max_when_not_separate_and_mie_claims_above_the_allowance_are_unallowable()
    {
        var claims = new Dictionary<DateOnly, TravelDayInput>
        {
            [Mon] = new(Mon, LodgingClaimed: 280m, LodgingTaxClaimed: 30m, MieClaimed: 92m),
        };

        var days = PerDiemCalculator.Compute([DcTrip], claims, Dc, lodgingTaxesSeparate: false, LodgingOveragePolicy.Unallowable);

        Assert.Equal((0m, 258m, 22m), (days[0].LodgingTaxAllowable, days[0].LodgingAllowable, days[0].LodgingUnallowable));
        Assert.Equal((69m, 92m, 23m), (days[0].MieAllowable, days[0].MieClaimed, days[0].MieUnallowable));
    }

    [Fact]
    public void Multi_city_trip_uses_where_the_traveler_sleeps_and_the_last_stop_on_the_last_day()
    {
        var huntsville = new ParsedMieTier(74m, 18m, 20m, 31m, 5m, 55.5m);
        PerDiemRateForDay Rate(TravelDestinationDto stop, DateOnly date) =>
            stop.Destination == "Huntsville" ? new(121m, 74m, huntsville, "Huntsville", false, []) : Dc(stop, date);
        TravelDestinationDto[] stops =
        [
            new(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(1)),
            new(PerDiemTable.Conus, "US", "AL", "Huntsville", Mon.AddDays(1), Mon.AddDays(3)),
        ];

        var days = PerDiemCalculator.Compute(stops, new Dictionary<DateOnly, TravelDayInput>(), Rate, true, LodgingOveragePolicy.RequiresJustification);

        Assert.Equal([258m, 121m, 121m, 121m], days.Select(d => d.LodgingMax));
        Assert.Equal([69m, 74m, 74m, 55.5m], days.Select(d => d.MieAllowable));
    }

    [Fact]
    public void Stops_must_be_contiguous()
    {
        TravelDestinationDto[] stops =
        [
            new(PerDiemTable.Conus, "US", "DC", "District of Columbia", Mon, Mon.AddDays(1)),
            new(PerDiemTable.Conus, "US", "AL", "Huntsville", Mon.AddDays(2), Mon.AddDays(3)),
        ];

        Assert.Throws<ArgumentException>(() => PerDiemCalculator.Validate(stops));
    }
}

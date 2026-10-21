using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.PerDiem;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Receipts;
using Workbench.Domain.PerDiem;
using Workbench.Domain.Travel;

namespace Workbench.Application.Receipts;

/// <summary>
/// Compares a stay's nightly room rate with the GSA lodging maximum (the per diem tables the app already uses, through
/// <see cref="PerDiemTables.Resolve"/>). Where: the expense report's per diem destinations, else its travel authorization's,
/// when one covers the night; else the folio's city and state (the destination itself, or a destination whose county list
/// names the city, like Arlington, VA under Washington DC); else the standard CONUS rate.
/// </summary>
public static class LodgingRateChecker
{
    private static readonly Dictionary<string, string> StateNames = new(StringComparer.Ordinal)
    {
        ["VA"] = "Virginia", ["MD"] = "Maryland", ["DC"] = "District of Columbia", ["CA"] = "California", ["AL"] = "Alabama",
        ["TX"] = "Texas", ["FL"] = "Florida", ["NY"] = "New York", ["NJ"] = "New Jersey", ["PA"] = "Pennsylvania", ["CO"] = "Colorado",
        ["WA"] = "Washington", ["MA"] = "Massachusetts", ["GA"] = "Georgia", ["OH"] = "Ohio", ["IL"] = "Illinois", ["AZ"] = "Arizona",
        ["NC"] = "North Carolina", ["SC"] = "South Carolina", ["TN"] = "Tennessee", ["MO"] = "Missouri", ["NV"] = "Nevada", ["HI"] = "Hawaii",
    };

    /// <summary>The nights to check: each room charge with its date, else <paramref name="nights"/> nights from check-in at the nightly rate.</summary>
    public static IReadOnlyList<(DateOnly Date, decimal Rate)> NightsOf(ReceiptExtractionDto extraction)
    {
        var dated = extraction.Charges.Where(c => c is { Kind: ReceiptChargeKind.Room, Date: not null, Amount: > 0 })
            .GroupBy(c => c.Date!.Value).Select(g => (g.Key, g.Sum(c => c.Amount))).OrderBy(n => n.Key).ToList();
        if (dated.Count > 0 && (extraction.Nights is null || dated.Count == extraction.Nights))
        {
            return dated;
        }

        return extraction is { CheckIn: { } start, Nights: { } count and > 0, NightlyRate: { } rate }
            ? Enumerable.Range(0, count).Select(i => (start.AddDays(i), rate)).ToList()
            : [];
    }

    public static async Task<LodgingRateCheckDto?> CheckAsync(
        IWorkbenchDbContext db,
        IReadOnlyList<(DateOnly Date, decimal Rate)> nights,
        string? city,
        string? state,
        Guid? expenseReportId,
        LodgingOveragePolicy policy,
        CancellationToken ct)
    {
        if (nights.Count == 0)
        {
            return null;
        }

        var tables = await PerDiemTables.LoadAsync(db, ct);
        var tripStops = await TripStopsAsync(db, expenseReportId, ct);
        var notes = new List<string>();
        var cityStop = CityStop(tables, city, state, nights[0].Date);
        var checks = new List<LodgingNightCheckDto>();
        var descriptions = new List<string>();
        var standard = false;
        foreach (var (date, rate) in nights)
        {
            var stop = tripStops.FirstOrDefault(s => s.ArriveOn <= date && date < s.DepartOn) ?? cityStop ?? Standard(date);
            var resolved = tables.Resolve(stop with { ArriveOn = date, DepartOn = date.AddDays(1) }, date);
            standard |= resolved.IsStandard;
            checks.Add(new LodgingNightCheckDto(date, rate, resolved.LodgingMax));
            if (!descriptions.Contains(resolved.Description))
            {
                descriptions.Add(resolved.Description);
            }

            foreach (var note in resolved.Notes.Where(n => !notes.Contains(n)))
            {
                notes.Add(note);
            }
        }

        var where = tripStops.Count > 0 && nights.All(n => tripStops.Any(s => s.ArriveOn <= n.Date && n.Date < s.DepartOn))
            ? string.Join(", ", tripStops.Select(Label).Distinct()) + " (from the trip)"
            : cityStop is not null ? Label(cityStop)
            : city is not null ? $"{city}{(state is null ? "" : ", " + state)} (no GSA rate for this city; standard CONUS rate)"
            : "Standard CONUS rate (no location on the receipt)";
        if (checks.Any(c => c.IsOver))
        {
            var over = checks.Where(c => c.IsOver).ToList();
            notes.Insert(0, string.Create(CultureInfo.InvariantCulture,
                $"{over.Count} of {checks.Count} night{(checks.Count == 1 ? "" : "s")} {(over.Count == 1 ? "is" : "are")} over the GSA lodging maximum, by {over.Sum(c => c.Over):C} in all. ")
                + (policy == LodgingOveragePolicy.Unallowable
                    ? "Lodging over the maximum is unallowable under your organization's policy."
                    : "Explain why on the line (for example, the conference hotel or no lodging at the rate), or it may be disallowed."));
        }

        return new LodgingRateCheckDto(where, string.Join("; ", descriptions), standard, checks, policy == LodgingOveragePolicy.Unallowable, notes);
    }

    private static async Task<IReadOnlyList<TravelDestinationDto>> TripStopsAsync(IWorkbenchDbContext db, Guid? reportId, CancellationToken ct)
    {
        if (reportId is not { } id)
        {
            return [];
        }

        var perDiem = await db.Set<TravelDestination>().AsNoTracking()
            .Join(db.Set<TravelPerDiem>().Where(p => p.ExpenseReportId == id), d => d.TravelPerDiemId, p => p.Id, (d, _) => d)
            .OrderBy(d => d.Order).ToListAsync(ct);
        if (perDiem.Count > 0)
        {
            return perDiem.Select(d => new TravelDestinationDto((PerDiemTable)(int)d.Kind, d.Country, d.State, d.Destination, d.ArriveOn, d.DepartOn)).ToList();
        }

        var trip = await db.Set<TravelAuthorizationDestination>().AsNoTracking()
            .Join(db.Set<TravelAuthorization>().Where(t => t.ExpenseReportId == id), d => d.TravelAuthorizationId, t => t.Id, (d, _) => d)
            .OrderBy(d => d.Order).ToListAsync(ct);
        return trip.Select(d => new TravelDestinationDto((PerDiemTable)(int)d.Kind, d.Country, d.State, d.Destination, d.ArriveOn, d.DepartOn)).ToList();
    }

    /// <summary>The GSA destination for a folio's city: the destination itself, or the one whose county list names the city in that state.</summary>
    private static TravelDestinationDto? CityStop(PerDiemTables tables, string? city, string? state, DateOnly date)
    {
        if (string.IsNullOrWhiteSpace(city))
        {
            return null;
        }

        var rates = tables.RatesInForce(PerDiemTables.FiscalYearOf(date), PerDiemRateKind.Conus).ToList();
        if (rates.Count == 0)
        {
            rates = tables.RatesInForce(kind: PerDiemRateKind.Conus).ToList();
        }

        bool Same(string a, string? b) => string.Equals(a.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
        var exact = rates.FirstOrDefault(r => !r.IsStandard && Same(r.Destination, city) && (state is null || Same(r.State, state)));
        var stateName = state is null ? null : StateNames.GetValueOrDefault(state);
        var county = exact ?? rates.FirstOrDefault(r => !r.IsStandard && r.County is { } c && Names(c, city)
            && (state is null || Same(r.State, state) || (stateName is not null && c.Contains(stateName, StringComparison.OrdinalIgnoreCase))));
        return county is null ? null : new TravelDestinationDto(PerDiemTable.Conus, county.Country, county.State, county.Destination, date, date.AddDays(1));
    }

    private static bool Names(string countyList, string city)
    {
        var index = countyList.IndexOf(city, StringComparison.OrdinalIgnoreCase);
        return index >= 0
            && (index == 0 || !char.IsLetter(countyList[index - 1]))
            && (index + city.Length == countyList.Length || !char.IsLetter(countyList[index + city.Length]));
    }

    private static TravelDestinationDto Standard(DateOnly date) => new(PerDiemTable.Conus, "US", "", "Standard CONUS", date, date.AddDays(1));

    private static string Label(TravelDestinationDto stop) => stop.State.Length == 0 ? stop.Destination : $"{stop.Destination}, {stop.State}";
}

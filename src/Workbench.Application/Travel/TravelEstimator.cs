using Microsoft.EntityFrameworkCore;
using Workbench.Application.PerDiem;
using Workbench.Contracts.PerDiem;
using Workbench.Contracts.Travel;
using Workbench.Domain.PerDiem;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

/// <summary>Travel policy, bound from config section <c>Travel</c>.</summary>
public sealed class TravelOptions
{
    public const string Section = "Travel";

    /// <summary>A category (or the trip) more than this percent over its authorized amount is flagged (default 10).</summary>
    public decimal TolerancePercent { get; set; } = 10m;
}

/// <summary>
/// Prices a trip from the per diem tables: the GSA lodging maximum for each night and the M&amp;IE allowance for each day
/// (75% on the first and last day), through the same <see cref="PerDiemCalculator"/> the expense report uses.
/// </summary>
public static class TravelEstimator
{
    public static TravelEstimateDto Estimate(PerDiemTables tables, IReadOnlyList<TravelDestinationDto> stops, decimal airfare, decimal rentalCar, decimal other)
    {
        var days = PerDiemCalculator.Compute(stops, new Dictionary<DateOnly, TravelDayInput>(), tables.Resolve, true, LodgingOveragePolicy.RequiresJustification);
        var estimateDays = days.Select(d => new TravelEstimateDayDto(d.Date, d.Location, d.RateDescription, d.HasLodging, d.HasLodging ? d.LodgingMax : 0, d.MieAllowable)).ToList();
        var lodging = estimateDays.Sum(d => d.LodgingMax);
        var mie = estimateDays.Sum(d => d.MieAllowance);
        var notes = days.SelectMany(d => d.Notes).Distinct().ToList();
        return new TravelEstimateDto(airfare, lodging, mie, rentalCar, other, airfare + lodging + mie + rentalCar + other,
            estimateDays.Count(d => d.HasLodging), estimateDays, notes);
    }

    public static async Task<TravelEstimateDto> EstimateAsync(IWorkbenchDbContext db, IReadOnlyList<TravelDestinationDto> stops, decimal airfare, decimal rentalCar, decimal other, CancellationToken ct) =>
        Estimate(await PerDiemTables.LoadAsync(db, ct), stops, airfare, rentalCar, other);

    public static IReadOnlyList<TravelDestinationDto> Stops(TravelAuthorization ta) =>
        ta.Destinations.OrderBy(d => d.Order)
            .Select(d => new TravelDestinationDto((PerDiemTable)(int)d.Kind, d.Country, d.State, d.Destination, d.ArriveOn, d.DepartOn))
            .ToList();

    public static string Summary(IEnumerable<TravelAuthorizationDestination> stops) =>
        string.Join(" → ", stops.OrderBy(d => d.Order).Select(d => d.Destination + (d.State.Length > 0 ? $", {d.State}" : d.Country is "US" or "" ? "" : $", {d.Country}")));

    /// <summary>Loads an authorization with its destinations.</summary>
    public static Task<TravelAuthorization?> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct, bool tracking = true)
    {
        var query = db.Set<TravelAuthorization>().Include(t => t.Destinations).AsQueryable();
        return (tracking ? query : query.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct);
    }
}

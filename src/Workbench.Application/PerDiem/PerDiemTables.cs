using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;

namespace Workbench.Application.PerDiem;

/// <summary>
/// The per diem tables in force (the latest batch per fiscal year and kind), loaded once per operation, and the rules for
/// picking a rate: the destination's season containing the date, else the standard CONUS rate, else (no table at all for
/// the year) the latest earlier year's table, else the built-in standard rate.
/// </summary>
public sealed class PerDiemTables
{
    private readonly Dictionary<(int Year, PerDiemRateKind Kind), PerDiemRateBatch> _batches;
    private readonly ILookup<Guid, PerDiemRate> _rates;
    private readonly ILookup<Guid, PerDiemMieTier> _tiers;

    private PerDiemTables(IEnumerable<PerDiemRateBatch> batches, IEnumerable<PerDiemRate> rates, IEnumerable<PerDiemMieTier> tiers)
    {
        _batches = batches.ToDictionary(b => (b.FiscalYear, b.Kind));
        _rates = rates.ToLookup(r => r.BatchId);
        _tiers = tiers.ToLookup(t => t.BatchId);
    }

    public IReadOnlyCollection<PerDiemRateBatch> Batches => _batches.Values;

    public static int FiscalYearOf(DateOnly date) => date.Month >= 10 ? date.Year + 1 : date.Year;

    public static async Task<PerDiemTables> LoadAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var all = await db.Set<PerDiemRateBatch>().AsNoTracking().ToListAsync(ct);
        var current = all.GroupBy(b => (b.FiscalYear, b.Kind))
            .Select(g => g.OrderByDescending(b => b.ImportedAtUtc).ThenByDescending(b => b.Id).First())
            .ToList();
        var ids = current.Select(b => b.Id).ToList();
        var rates = await db.Set<PerDiemRate>().AsNoTracking().Where(r => ids.Contains(r.BatchId)).ToListAsync(ct);
        var tiers = await db.Set<PerDiemMieTier>().AsNoTracking().Where(t => ids.Contains(t.BatchId)).ToListAsync(ct);
        return new PerDiemTables(current, rates, tiers);
    }

    public IEnumerable<PerDiemRate> RatesInForce(int? fiscalYear = null, PerDiemRateKind? kind = null) =>
        _batches.Values
            .Where(b => (fiscalYear is null || b.FiscalYear == fiscalYear) && (kind is null || b.Kind == kind))
            .SelectMany(b => _rates[b.Id]);

    public IEnumerable<PerDiemMieTier> TiersInForce(int? fiscalYear = null) =>
        _batches.Values.Where(b => fiscalYear is null || b.FiscalYear == fiscalYear).SelectMany(b => _tiers[b.Id]);

    public PerDiemRateForDay Resolve(TravelDestinationDto stop, DateOnly date)
    {
        var kind = (PerDiemRateKind)(int)stop.Kind;
        var year = FiscalYearOf(date);
        var notes = new List<string>();
        var batch = _batches.GetValueOrDefault((year, kind));
        var lookupDate = date;
        if (batch is null)
        {
            batch = _batches.Values.Where(b => b.Kind == kind && b.FiscalYear < year).MaxBy(b => b.FiscalYear);
            if (batch is not null)
            {
                lookupDate = date.AddYears(batch.FiscalYear - year);
                notes.Add($"FY{year} {kind} rates aren't loaded; FY{batch.FiscalYear} rates are used.");
            }
        }

        var tiers = TiersFor(batch, year);
        var rates = batch is null ? [] : _rates[batch.Id].ToList();
        var isStandardStop = kind == PerDiemRateKind.Conus && PerDiemFileParser.IsStandardName(stop.Destination);
        var match = isStandardStop ? null : rates.FirstOrDefault(r => !r.IsStandard && Matches(r, stop) && Contains(r, lookupDate));
        if (match is not null)
        {
            return ForRate(match, false);
        }

        if (kind == PerDiemRateKind.Conus)
        {
            var standard = rates.FirstOrDefault(r => r.IsStandard && Contains(r, lookupDate)) ?? rates.FirstOrDefault(r => r.IsStandard);
            if (!isStandardStop)
            {
                notes.Add($"No FY{batch?.FiscalYear ?? year} rate for {Label(stop)}; the standard CONUS rate applies.");
            }

            if (standard is not null)
            {
                return ForRate(standard, true);
            }

            notes.Add("No per diem table is loaded; the built-in standard CONUS rate is used.");
            return new PerDiemRateForDay(DefaultMieTiers.StandardLodging, DefaultMieTiers.StandardMie, DefaultMieTiers.Scale(DefaultMieTiers.StandardMie, tiers),
                $"Standard CONUS (built-in)", true, notes);
        }

        notes.Add($"No OCONUS rate for {Label(stop)} in FY{year}; ask Finance to import the DoD/State table.");
        return new PerDiemRateForDay(0, 0, new ParsedMieTier(0, 0, 0, 0, 0, 0), "No rate", false, notes);

        PerDiemRateForDay ForRate(PerDiemRate rate, bool standard) => new(
            rate.LodgingMax, rate.MieRate, DefaultMieTiers.Scale(rate.MieRate, tiers),
            $"{(rate.IsStandard ? PerDiemFileParser.StandardDestination : rate.Destination + (rate.State.Length > 0 ? ", " + rate.State : ""))} FY{rate.FiscalYear}"
                + (IsWholeYear(rate) ? "" : $" ({rate.SeasonStart:MMM d}–{rate.SeasonEnd:MMM d})"),
            standard, notes);
    }

    private IReadOnlyList<ParsedMieTier> TiersFor(PerDiemRateBatch? batch, int year)
    {
        var own = batch is null ? [] : _tiers[batch.Id].ToList();
        if (own.Count == 0)
        {
            own = _batches.Values.Where(b => b.FiscalYear == year).SelectMany(b => _tiers[b.Id]).ToList();
        }

        if (own.Count == 0)
        {
            own = _batches.Values.Where(b => b.FiscalYear < year).OrderByDescending(b => b.FiscalYear).Select(b => _tiers[b.Id].ToList()).FirstOrDefault(t => t.Count > 0) ?? [];
        }

        return own.Count == 0 ? DefaultMieTiers.All : own.Select(t => new ParsedMieTier(t.Total, t.Breakfast, t.Lunch, t.Dinner, t.Incidentals, t.FirstLastDay)).ToList();
    }

    private static bool Matches(PerDiemRate rate, TravelDestinationDto stop) =>
        Same(rate.Destination, stop.Destination) && Same(rate.State, stop.State)
        && (rate.Kind == PerDiemRateKind.Conus || Same(rate.Country, stop.Country));

    private static bool Same(string a, string? b) => string.Equals(a.Trim(), (b ?? "").Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool Contains(PerDiemRate rate, DateOnly date) => rate.SeasonStart <= date && date <= rate.SeasonEnd;

    private static bool IsWholeYear(PerDiemRate rate) => rate.SeasonStart.Month == 10 && rate.SeasonStart.Day == 1 && rate.SeasonEnd.Month == 9 && rate.SeasonEnd.Day == 30;

    private static string Label(TravelDestinationDto stop) =>
        stop.Destination + (stop.State.Length > 0 ? ", " + stop.State : "") + (stop.Kind == PerDiemTable.Oconus && stop.Country.Length > 0 ? ", " + stop.Country : "");
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.PerDiem;
using Workbench.Domain.PerDiem;

namespace Workbench.Application.PerDiem;

/// <summary>Per diem rate tables. Each import is a new batch (append-only) that becomes the table in force for its year and kind.</summary>
public sealed class PerDiemRateService(
    IWorkbenchDbContextFactory dbFactory,
    IGsaPerDiemClient gsa,
    IOptions<PerDiemOptions> options,
    AuditRecorder audit,
    TimeProvider clock) : IPerDiemRateService
{
    public const string SampleResource = "Workbench.Application.PerDiem.gsa-conus-sample.csv";

    public async Task<PerDiemRateTableDto> GetTableAsync(PerDiemRateQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new PerDiemRateQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var tables = await PerDiemTables.LoadAsync(db, cancellationToken);
        var search = query.Search?.Trim();
        var rates = tables.RatesInForce(query.FiscalYear, query.Kind is { } k ? (PerDiemRateKind)(int)k : null)
            .Where(r => string.IsNullOrEmpty(search)
                || r.Destination.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.State.Contains(search, StringComparison.OrdinalIgnoreCase)
                || r.Country.Contains(search, StringComparison.OrdinalIgnoreCase)
                || (r.County ?? "").Contains(search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(r => r.FiscalYear).ThenBy(r => r.Kind).ThenByDescending(r => r.IsStandard)
            .ThenBy(r => r.Country, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.State, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Destination, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.SeasonStart)
            .Take(Math.Clamp(query.Take, 1, 5000))
            .Select(ToDto)
            .ToList();
        var tiers = tables.TiersInForce(query.FiscalYear)
            .GroupBy(t => (t.FiscalYear, t.Total)).Select(g => g.First())
            .OrderByDescending(t => t.FiscalYear).ThenBy(t => t.Total)
            .Select(t => new MieTierDto(t.FiscalYear, t.Total, t.Breakfast, t.Lunch, t.Dinner, t.Incidentals, t.FirstLastDay))
            .ToList();
        var batches = (await db.Set<PerDiemRateBatch>().AsNoTracking().ToListAsync(cancellationToken))
            .OrderByDescending(b => b.ImportedAtUtc).Take(50).Select(ToDto).ToList();
        return new PerDiemRateTableDto(rates, tiers, batches, gsa.IsConfigured, options.Value.LodgingOveragePolicy);
    }

    public async Task<IReadOnlyList<PerDiemLocationDto>> ListLocationsAsync(int fiscalYear, PerDiemTable? kind = null, string? search = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var tables = await PerDiemTables.LoadAsync(db, cancellationToken);
        var rates = tables.RatesInForce(fiscalYear).ToList();
        if (rates.Count == 0)
        {
            // Nothing for that year yet: offer the latest year's destinations (lookups fall back the same way).
            var latest = tables.Batches.Where(b => b.FiscalYear < fiscalYear).Select(b => b.FiscalYear).DefaultIfEmpty().Max();
            rates = tables.RatesInForce(latest).ToList();
        }

        return rates
            .Where(r => kind is null || (int)r.Kind == (int)kind)
            .Where(r => string.IsNullOrWhiteSpace(search) || r.Destination.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                || r.State.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase) || r.Country.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .Select(r => new PerDiemLocationDto((PerDiemTable)(int)r.Kind, r.Country, r.State, r.Destination))
            .Distinct()
            .OrderByDescending(l => PerDiemFileParser.IsStandardName(l.Destination)).ThenBy(l => l.Kind).ThenBy(l => l.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ResolvedPerDiemRateDto> LookupAsync(PerDiemLocationDto location, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var tables = await PerDiemTables.LoadAsync(db, cancellationToken);
        var rate = tables.Resolve(new TravelDestinationDto(location.Kind, location.Country, location.State, location.Destination, date, date), date);
        var t = rate.Tier;
        return new ResolvedPerDiemRateDto(rate.LodgingMax, rate.MieRate,
            new MieTierDto(PerDiemTables.FiscalYearOf(date), t.Total, t.Breakfast, t.Lunch, t.Dinner, t.Incidentals, t.FirstLastDay),
            rate.IsStandard && !PerDiemFileParser.IsStandardName(location.Destination), rate.Description, rate.Notes);
    }

    public async Task<PerDiemImportResultDto> ImportFileAsync(ImportPerDiemFileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateYear(request.FiscalYear);
        if (request.Content is not { Length: > 0 })
        {
            throw new ArgumentException("The file is empty.");
        }

        if (request.Content.Length > 20 * 1024 * 1024)
        {
            throw new ArgumentException("The file is larger than 20 MB.");
        }

        var kind = (PerDiemRateKind)(int)request.Kind;
        var parsed = PerDiemFileParser.Parse(request.FileName, request.Content, request.FiscalYear, kind);
        var excel = PerDiemFileParser.IsExcel(request.FileName, request.Content);
        var source = kind == PerDiemRateKind.Oconus ? "OCONUS " + (excel ? "Excel" : "CSV") : "GSA " + (excel ? "Excel" : "CSV");
        return await SaveBatchAsync(request.FiscalYear, kind, source, Path.GetFileName(request.FileName), parsed.Rates, [], parsed.Warnings, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> ImportFromGsaApiAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        ValidateYear(fiscalYear);
        if (!gsa.IsConfigured)
        {
            throw new ArgumentException("The GSA Per Diem API isn't configured: set PerDiem:Gsa:ApiKey to an api.data.gov key.");
        }

        var lodging = await gsa.GetConusLodgingAsync(fiscalYear, cancellationToken);
        var tiers = await gsa.GetMieBreakdownAsync(fiscalYear, cancellationToken);
        var rates = lodging.SelectMany(l => GsaPerDiemHttpClient.ToSeasons(l, fiscalYear)).ToList();
        if (rates.Count == 0)
        {
            throw new ArgumentException($"The GSA API returned no FY{fiscalYear} rates.");
        }

        var warnings = new List<string>();
        if (!rates.Any(r => r.IsStandard))
        {
            warnings.Add("The GSA response had no standard CONUS rate.");
        }

        return await SaveBatchAsync(fiscalYear, PerDiemRateKind.Conus, "GSA API", null, rates, tiers, warnings, cancellationToken);
    }

    public async Task<PerDiemImportResultDto> InstallSampleRatesAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        ValidateYear(fiscalYear);
        var parsed = PerDiemFileParser.Parse("gsa-conus-sample.csv", SampleFile(), fiscalYear, PerDiemRateKind.Conus);
        return await SaveBatchAsync(fiscalYear, PerDiemRateKind.Conus, "Sample", "gsa-conus-sample.csv", parsed.Rates, DefaultMieTiers.All, parsed.Warnings, cancellationToken);
    }

    /// <summary>The shipped sample CONUS rate file (samples/perdiem/gsa-conus-sample.csv).</summary>
    public static byte[] SampleFile()
    {
        using var stream = typeof(PerDiemRateService).Assembly.GetManifestResourceStream(SampleResource)
            ?? throw new InvalidOperationException("The sample per diem file isn't embedded.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    /// <summary>Saves a parsed table as a new batch (also used by the development seed).</summary>
    public static PerDiemRateBatch AddBatch(IWorkbenchDbContext db, int fiscalYear, PerDiemRateKind kind, string source, string? fileName,
        IReadOnlyList<ParsedPerDiemRate> rates, IReadOnlyList<ParsedMieTier> tiers, string actor, DateTime now)
    {
        var batch = new PerDiemRateBatch
        {
            FiscalYear = fiscalYear,
            Kind = kind,
            Source = source,
            FileName = fileName,
            RateCount = rates.Count,
            TierCount = tiers.Count,
            ImportedBy = actor,
            ImportedAtUtc = now,
        };
        db.Set<PerDiemRateBatch>().Add(batch);
        db.Set<PerDiemRate>().AddRange(rates.Select(r => new PerDiemRate
        {
            BatchId = batch.Id,
            FiscalYear = fiscalYear,
            Kind = kind,
            Country = r.Country,
            State = r.State,
            Destination = r.Destination,
            County = r.County,
            SeasonStart = r.SeasonStart,
            SeasonEnd = r.SeasonEnd,
            LodgingMax = r.LodgingMax,
            MieRate = r.MieRate,
            IsStandard = r.IsStandard,
        }));
        db.Set<PerDiemMieTier>().AddRange(tiers.Select(t => new PerDiemMieTier
        {
            BatchId = batch.Id,
            FiscalYear = fiscalYear,
            Total = t.Total,
            Breakfast = t.Breakfast,
            Lunch = t.Lunch,
            Dinner = t.Dinner,
            Incidentals = t.Incidentals,
            FirstLastDay = t.FirstLastDay,
        }));
        return batch;
    }

    private async Task<PerDiemImportResultDto> SaveBatchAsync(int fiscalYear, PerDiemRateKind kind, string source, string? fileName,
        IReadOnlyList<ParsedPerDiemRate> rates, IReadOnlyList<ParsedMieTier> tiers, IReadOnlyList<string> warnings, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var batch = AddBatch(db, fiscalYear, kind, source, fileName, rates, tiers, audit.Actor, clock.GetUtcNow().UtcDateTime);
        audit.Record(db, nameof(PerDiemRateBatch), batch.Id, "Imported",
            after: new { batch.FiscalYear, Kind = kind.ToString(), batch.Source, batch.FileName, batch.RateCount, batch.TierCount });
        await db.SaveChangesAsync(ct);
        return new PerDiemImportResultDto(ToDto(batch), warnings);
    }

    private static void ValidateYear(int fiscalYear)
    {
        if (fiscalYear is < 2000 or > 2100)
        {
            throw new ArgumentException("Pick a fiscal year between 2000 and 2100.");
        }
    }

    private static PerDiemRateDto ToDto(PerDiemRate r) => new(r.Id, r.FiscalYear, (PerDiemTable)(int)r.Kind, r.Country, r.State, r.Destination, r.County,
        r.SeasonStart, r.SeasonEnd, r.LodgingMax, r.MieRate, r.IsStandard);

    private static PerDiemBatchDto ToDto(PerDiemRateBatch b) => new(b.Id, b.FiscalYear, (PerDiemTable)(int)b.Kind, b.Source, b.FileName, b.RateCount, b.TierCount,
        b.ImportedBy, b.ImportedAtUtc);
}

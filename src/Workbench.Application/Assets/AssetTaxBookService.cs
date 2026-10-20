using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Security;
using Workbench.Domain.Assets;
using Workbench.Domain.Companies;

namespace Workbench.Application.Assets;

/// <summary>
/// Tax depreciation books (federal, state, AMT) beside each asset's own GAAP book, and the tax reports: the depreciation
/// schedule by tax year, book-to-tax differences, Form 4562 figures and the mid-quarter test. Tax books never post.
/// </summary>
public sealed class AssetTaxBookService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    IOptions<AssetOptions> options,
    TimeProvider clock) : IAssetTaxBookService
{
    public const string BookEntity = nameof(AssetTaxBook);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public async Task<AssetBooksDto> GetBooksAsync(Guid assetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Asset", assetId);
        var startMonth = await AssetSupport.TaxYearStartMonthAsync(db, asset.CompanyId, cancellationToken);
        var books = await db.Set<AssetTaxBook>().AsNoTracking().Where(b => b.AssetId == assetId).ToListAsync(cancellationToken);
        var schedules = new List<AssetTaxScheduleDto>();
        if (!asset.IsGovernmentProperty)
        {
            schedules.Add(BookSchedule(asset, startMonth));
        }

        foreach (var book in books.OrderBy(b => b.Book))
        {
            var s = ScheduleOf(asset, book, startMonth);
            schedules.Add(new AssetTaxScheduleDto(asset.Id, asset.TagNumber, asset.Description, Kind(book.Book), MethodLabel(book.Method, book.RecoveryYears, book.Convention), s.Terms.Basis,
                s.Years.Select(Row).ToList()));
        }

        return new AssetBooksDto(asset.Id, asset.TagNumber, startMonth, books.OrderBy(b => b.Book).Select(b => Dto(asset, b, startMonth)).ToList(), schedules);
    }

    public async Task<AssetTaxBookDto> SaveBookAsync(Guid assetId, SaveAssetTaxBookRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Book == AssetBookKind.Book || !Enum.IsDefined(request.Book))
        {
            throw new ArgumentException("The book (GAAP) depreciation is the asset itself; add a federal, state or AMT tax book here.");
        }

        if (!Enum.IsDefined(request.Method) || !Enum.IsDefined(request.Convention))
        {
            throw new ArgumentException("Unknown tax depreciation method or convention.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await AssetSupport.FindAsync(db, assetId, cancellationToken);
        if (asset.IsGovernmentProperty)
        {
            throw new ArgumentException("Government property belongs to the government, so it has no tax depreciation.");
        }

        if (asset.Status != AssetStatus.InService)
        {
            throw new InvalidOperationException($"{asset.TagNumber} is {asset.Status}; its tax books are closed.");
        }

        var kind = (TaxBookKind)((int)request.Book - 1);
        var book = await db.Set<AssetTaxBook>().FirstOrDefaultAsync(b => b.AssetId == assetId && b.Book == kind, cancellationToken);
        var before = book is null ? null : Snapshot(book);
        book ??= new AssetTaxBook { AssetId = assetId, Book = kind, UpdatedBy = audit.Actor };
        book.Method = (TaxDepreciationMethod)(int)request.Method;
        book.RecoveryYears = request.RecoveryYears;
        book.Convention = (TaxConvention)(int)request.Convention;
        book.Section179Amount = decimal.Round(request.Section179Amount, 2);
        book.BonusPercent = decimal.Round(request.BonusPercent, 2);
        book.Basis = request.Basis is { } basis ? decimal.Round(basis, 2) : null;
        book.PlacedInServiceDate = request.PlacedInServiceDate;
        book.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 1000);
        if (book.PlacedInServiceDate is { } placed && placed < asset.AcquisitionDate)
        {
            throw new ArgumentException("The tax placed-in-service date can't be before the acquisition date.");
        }

        var startMonth = await AssetSupport.TaxYearStartMonthAsync(db, asset.CompanyId, cancellationToken);
        TaxDepreciation.Validate(AssetSupport.Terms(asset, book, startMonth));
        book.UpdatedBy = audit.Actor;
        book.UpdatedAtUtc = UtcNow;
        if (before is null)
        {
            db.Set<AssetTaxBook>().Add(book);
        }

        audit.Record(db, BookEntity, book.Id, before is null ? "Created" : "Updated", before, Snapshot(book));
        await db.SaveChangesAsync(cancellationToken);
        return Dto(asset, book, startMonth);
    }

    public async Task DeleteBookAsync(Guid assetId, AssetBookKind book, CancellationToken cancellationToken = default)
    {
        if (book == AssetBookKind.Book)
        {
            throw new ArgumentException("The book (GAAP) depreciation is the asset itself and can't be removed.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await AssetSupport.FindAsync(db, assetId, cancellationToken);
        var kind = (TaxBookKind)((int)book - 1);
        var row = await db.Set<AssetTaxBook>().FirstOrDefaultAsync(b => b.AssetId == assetId && b.Book == kind, cancellationToken)
            ?? throw WorkbenchNotFoundException.For($"{book} book", assetId);
        if (asset.Status != AssetStatus.InService)
        {
            throw new InvalidOperationException($"{asset.TagNumber} is {asset.Status}; its tax books are kept as they were at disposal.");
        }

        audit.Record(db, BookEntity, row.Id, "Deleted", Snapshot(row));
        db.Set<AssetTaxBook>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
    }

    public Task<TaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new TaxSettingsDto(options.Value.Section179Limit, options.Value.Section179PhaseOutThreshold, options.Value.TaxRate));

    // ---- Reports ----

    public async Task<TaxDepreciationReportDto> GetDepreciationReportAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(query, cancellationToken);
        var rows = new List<TaxDepreciationRowDto>();
        foreach (var item in data.Items.Where(i => i.InScope(query.TaxYear)))
        {
            var year = item.Schedule.For(query.TaxYear);
            var prior = item.Schedule.TotalThrough(query.TaxYear - 1);
            var accumulated = prior + (year?.Total ?? 0);
            rows.Add(new TaxDepreciationRowDto(item.Asset.Id, item.Asset.TagNumber, item.Asset.Description, data.CompanyCode(item.Asset.CompanyId),
                item.Schedule.Terms.PlacedInService, MethodLabel(item.Book.Method, item.Book.RecoveryYears, item.Book.Convention), item.Book.RecoveryYears,
                (TaxConventionKind)(int)item.Book.Convention, item.Schedule.Terms.Basis, year?.Section179 ?? 0, year?.Bonus ?? 0, prior, year?.Depreciation ?? 0,
                accumulated, item.Schedule.Terms.Basis - accumulated, item.Asset.DisposedOn is { } d && d <= data.End));
        }

        rows = rows.OrderBy(r => r.TagNumber, StringComparer.Ordinal).ToList();
        var totals = new TaxDepreciationRowDto(Guid.Empty, "", "Total", "", data.Start, "", 0, TaxConventionKind.HalfYear, rows.Sum(r => r.Basis), rows.Sum(r => r.Section179),
            rows.Sum(r => r.Bonus), rows.Sum(r => r.PriorDepreciation), rows.Sum(r => r.CurrentDepreciation), rows.Sum(r => r.AccumulatedDepreciation), rows.Sum(r => r.EndingBasis), false);
        return new TaxDepreciationReportDto(query.TaxYear, data.Start, data.End, query.Book, rows, totals);
    }

    public async Task<BookTaxDifferenceDto> GetBookTaxDifferenceAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(query, cancellationToken);
        var rate = options.Value.TaxRate;
        var rows = new List<BookTaxDifferenceRowDto>();
        foreach (var item in data.Items.Where(i => i.InScope(query.TaxYear)))
        {
            var asset = item.Asset;
            var (start, end) = YearRange(query.TaxYear, item.StartMonth);
            var schedule = AssetSupport.Schedule(asset);
            var bookYear = BookDepreciation(asset, schedule, start, end);
            var bookThrough = BookDepreciation(asset, schedule, DateOnly.MinValue, end);
            var gone = asset.DisposedOn is { } d && d <= end;
            var bookNet = gone ? 0 : asset.Cost - bookThrough;
            var taxYear = item.Schedule.For(query.TaxYear)?.Total ?? 0;
            var taxBasis = gone ? 0 : item.Schedule.BasisAfter(query.TaxYear);
            var temporary = bookNet - taxBasis;
            rows.Add(new BookTaxDifferenceRowDto(asset.Id, asset.TagNumber, asset.Description, bookYear, taxYear, taxYear - bookYear, bookNet, taxBasis, temporary,
                decimal.Round(temporary * rate, 2, MidpointRounding.AwayFromZero)));
        }

        rows = rows.OrderBy(r => r.TagNumber, StringComparer.Ordinal).ToList();
        var totals = new BookTaxDifferenceRowDto(Guid.Empty, "", "Total", rows.Sum(r => r.BookDepreciation), rows.Sum(r => r.TaxDepreciation), rows.Sum(r => r.Difference),
            rows.Sum(r => r.BookNetValue), rows.Sum(r => r.TaxBasis), rows.Sum(r => r.TemporaryDifference), rows.Sum(r => r.DeferredTax));
        return new BookTaxDifferenceDto(query.TaxYear, data.Start, data.End, query.Book, rate, rows, totals);
    }

    public async Task<Form4562SummaryDto> GetForm4562Async(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(query with { Book = AssetBookKind.Federal }, cancellationToken);
        var o = options.Value;
        var current = data.Items.Where(i => i.PlacedYear == query.TaxYear).ToList();
        var line2 = current.Where(i => i.Book.Section179Amount > 0).Sum(i => i.Schedule.Terms.Basis);
        var line8 = current.Sum(i => i.Schedule.For(query.TaxYear)?.Section179 ?? 0);
        var line4 = Math.Max(0, line2 - o.Section179PhaseOutThreshold);
        var line5 = Math.Max(0, o.Section179Limit - line4);
        var line12 = Math.Min(line8, line5);
        var line14 = current.Sum(i => i.Schedule.For(query.TaxYear)?.Bonus ?? 0);
        var line17 = data.Items.Where(i => i.PlacedYear < query.TaxYear).Sum(i => i.Schedule.For(query.TaxYear)?.Depreciation ?? 0);
        List<Form4562ClassRowDto> Classes(bool ads) => current
            .Where(i => (i.Book.Method == TaxDepreciationMethod.MacrsAds) == ads)
            .GroupBy(i => (i.Book.RecoveryYears, i.Book.Convention, i.Book.Method))
            .OrderBy(g => g.Key.RecoveryYears).ThenBy(g => g.Key.Convention)
            .Select(g => new Form4562ClassRowDto(ads ? "20" : ClassLine(g.Key.RecoveryYears), ClassLabel(g.Key.RecoveryYears), g.Sum(i => i.Schedule.DepreciableBasis),
                ConventionCode(g.Key.Convention), MethodCode(g.Key.Method, g.Key.RecoveryYears), g.Sum(i => i.Schedule.For(query.TaxYear)?.Depreciation ?? 0)))
            .ToList();
        var line19 = Classes(false);
        var line20 = Classes(true);
        var test = MidQuarter(query.TaxYear, data);
        return new Form4562SummaryDto(query.TaxYear, data.Start, data.End, o.Section179Limit, line2, o.Section179PhaseOutThreshold, line4, line5, line8, line12, line14, line17,
            line19, line20, line12 + line14 + line17 + line19.Sum(r => r.Deduction) + line20.Sum(r => r.Deduction), test.MidQuarterRequired);
    }

    public async Task<MidQuarterTestDto> GetMidQuarterTestAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var data = await LoadAsync(query, cancellationToken);
        var test = MidQuarter(query.TaxYear, data);
        var expected = test.MidQuarterRequired ? TaxConvention.MidQuarter : TaxConvention.HalfYear;
        var assets = data.Items
            .Where(i => i.PlacedYear == query.TaxYear && i.Book.Convention != TaxConvention.MidMonth && !TaxDepreciation.IsRealProperty(i.Book.RecoveryYears))
            .Where(i => !(i.Asset.DisposedOn is { } d && TaxDepreciation.TaxYearOf(d, i.StartMonth) == query.TaxYear))
            .OrderBy(i => i.Schedule.Terms.PlacedInService).ThenBy(i => i.Asset.TagNumber, StringComparer.Ordinal)
            .Select(i => new MidQuarterBookDto(i.Asset.Id, i.Asset.TagNumber, i.Asset.Description, i.Schedule.Terms.PlacedInService,
                TaxDepreciation.QuarterOfTaxYear(i.Schedule.Terms.PlacedInService, i.StartMonth), i.Schedule.Terms.Basis - i.Schedule.Terms.Section179,
                (TaxConventionKind)(int)i.Book.Convention, i.Book.Convention == expected))
            .ToList();
        return new MidQuarterTestDto(query.TaxYear, data.Start, data.End, query.Book, test.BasisByQuarter, test.Total, decimal.Round(test.LastQuarterShare * 100, 2),
            test.MidQuarterRequired, assets);
    }

    public async Task<AssetExportFile> ExportDepreciationReportCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var r = await GetDepreciationReportAsync(query, cancellationToken);
        return new AssetExportFile($"tax-depreciation-{query.Book.ToString().ToLowerInvariant()}-{query.TaxYear}.csv", "text/csv", PurchasingSupport.Csv(
            new[] { new object?[] { "Tag", "Description", "Company", "Placed in service", "Method", "Recovery (years)", "Convention", "Basis", "Section 179", "Bonus", "Prior depreciation", "MACRS this year", "Accumulated", "Ending basis", "Disposed" } }
                .Concat(r.Rows.Append(r.Totals).Select(x => new object?[] { x.TagNumber, x.Description, x.CompanyCode, x.AssetId == Guid.Empty ? null : x.PlacedInService, x.Method,
                    x.AssetId == Guid.Empty ? null : x.RecoveryYears, x.AssetId == Guid.Empty ? null : x.Convention, x.Basis, x.Section179, x.Bonus, x.PriorDepreciation,
                    x.CurrentDepreciation, x.AccumulatedDepreciation, x.EndingBasis, x.Disposed ? "Y" : "" }))));
    }

    public async Task<AssetExportFile> ExportBookTaxDifferenceCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var r = await GetBookTaxDifferenceAsync(query, cancellationToken);
        return new AssetExportFile($"book-to-tax-{query.Book.ToString().ToLowerInvariant()}-{query.TaxYear}.csv", "text/csv", PurchasingSupport.Csv(
            new[] { new object?[] { "Tag", "Description", "Book depreciation", "Tax depreciation", "Difference", "Book net value", "Tax basis", "Temporary difference", $"Deferred tax at {r.TaxRate:P1}" } }
                .Concat(r.Rows.Append(r.Totals).Select(x => new object?[] { x.TagNumber, x.Description, x.BookDepreciation, x.TaxDepreciation, x.Difference, x.BookNetValue,
                    x.TaxBasis, x.TemporaryDifference, x.DeferredTax }))));
    }

    // ---- Helpers ----

    private sealed record Item(FixedAsset Asset, AssetTaxBook Book, TaxSchedule Schedule, int StartMonth)
    {
        public int PlacedYear => TaxDepreciation.TaxYearOf(Schedule.Terms.PlacedInService, StartMonth);

        /// <summary>Placed in service by the end of the year and not disposed of before it began.</summary>
        public bool InScope(int taxYear) =>
            PlacedYear <= taxYear && !(Asset.DisposedOn is { } d && TaxDepreciation.TaxYearOf(d, StartMonth) < taxYear);
    }

    private sealed record ReportData(IReadOnlyList<Item> Items, DateOnly Start, DateOnly End, IReadOnlyDictionary<Guid, string> Companies)
    {
        public string CompanyCode(Guid id) => Companies.GetValueOrDefault(id) ?? (id == Company.DefaultId ? "MAIN" : "");
    }

    private async Task<ReportData> LoadAsync(TaxReportQuery query, CancellationToken ct)
    {
        if (query.Book == AssetBookKind.Book || !Enum.IsDefined(query.Book))
        {
            throw new ArgumentException("Tax reports are for the federal, state or AMT book.");
        }

        if (query.TaxYear is < 1990 or > 2200)
        {
            throw new ArgumentException("Choose a tax year between 1990 and 2200.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var kind = (TaxBookKind)((int)query.Book - 1);
        var books = await db.Set<AssetTaxBook>().AsNoTracking().Where(b => b.Book == kind).ToListAsync(ct);
        var assetIds = books.Select(b => b.AssetId).ToList();
        var assetQuery = db.Set<FixedAsset>().AsNoTracking().Where(a => assetIds.Contains(a.Id) && !a.IsGovernmentFurnished && !a.IsContractorAcquired);
        if (query.CompanyId is { } companyId)
        {
            assetQuery = assetQuery.Where(a => a.CompanyId == companyId);
        }

        var assets = await assetQuery.ToDictionaryAsync(a => a.Id, ct);
        var companies = await db.Set<Company>().AsNoTracking().ToListAsync(ct);
        int Start(Guid company) => companies.FirstOrDefault(c => c.Id == company)?.FiscalYearStartMonth ?? 10;
        var items = books.Where(b => assets.ContainsKey(b.AssetId)).Select(b =>
        {
            var asset = assets[b.AssetId];
            var month = Start(asset.CompanyId);
            var disposedOn = asset.Status == AssetStatus.Disposed ? asset.DisposedOn : null;
            return new Item(asset, b, TaxDepreciation.Schedule(AssetSupport.Terms(asset, b, month), disposedOn), month);
        }).ToList();
        var (start, end) = YearRange(query.TaxYear, Start(query.CompanyId ?? Company.DefaultId));
        return new ReportData(items, start, end, companies.ToDictionary(c => c.Id, c => c.Code));
    }

    private static MidQuarterTestResult MidQuarter(int taxYear, ReportData data)
    {
        // Each company's year is tested on its own months; the report's quarters are the companies' own quarters added up.
        var quarters = new decimal[4];
        foreach (var group in data.Items.GroupBy(i => i.StartMonth))
        {
            var r = TaxDepreciation.MidQuarterTest(taxYear, group.Key, group.Select(i => (i.Schedule.Terms, i.Asset.Status == AssetStatus.Disposed ? i.Asset.DisposedOn : null)));
            for (var q = 0; q < 4; q++)
            {
                quarters[q] += r.BasisByQuarter[q];
            }
        }

        return new MidQuarterTestResult(taxYear, quarters, quarters.Sum());
    }

    private static (DateOnly Start, DateOnly End) YearRange(int taxYear, int startMonth)
    {
        var start = new DateOnly(taxYear, startMonth, 1);
        return (start, start.AddYears(1).AddDays(-1));
    }

    /// <summary>Scheduled GAAP depreciation for the months from <paramref name="from"/> to <paramref name="to"/> (none from the disposal month on).</summary>
    internal static decimal BookDepreciation(FixedAsset asset, IReadOnlyList<decimal> schedule, DateOnly from, DateOnly to)
    {
        if (asset.IsGovernmentProperty)
        {
            return 0;
        }

        var last = DepreciationCalculator.MonthIndex(asset.InServiceDate, to);
        if (asset.DisposedOn is { } disposed && asset.Status != AssetStatus.InService)
        {
            last = Math.Min(last, DepreciationCalculator.MonthIndex(asset.InServiceDate, disposed) - 1);
        }

        var first = from == DateOnly.MinValue ? 0 : Math.Max(0, DepreciationCalculator.MonthIndex(asset.InServiceDate, from));
        decimal total = 0;
        for (var m = first; m <= last && m < schedule.Count; m++)
        {
            total += schedule[m];
        }

        return total;
    }

    /// <summary>The GAAP book's scheduled depreciation summed by tax year, so it lines up with the tax books.</summary>
    private static AssetTaxScheduleDto BookSchedule(FixedAsset asset, int startMonth)
    {
        var schedule = AssetSupport.Schedule(asset);
        var rows = new List<TaxScheduleRowDto>();
        var year = TaxDepreciation.TaxYearOf(asset.InServiceDate, startMonth);
        var basis = asset.Cost;
        var lastMonth = asset.InServiceDate.AddMonths(schedule.Count);
        while (true)
        {
            var (start, end) = YearRange(year, startMonth);
            if (start >= lastMonth || (asset.DisposedOn is { } d && asset.Status != AssetStatus.InService && start > d))
            {
                break;
            }

            var amount = BookDepreciation(asset, schedule, start, end);
            rows.Add(new TaxScheduleRowDto(year, start, end, basis, 0, 0, amount, amount, basis - amount));
            basis -= amount;
            year++;
        }

        return new AssetTaxScheduleDto(asset.Id, asset.TagNumber, asset.Description, AssetBookKind.Book, $"{asset.Method} over {asset.UsefulLifeYears} years ({asset.Convention})", asset.Cost, rows);
    }

    private static TaxSchedule ScheduleOf(FixedAsset asset, AssetTaxBook book, int startMonth) =>
        TaxDepreciation.Schedule(AssetSupport.Terms(asset, book, startMonth), asset.Status == AssetStatus.Disposed ? asset.DisposedOn : null);

    private AssetTaxBookDto Dto(FixedAsset asset, AssetTaxBook book, int startMonth)
    {
        var schedule = ScheduleOf(asset, book, startMonth);
        var through = asset.Status == AssetStatus.Disposed && asset.DisposedOn is { } d ? TaxDepreciation.TaxYearOf(d, startMonth) : TaxDepreciation.TaxYearOf(Today, startMonth);
        var toDate = schedule.TotalThrough(through);
        var first = schedule.Years.FirstOrDefault();
        return new AssetTaxBookDto(book.Id, book.AssetId, Kind(book.Book), (TaxMethod)(int)book.Method, book.RecoveryYears, (TaxConventionKind)(int)book.Convention,
            schedule.Terms.Basis, book.Basis is null, schedule.Terms.PlacedInService, book.Section179Amount, book.BonusPercent, first?.Bonus ?? 0, book.Notes,
            toDate, schedule.Terms.Basis - toDate, book.DisposalDepreciation, book.DisposalAdjustedBasis, book.DisposalGainLoss, book.UpdatedBy, book.UpdatedAtUtc);
    }

    private static TaxScheduleRowDto Row(TaxYearAmount y) => new(y.TaxYear, y.Start, y.End, y.BeginningBasis, y.Section179, y.Bonus, y.Depreciation, y.Total, y.EndingBasis);

    private static AssetBookKind Kind(TaxBookKind k) => (AssetBookKind)((int)k + 1);

    internal static string MethodCode(TaxDepreciationMethod method, decimal years) => TaxDepreciation.Factor(method, years) switch
    {
        2m => "200DB",
        1.5m => "150DB",
        _ => "S/L",
    };

    internal static string ConventionCode(TaxConvention c) => c switch
    {
        TaxConvention.HalfYear => "HY",
        TaxConvention.MidQuarter => "MQ",
        _ => "MM",
    };

    internal static string MethodLabel(TaxDepreciationMethod method, decimal years, TaxConvention convention) =>
        $"{(method == TaxDepreciationMethod.MacrsAds ? "ADS" : "MACRS")} {MethodCode(method, years)} {years:0.#}-yr {ConventionCode(convention)}";

    private static string ClassLine(decimal years) => years switch
    {
        3m => "19a",
        5m => "19b",
        7m => "19c",
        10m => "19d",
        15m => "19e",
        20m => "19f",
        25m => "19g",
        27.5m => "19h",
        39m => "19i",
        _ => "19",
    };

    private static string ClassLabel(decimal years) => years switch
    {
        27.5m => "Residential rental property (27.5 years)",
        39m => "Nonresidential real property (39 years)",
        _ => $"{years:0.#}-year property",
    };

    private static object Snapshot(AssetTaxBook b) => new
    {
        Book = b.Book.ToString(),
        Method = b.Method.ToString(),
        b.RecoveryYears,
        Convention = b.Convention.ToString(),
        b.Basis,
        b.PlacedInServiceDate,
        b.Section179Amount,
        b.BonusPercent,
        b.Notes,
    };
}

/// <summary>Records an <see cref="SecurityEventTypes.ExportAssets"/> event for each tax report export (inside the permission check).</summary>
public sealed class SecurityLoggingAssetTaxBookService(IAssetTaxBookService inner, SecurityEventLog log) : IAssetTaxBookService
{
    public Task<AssetBooksDto> GetBooksAsync(Guid assetId, CancellationToken cancellationToken = default) => inner.GetBooksAsync(assetId, cancellationToken);

    public Task<AssetTaxBookDto> SaveBookAsync(Guid assetId, SaveAssetTaxBookRequest request, CancellationToken cancellationToken = default) => inner.SaveBookAsync(assetId, request, cancellationToken);

    public Task DeleteBookAsync(Guid assetId, AssetBookKind book, CancellationToken cancellationToken = default) => inner.DeleteBookAsync(assetId, book, cancellationToken);

    public Task<TaxSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) => inner.GetSettingsAsync(cancellationToken);

    public Task<TaxDepreciationReportDto> GetDepreciationReportAsync(TaxReportQuery query, CancellationToken cancellationToken = default) => inner.GetDepreciationReportAsync(query, cancellationToken);

    public Task<BookTaxDifferenceDto> GetBookTaxDifferenceAsync(TaxReportQuery query, CancellationToken cancellationToken = default) => inner.GetBookTaxDifferenceAsync(query, cancellationToken);

    public Task<Form4562SummaryDto> GetForm4562Async(TaxReportQuery query, CancellationToken cancellationToken = default) => inner.GetForm4562Async(query, cancellationToken);

    public Task<MidQuarterTestDto> GetMidQuarterTestAsync(TaxReportQuery query, CancellationToken cancellationToken = default) => inner.GetMidQuarterTestAsync(query, cancellationToken);

    public async Task<AssetExportFile> ExportDepreciationReportCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportDepreciationReportCsvAsync(query, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportAssets, file.FileName, detail: $"Tax depreciation ({query.Book}) {query.TaxYear}", cancellationToken: cancellationToken);
        return file;
    }

    public async Task<AssetExportFile> ExportBookTaxDifferenceCsvAsync(TaxReportQuery query, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportBookTaxDifferenceCsvAsync(query, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportAssets, file.FileName, detail: $"Book-to-tax differences ({query.Book}) {query.TaxYear}", cancellationToken: cancellationToken);
        return file;
    }
}

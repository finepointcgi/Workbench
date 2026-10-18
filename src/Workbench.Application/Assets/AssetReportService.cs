using Microsoft.EntityFrameworkCore;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Domain.Assets;

namespace Workbench.Application.Assets;

/// <summary>The asset register, depreciation schedules (book and MACRS tax), roll-forward and government property listing, with CSV exports.</summary>
public sealed class AssetReportService(IWorkbenchDbContextFactory dbFactory) : IAssetReportService
{
    public async Task<IReadOnlyList<AssetSummaryDto>> GetRegisterAsync(AssetQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await AssetQueries.ListAsync(db, query, cancellationToken);
    }

    public async Task<DepreciationScheduleDto> GetScheduleAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var asset = await db.Set<FixedAsset>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == assetId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Asset", assetId);
        var posted = await db.Set<AssetDepreciation>().AsNoTracking().Where(d => d.AssetId == assetId).ToListAsync(cancellationToken);
        var rows = new List<DepreciationScheduleRowDto>();
        if (book == DepreciationBook.Tax)
        {
            var years = asset.MacrsRecoveryYears ?? throw new ArgumentException($"{asset.TagNumber} has no MACRS recovery period.");
            var book0 = asset.Cost;
            var amounts = DepreciationCalculator.Macrs(asset.Cost, years);
            for (var i = 0; i < amounts.Count; i++)
            {
                var year = asset.InServiceDate.Year + i;
                rows.Add(new DepreciationScheduleRowDto(year, $"Tax year {year}", book0, amounts[i], book0 - amounts[i], 0));
                book0 -= amounts[i];
            }

            return new DepreciationScheduleDto(asset.Id, asset.TagNumber, asset.Description, book, $"MACRS {years}-year (half-year convention)", asset.Cost, 0, rows);
        }

        // Book: the monthly schedule grouped by calendar year, beside what's actually posted in each year.
        var schedule = DepreciationCalculator.MonthlySchedule(asset.Cost, asset.SalvageValue, asset.UsefulLifeYears, asset.Method);
        var start = new DateOnly(asset.InServiceDate.Year, asset.InServiceDate.Month, 1);
        var byYear = schedule.Select((amount, i) => (Year: start.AddMonths(i).Year, Amount: amount)).GroupBy(x => x.Year).ToList();
        var bookValue = asset.Cost;
        foreach (var group in byYear)
        {
            var amount = group.Sum(x => x.Amount);
            rows.Add(new DepreciationScheduleRowDto(group.Key, group.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), bookValue, amount, bookValue - amount,
                posted.Where(p => p.PeriodStart.Year == group.Key).Sum(p => p.Amount)));
            bookValue -= amount;
        }

        var method = asset.Method switch
        {
            DepreciationMethod.StraightLine => "Straight line",
            DepreciationMethod.DecliningBalance => "Double-declining balance",
            _ => "Sum-of-years digits",
        };
        return new DepreciationScheduleDto(asset.Id, asset.TagNumber, asset.Description, book, $"{method}, {asset.UsefulLifeYears} years", asset.Cost, asset.SalvageValue, rows);
    }

    public async Task<RollForwardDto> GetRollForwardAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The end date is before the start date.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => !a.IsGovernmentFurnished && !a.IsContractorAcquired).ToListAsync(cancellationToken);
        var depreciation = (await db.Set<AssetDepreciation>().AsNoTracking().ToListAsync(cancellationToken)).ToLookup(d => d.AssetId);
        var categories = await db.Set<AssetCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, cancellationToken);
        var rows = new List<RollForwardRowDto>();
        foreach (var group in assets.GroupBy(a => a.CategoryId).OrderBy(g => categories.GetValueOrDefault(g.Key)?.Code))
        {
            decimal beginning = 0, additions = 0, depr = 0, disposals = 0, ending = 0;
            foreach (var a in group)
            {
                var dep = depreciation[a.Id].ToList();
                var gone = a.Status != AssetStatus.InService && a.DisposedOn is not null;
                if (a.AcquisitionDate < from && !(gone && a.DisposedOn < from))
                {
                    beginning += a.Cost - dep.Where(d => d.PeriodStart < from).Sum(d => d.Amount);
                }

                if (a.AcquisitionDate >= from && a.AcquisitionDate <= to)
                {
                    additions += a.Cost;
                }

                depr += dep.Where(d => d.PeriodStart >= from && d.PeriodStart <= to).Sum(d => d.Amount);
                if (gone && a.DisposedOn >= from && a.DisposedOn <= to)
                {
                    disposals += a.Cost - dep.Sum(d => d.Amount);
                }

                if (a.AcquisitionDate <= to && !(gone && a.DisposedOn <= to))
                {
                    ending += a.Cost - dep.Where(d => d.PeriodStart <= to).Sum(d => d.Amount);
                }
            }

            var category = categories.GetValueOrDefault(group.Key);
            rows.Add(new RollForwardRowDto(category?.Code ?? "", category?.Name ?? "", beginning, additions, depr, disposals, ending));
        }

        var totals = new RollForwardRowDto("", "Total", rows.Sum(r => r.Beginning), rows.Sum(r => r.Additions), rows.Sum(r => r.Depreciation), rows.Sum(r => r.Disposals), rows.Sum(r => r.Ending));
        return new RollForwardDto(from, to, rows, totals);
    }

    public async Task<IReadOnlyList<GovernmentPropertyRowDto>> GetGovernmentPropertyAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var assets = await db.Set<FixedAsset>().AsNoTracking().Where(a => a.IsGovernmentFurnished || a.IsContractorAcquired).ToListAsync(cancellationToken);
        var names = await AssetQueries.Names.LoadAsync(db, assets, cancellationToken);
        return assets.OrderBy(a => names.Contract(a.ContractId)).ThenBy(a => a.TagNumber, StringComparer.Ordinal).Select(a => new GovernmentPropertyRowDto(
            a.Id, a.TagNumber, a.GovernmentPropertyNumber, a.Description, a.IsGovernmentFurnished ? "GFP" : "CAP", names.Contract(a.ContractId), a.AcquisitionDate, a.Cost,
            a.SerialNumber, a.Location, names.Employee(a.CustodianEmployeeId), a.Status.ToContract())).ToList();
    }

    public async Task<AssetExportFile> ExportRegisterCsvAsync(CancellationToken cancellationToken = default)
    {
        var rows = await GetRegisterAsync(new AssetQuery(), cancellationToken);
        return Csv("asset-register.csv",
            [["Tag", "Description", "Category", "Acquired", "Cost", "Accumulated depreciation", "Net book value", "Method", "Life (years)", "Status", "Location", "Custodian", "Org", "Project", "GFP", "CAP", "Gov property no."]],
            rows.Select(r => new object?[] { r.TagNumber, r.Description, r.CategoryCode, r.AcquisitionDate, r.Cost, r.AccumulatedDepreciation, r.NetBookValue, r.Method, r.UsefulLifeYears,
                r.Status, r.Location, r.CustodianName, r.OrgUnitCode, r.ProjectCode, r.IsGovernmentFurnished ? "Y" : "", r.IsContractorAcquired ? "Y" : "", r.GovernmentPropertyNumber }));
    }

    public async Task<AssetExportFile> ExportScheduleCsvAsync(Guid assetId, DepreciationBook book = DepreciationBook.Book, CancellationToken cancellationToken = default)
    {
        var s = await GetScheduleAsync(assetId, book, cancellationToken);
        return Csv($"depreciation-{s.TagNumber}-{book.ToString().ToLowerInvariant()}.csv",
            [["Year", "Beginning book value", "Depreciation", "Ending book value", "Posted"]],
            s.Rows.Select(r => new object?[] { r.Label, r.BeginningBookValue, r.Depreciation, r.EndingBookValue, r.Posted }));
    }

    public async Task<AssetExportFile> ExportRollForwardCsvAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        var r = await GetRollForwardAsync(from, to, cancellationToken);
        return Csv($"asset-roll-forward-{from:yyyyMMdd}-{to:yyyyMMdd}.csv",
            [["Category", "Name", "Beginning", "Additions", "Depreciation", "Disposals", "Ending"]],
            r.Rows.Append(r.Totals).Select(x => new object?[] { x.CategoryCode, x.CategoryName, x.Beginning, x.Additions, x.Depreciation, x.Disposals, x.Ending }));
    }

    public async Task<AssetExportFile> ExportGovernmentPropertyCsvAsync(CancellationToken cancellationToken = default)
    {
        var rows = await GetGovernmentPropertyAsync(cancellationToken);
        return Csv("government-property.csv",
            [["Tag", "Gov property no.", "Description", "Kind", "Contract", "Acquired", "Cost", "Serial", "Location", "Custodian", "Status"]],
            rows.Select(r => new object?[] { r.TagNumber, r.GovernmentPropertyNumber, r.Description, r.Kind, r.ContractNumber, r.AcquisitionDate, r.Cost, r.SerialNumber, r.Location, r.Custodian, r.Status }));
    }

    private static AssetExportFile Csv(string fileName, IEnumerable<IEnumerable<object?>> header, IEnumerable<IEnumerable<object?>> rows) =>
        new(fileName, "text/csv", PurchasingSupport.Csv(header.Concat(rows)));
}

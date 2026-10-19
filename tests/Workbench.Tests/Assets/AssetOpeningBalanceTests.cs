using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Imports;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;
using Workbench.Tests.Imports;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assets;

/// <summary>
/// Opening balances from Costpoint (the fixed asset importer over <c>samples/import/costpoint/fixed-assets.csv</c>, as of
/// 2026-09-30), depreciation continuing after the cut-over, and the export security event. Over <see cref="PurchasingKit"/>
/// (period 2026-10 open; 2026-11 and 2026-12 added here), as finance@.
/// </summary>
public sealed class AssetOpeningBalanceTests
{
    private const string Sample = "samples/import/costpoint/fixed-assets.csv";
    private const string Header = "ASSET_ID,ASSET_DESC,ASSET_CLASS_CD,PL_IN_SERV_DT,ACQ_COST_AMT,ACCUM_DEPR_AMT,NET_BOOK_VAL,AS_OF_DT,LIFE_MOS,SALVAGE_AMT\n";

    private static IAssetService Assets(PurchasingKit kit) => kit.Get<IAssetService>();

    private static IDepreciationService Depreciation(PurchasingKit kit) => kit.Get<IDepreciationService>();

    private static IImportService Imports(PurchasingKit kit) => kit.Get<IImportService>();

    private static async Task<(Guid Oct, Guid Nov, Guid Dec)> PeriodsAsync(PurchasingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        var oct = await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-10");
        var nov = new FiscalPeriod { FiscalYearId = oct.FiscalYearId, Name = "2026-11", Number = 2, Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 30) };
        var dec = new FiscalPeriod { FiscalYearId = oct.FiscalYearId, Name = "2026-12", Number = 3, Start = new DateOnly(2026, 12, 1), End = new DateOnly(2026, 12, 31) };
        db.Set<FiscalPeriod>().AddRange(nov, dec);
        await db.SaveChangesAsync();
        return (oct.Id, nov.Id, dec.Id);
    }

    /// <summary>Uploads, keeps the suggested mapping, validates and dry-runs.</summary>
    private static async Task<ImportJobDetailDto> PrepareAsync(PurchasingKit kit, UploadFileRequest file)
    {
        await Assets(kit).ListCategoriesAsync(); // creates the default categories (COMP, FURN, LHI)
        var job = await Imports(kit).UploadAsync(new UploadImportRequest(ImportSource.Costpoint, ImportKind.FixedAssets, [file]));
        await Imports(kit).SaveMappingAsync(job.Job.Id, new SaveImportMappingRequest(job.Mapping, job.Options));
        await Imports(kit).ValidateAsync(job.Job.Id);
        return await Imports(kit).DryRunAsync(job.Job.Id);
    }

    private static async Task<ImportJobDetailDto> ImportSampleAsync(PurchasingKit kit)
    {
        var dry = await PrepareAsync(kit, Samples.File(Sample));
        var done = await Imports(kit).ImportAsync(dry.Job.Id);
        Assert.True(done.Job.Status == ImportJobStatus.Imported, done.Job.Message);
        return done;
    }

    private static async Task<AssetDto> ByTagAsync(PurchasingKit kit, string tag) =>
        await Assets(kit).GetAsync((await Assets(kit).ListAsync(new AssetQuery(Search: tag))).Single(a => a.TagNumber == tag).Id);

    [Fact]
    public async Task The_sample_imports_after_a_dry_run_posts_nothing_and_rolls_back()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var dry = await PrepareAsync(kit, Samples.File(Sample));

        Assert.Equal(("AS_OF_DT", "LIFE_MOS", "NET_BOOK_VAL", "ASSET_CLASS_CD"),
            (dry.Mapping.Fields["CutoverDate"], dry.Mapping.Fields["UsefulLifeMonths"], dry.Mapping.Fields["NetBookValue"], dry.Mapping.Fields["Category"]));
        Assert.True(dry.Errors.Count == 0, string.Join("; ", dry.Errors.Select(e => $"{e.Row} {e.Field}: {e.Message}")));
        Assert.Equal((5, 5, 0), (dry.Job.Counts.TotalRows, dry.Job.Counts.Creates, dry.Job.Counts.Errors));
        Assert.Empty(await Assets(kit).ListAsync(new AssetQuery())); // the dry run changes nothing

        var done = await Imports(kit).ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var furniture = await ByTagAsync(kit, "CP-1003");
        Assert.Equal((14_000m, 6_500m, 7_500m, 6_500m), (furniture.Asset.Cost, furniture.Asset.AccumulatedDepreciation, furniture.Asset.NetBookValue, furniture.OpeningAccumulatedDepreciation));
        Assert.Equal((new DateOnly(2026, 9, 30), new DateOnly(2023, 7, 1), 7, "FURN"), (furniture.OpeningBalanceDate!.Value, furniture.InServiceDate, furniture.Asset.UsefulLifeYears, furniture.Asset.CategoryCode));
        Assert.Empty(furniture.Depreciation);
        Assert.Empty(await kit.EntriesAsync()); // nothing posted: the balances come with the GL import
        var register = await Assets(kit).ListAsync(new AssetQuery());
        Assert.Equal((161_600m, 69_800m, 91_800m), (register.Sum(a => a.Cost), register.Sum(a => a.AccumulatedDepreciation), register.Sum(a => a.NetBookValue)));

        // Importing the same file again changes nothing.
        var again = await PrepareAsync(kit, Samples.File(Sample));
        Assert.Equal((0, 0, 5), (again.Job.Counts.Creates, again.Job.Counts.Updates, again.Job.Counts.Skips));

        Assert.True((await Imports(kit).CheckRollbackAsync(done.Job.Id)).CanRollBack);
        var rolled = await Imports(kit).RollBackAsync(done.Job.Id);
        Assert.Equal(ImportJobStatus.RolledBack, rolled.Job.Status);
        Assert.Empty(await Assets(kit).ListAsync(new AssetQuery()));
    }

    [Fact]
    public async Task Depreciation_continues_from_the_month_after_the_cut_over_without_double_counting()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, nov, _) = await PeriodsAsync(kit);
        var job = await ImportSampleAsync(kit);

        // October is the first Workbench month: one month each, the fully depreciated plotter nothing.
        var preview = await Depreciation(kit).PreviewAsync(oct);
        Assert.Equal(["CP-1001", "CP-1002", "CP-1003", "CP-1004"], preview.Lines.Select(l => l.TagNumber));
        Assert.Equal(100m + 300m + 166.67m + 1_000m, preview.Total);
        var run = await Depreciation(kit).RunAsync(oct);
        Assert.Equal(1_566.67m, run!.Total);
        Assert.Null(await Depreciation(kit).RunAsync(oct));
        Assert.Equal(-1_566.67m, await kit.BalanceAsync("1590")); // only Workbench's own depreciation is posted
        Assert.Equal(3_100m, (await ByTagAsync(kit, "CP-1001")).Asset.AccumulatedDepreciation);

        // Once depreciation is posted the job can't be rolled back, and the assets can't be re-imported with other balances.
        var check = await Imports(kit).CheckRollbackAsync(job.Job.Id);
        Assert.False(check.CanRollBack);
        var changed = File.ReadAllText(Samples.PathOf(Sample)).Replace("\"3,000.00\",600.00", "\"2,900.00\",700.00");
        var reimport = await PrepareAsync(kit, Samples.Csv("fixed-assets.csv", changed));
        Assert.Contains(reimport.Errors, e => e.Message.Contains("posted depreciation"));

        // November depreciates one more month; the laptop finishes its life in March 2027 at exactly its cost.
        var november = await Depreciation(kit).RunAsync(nov);
        Assert.InRange(november!.Total, 1_566.66m, 1_566.68m);
        var schedule = await kit.Get<IAssetReportService>().GetScheduleAsync((await ByTagAsync(kit, "CP-1001")).Asset.Id);
        Assert.StartsWith("Opening balance through 2026-09-30", schedule.Rows[0].Label);
        Assert.Equal((3_000m, 300m, 300m), (schedule.Rows[0].Depreciation, schedule.Rows[1].Depreciation, schedule.Rows[2].Depreciation));
        Assert.Equal(200m, schedule.Rows[1].Posted);
        Assert.Equal(0m, schedule.Rows[^1].EndingBookValue);

        // Every asset's whole schedule adds up to cost less salvage: nothing taken twice, nothing left over.
        await using var db = await kit.Factory.CreateAsync();
        foreach (var asset in await db.Set<Workbench.Domain.Assets.FixedAsset>().AsNoTracking().ToListAsync())
        {
            Assert.Equal(asset.Cost - asset.SalvageValue, Workbench.Application.Assets.AssetSupport.Schedule(asset).Sum());
        }
    }

    [Fact]
    public async Task The_first_run_catches_up_months_since_the_cut_over_once_and_spreads_differences_over_the_remaining_life()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, nov, _) = await PeriodsAsync(kit);
        var comp = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");
        SaveAssetRequest Opening(string tag, DateOnly cutover, decimal accumulated) =>
            new($"Server {tag}", comp.Id, new DateOnly(2025, 10, 1), 12_000m, 0, 5, AssetDepreciationMethod.StraightLine, tag,
                OpeningBalanceDate: cutover, OpeningAccumulatedDepreciation: accumulated);

        // As of August: September and October are both Workbench's, caught up in October's run (200 a month).
        var august = await Assets(kit).CreateAsync(Opening("AUG", new DateOnly(2026, 8, 31), 2_200m));
        // As of September, but Costpoint took less than Workbench's schedule (2,000 instead of 2,400): the difference is
        // spread over the 48 months left (10,000 / 48), never caught up as a lump.
        var behind = await Assets(kit).CreateAsync(Opening("LOW", new DateOnly(2026, 9, 30), 2_000m));
        // As of October: Costpoint already took October, so October's run leaves it alone.
        var october = await Assets(kit).CreateAsync(Opening("OCT", new DateOnly(2026, 10, 31), 2_600m));

        var lines = (await Depreciation(kit).PreviewAsync(oct)).Lines.ToDictionary(l => l.TagNumber, l => l.Amount);
        Assert.Equal(400m, lines["AUG"]);
        Assert.Equal(208.33m, lines["LOW"]);
        Assert.False(lines.ContainsKey("OCT"));
        await Depreciation(kit).RunAsync(oct);

        var november = (await Depreciation(kit).PreviewAsync(nov)).Lines.ToDictionary(l => l.TagNumber, l => l.Amount);
        Assert.Equal((200m, 208.34m, 200m), (november["AUG"], november["LOW"], november["OCT"])); // 416.67 for two months, rounded cumulatively
        await Depreciation(kit).RunAsync(nov);
        Assert.Equal((2_800m, 2_416.67m, 2_800m), ((await Assets(kit).GetAsync(august.Asset.Id)).Asset.AccumulatedDepreciation,
            (await Assets(kit).GetAsync(behind.Asset.Id)).Asset.AccumulatedDepreciation, (await Assets(kit).GetAsync(october.Asset.Id)).Asset.AccumulatedDepreciation));

        // Opening balances are fixed once depreciation is posted; editing descriptive fields keeps them.
        var edited = await Assets(kit).UpdateAsync(august.Asset.Id, new SaveAssetRequest("Server AUG (rack 2)", comp.Id, new DateOnly(2025, 10, 1), 12_000m, 0, 5,
            AssetDepreciationMethod.StraightLine, "AUG"));
        Assert.Equal((new DateOnly(2026, 8, 31), 2_200m), (edited.OpeningBalanceDate!.Value, edited.OpeningAccumulatedDepreciation));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).UpdateAsync(august.Asset.Id, Opening("AUG", new DateOnly(2026, 8, 31), 2_000m)));
    }

    [Fact]
    public async Task Bad_rows_are_reported_in_the_dry_run_and_nothing_is_created()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var csv = Header
            + "A-1,Mid-month cut-over,COMP,01/01/2025,\"3,600.00\",900,,09/15/2026,36,\n"
            + "A-2,Doesn't add up,COMP,01/01/2025,3600,900,2000,09/30/2026,36,\n"
            + "A-3,Unknown category,BOATS,01/01/2025,3600,900,,09/30/2026,36,\n"
            + "A-4,Life over with value left,COMP,01/01/2020,3600,3000,,09/30/2026,36,\n"
            + "A-5,Not whole years,COMP,01/01/2025,3600,900,,09/30/2026,30,\n"
            + "A-6,More than cost,COMP,01/01/2025,3600,4000,,09/30/2026,36,\n"
            + "A-7,No balance,COMP,01/01/2025,3600,,,09/30/2026,36,\n"
            + "A-8,In service after cut-over,COMP,11/01/2026,3600,100,,09/30/2026,36,\n"
            + "A-9,Fine,COMP,01/01/2025,3600,2100,1500,09/30/2026,36,\n"
            + "A-9,Twice,COMP,01/01/2025,3600,2100,1500,09/30/2026,36,\n";
        var dry = await PrepareAsync(kit, Samples.Csv("bad.csv", csv));

        // Row numbers count the header line, so the first data row is 2.
        string ErrorFor(int row) => string.Join("; ", dry.Errors.Where(e => e.Row == row + 1 && !e.IsWarning).Select(e => e.Message));
        Assert.Contains("isn't a month end", ErrorFor(1));
        Assert.Contains("not the net book value", ErrorFor(2));
        Assert.Contains("BOATS doesn't exist", ErrorFor(3));
        Assert.Contains("useful life ended", ErrorFor(4));
        Assert.Contains("isn't whole years", ErrorFor(5));
        Assert.Contains("between zero and cost", ErrorFor(6));
        Assert.Contains("accumulated depreciation or the net book value", ErrorFor(7));
        Assert.Contains("wasn't in service before the cut-over", ErrorFor(8));
        Assert.Equal("", ErrorFor(9));
        Assert.Contains("more than once", ErrorFor(10));
        Assert.Equal((1, 9), (dry.Job.Counts.Creates, dry.Job.Counts.Errors));

        var done = await Imports(kit).ImportAsync(dry.Job.Id);
        Assert.Equal("A-9", Assert.Single(await Assets(kit).ListAsync(new AssetQuery())).TagNumber);
        Assert.Equal(1, done.Job.Counts.Creates);
    }

    [Fact]
    public async Task Re_importing_before_any_depreciation_updates_the_balances()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await ImportSampleAsync(kit);
        var changed = File.ReadAllText(Samples.PathOf(Sample)).Replace("\"3,000.00\",600.00", "\"2,900.00\",700.00");
        var dry = await PrepareAsync(kit, Samples.Csv("fixed-assets.csv", changed));
        Assert.Equal((0, 1, 4), (dry.Job.Counts.Creates, dry.Job.Counts.Updates, dry.Job.Counts.Skips));
        Assert.Contains(dry.Plan, p => p.Key == "CP-1001" && p.Summary.Contains("Accumulated depreciation"));

        var done = await Imports(kit).ImportAsync(dry.Job.Id);
        var laptop = await ByTagAsync(kit, "CP-1001");
        Assert.Equal((2_900m, 2_900m), (laptop.OpeningAccumulatedDepreciation, laptop.Asset.AccumulatedDepreciation));

        await Imports(kit).RollBackAsync(done.Job.Id);
        Assert.Equal(3_000m, (await ByTagAsync(kit, "CP-1001")).Asset.AccumulatedDepreciation);
    }

    [Fact]
    public async Task Asset_exports_record_a_security_event()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await ImportSampleAsync(kit);
        var reports = kit.Get<IAssetReportService>();
        var register = await reports.ExportRegisterCsvAsync();
        await reports.ExportRollForwardCsvAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31));
        await reports.ExportScheduleCsvAsync((await ByTagAsync(kit, "CP-1001")).Asset.Id);
        await reports.ExportGovernmentPropertyCsvAsync();
        await reports.GetRegisterAsync(new AssetQuery()); // reading isn't an export

        kit.As(Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => reports.ExportRegisterCsvAsync());

        await using var db = await kit.Factory.CreateAsync();
        var exports = await db.Set<SecurityEvent>().AsNoTracking().Where(e => e.EventType == SecurityEventTypes.ExportAssets).ToListAsync();
        Assert.Equal(4, exports.Count);
        Assert.All(exports, e => Assert.Equal(Finance, e.Actor));
        var registerEvent = Assert.Single(exports, e => e.Target == register.FileName);
        Assert.Equal("Asset register: 5 rows", registerEvent.Detail);
        Assert.Contains(await db.Set<SecurityEvent>().AsNoTracking().ToListAsync(), e => e.EventType == SecurityEventTypes.PermissionDenied && e.Actor == Owner);
    }

    [Fact]
    public async Task The_roll_forward_counts_opening_balances_as_depreciation_through_the_cut_over()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, _, _) = await PeriodsAsync(kit);
        await ImportSampleAsync(kit);
        await Depreciation(kit).RunAsync(oct);

        var roll = await kit.Get<IAssetReportService>().GetRollForwardAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal((91_800m, 0m), (roll.Totals.Beginning, roll.Totals.Additions)); // cost less the opening balances
        Assert.Equal(1_566.67m, roll.Totals.Depreciation);
        Assert.Equal(91_800m - 1_566.67m, roll.Totals.Ending);
    }
}

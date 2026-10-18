using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assets;

/// <summary>Fixed assets over <see cref="PurchasingKit"/> (period 2026-10 open; 2026-11 added here), as finance@.</summary>
public sealed class AssetTests
{
    private static readonly DateOnly Day = PurchasingKit.Day;

    private static IAssetService Assets(PurchasingKit kit) => kit.Get<IAssetService>();

    private static IDepreciationService Depreciation(PurchasingKit kit) => kit.Get<IDepreciationService>();

    private static async Task<(Guid Oct, Guid Nov)> PeriodsAsync(PurchasingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        var oct = await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-10");
        var nov = new FiscalPeriod { FiscalYearId = oct.FiscalYearId, Name = "2026-11", Number = 2, Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 30) };
        db.Set<FiscalPeriod>().Add(nov);
        await db.SaveChangesAsync();
        return (oct.Id, nov.Id);
    }

    private static async Task<AssetCategoryDto> CategoryAsync(PurchasingKit kit, string code = "COMP") =>
        (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == code);

    private static async Task<AssetDto> AssetAsync(PurchasingKit kit, decimal cost, DateOnly acquired, AssetDepreciationMethod method = AssetDepreciationMethod.StraightLine,
        int life = 5, decimal salvage = 0, string description = "Server") =>
        await Assets(kit).CreateAsync(new SaveAssetRequest(description, (await CategoryAsync(kit)).Id, acquired, cost, salvage, life, method, OrgUnitId: kit.OrgId));

    [Fact]
    public async Task The_depreciation_run_posts_a_balanced_entry_and_is_idempotent()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, _) = await PeriodsAsync(kit);
        var sl = await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 3));
        var db = await AssetAsync(kit, 10_000m, new DateOnly(2026, 10, 3), AssetDepreciationMethod.DecliningBalance, salvage: 1_000m, description: "Test rig");
        var syd = await AssetAsync(kit, 10_000m, new DateOnly(2026, 10, 3), AssetDepreciationMethod.SumOfYearsDigits, salvage: 1_000m, description: "Lab bench");
        var gfp = await Assets(kit).CreateAsync(new SaveAssetRequest("Government spectrum analyzer", (await CategoryAsync(kit)).Id, Day, 40_000m,
            IsGovernmentFurnished: true, ContractId: kit.Fx.Contract1, GovernmentPropertyNumber: "W91-0042"));

        var preview = await Depreciation(kit).PreviewAsync(oct);
        Assert.Equal(3, preview.Lines.Count);
        Assert.Equal(200m + 333.33m + 250m, preview.Total);
        Assert.DoesNotContain(preview.Lines, l => l.AssetId == gfp.Asset.Id);

        var run = await Depreciation(kit).RunAsync(oct);
        Assert.NotNull(run);
        Assert.Equal((783.33m, 3), (run!.Total, run.AssetCount));
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Depreciation));
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        Assert.Equal(783.33m, entry.TotalDebit);
        Assert.All(entry.Lines.Where(l => l.Debit > 0), l => Assert.Equal((kit.Account("6140"), kit.OrgId), (l.AccountId, l.OrgUnitId!.Value)));
        Assert.All(entry.Lines.Where(l => l.Credit > 0), l => Assert.Equal(kit.Account("1590"), l.AccountId));
        Assert.Equal(-783.33m, await kit.BalanceAsync("1590"));

        // Running again posts nothing.
        Assert.Null(await Depreciation(kit).RunAsync(oct));
        Assert.Single(await kit.EntriesAsync(JournalSource.Depreciation));
        Assert.Empty((await Depreciation(kit).PreviewAsync(oct)).Lines);
        Assert.Equal(200m, (await Assets(kit).GetAsync(sl.Asset.Id)).Asset.AccumulatedDepreciation);

        // An asset added later is picked up by a second run for the same period.
        await AssetAsync(kit, 3_600m, new DateOnly(2026, 10, 20), life: 3, description: "Laptop");
        var late = await Depreciation(kit).RunAsync(oct);
        Assert.Equal((100m, 1), (late!.Total, late.AssetCount));
        Assert.Equal(2, (await Depreciation(kit).PreviewAsync(oct)).PostedRuns.Count);
    }

    [Fact]
    public async Task Depreciation_is_blocked_for_a_closed_period_and_the_close_checklist_requires_it()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, _) = await PeriodsAsync(kit);
        await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 1));

        var checklist = await kit.Get<IPeriodCloseService>().GetChecklistAsync(oct);
        var check = checklist.Checks.Single(c => c.Kind == CloseCheckKind.DepreciationPosted);
        Assert.False(check.Passed);
        Assert.Contains("1 asset(s) not depreciated", check.Summary);

        await Depreciation(kit).RunAsync(oct);
        checklist = await kit.Get<IPeriodCloseService>().GetChecklistAsync(oct);
        Assert.True(checklist.Checks.Single(c => c.Kind == CloseCheckKind.DepreciationPosted).Passed);

        await AssetAsync(kit, 6_000m, new DateOnly(2026, 10, 5), description: "Second server");
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<FiscalPeriod>().FirstAsync(p => p.Id == oct)).Status = PeriodStatus.Closed;
            await db.SaveChangesAsync();
        }

        var closed = await Assert.ThrowsAsync<ArgumentException>(() => Depreciation(kit).RunAsync(oct));
        Assert.Contains("closed", closed.Message);
        Assert.Single(await kit.EntriesAsync(JournalSource.Depreciation));
    }

    [Fact]
    public async Task A_mid_life_disposal_catches_up_depreciation_and_books_the_loss()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, nov) = await PeriodsAsync(kit);

        // Straight line 12,000 over 5 years = 200 a month, in service May 2025.
        var asset = await AssetAsync(kit, 12_000m, new DateOnly(2025, 5, 12));

        // Disposal needs depreciation through October first.
        var notYet = await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 11, 15), 8_000m, "Sold")));
        Assert.Contains("Run depreciation", notYet.Message);

        // The first run catches up May 2025 – Oct 2026: 18 months × 200.
        var run = await Depreciation(kit).RunAsync(oct);
        Assert.Equal(3_600m, run!.Total);

        // Sold in November for 8,000: book value 12,000 − 3,600 = 8,400, a 400 loss (no November depreciation).
        var disposed = await Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 11, 15), 8_000m, "Sold to employee"));
        Assert.Equal(AssetState.Disposed, disposed.Asset.Status);
        Assert.Equal(0m, disposed.Asset.NetBookValue);
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        Assert.Equal(3_600m, entry.Lines.Single(l => l.AccountId == kit.Account("1590")).Debit);
        Assert.Equal(8_000m, entry.Lines.Single(l => l.AccountId == kit.Account("1010")).Debit);
        Assert.Equal(400m, entry.Lines.Single(l => l.AccountId == kit.Account("4900")).Debit);
        Assert.Equal(12_000m, entry.Lines.Single(l => l.AccountId == kit.Account("1510")).Credit);
        Assert.Equal(0m, await kit.BalanceAsync("1590"));

        // Disposed assets aren't depreciated again, and can't be disposed twice.
        Assert.Empty((await Depreciation(kit).PreviewAsync(nov)).Lines);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 11, 20), 0, "Again")));

        // The roll-forward ties out: 0 + 12,000 − 3,600 − 8,400 = 0.
        var roll = await kit.Get<IAssetReportService>().GetRollForwardAsync(new DateOnly(2025, 1, 1), new DateOnly(2026, 11, 30));
        Assert.Equal((0m, 12_000m, 3_600m, 8_400m, 0m), (roll.Totals.Beginning, roll.Totals.Additions, roll.Totals.Depreciation, roll.Totals.Disposals, roll.Totals.Ending));
    }

    [Fact]
    public async Task A_disposal_above_book_value_books_a_gain()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, _) = await PeriodsAsync(kit);
        var asset = await AssetAsync(kit, 6_000m, new DateOnly(2026, 10, 1), life: 5);
        await Depreciation(kit).RunAsync(oct);
        await Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 11, 2), 6_100m, "Sold"));

        // Book value 6,000 − 100 = 5,900; proceeds 6,100: a 200 gain.
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        Assert.Equal(200m, entry.Lines.Single(l => l.AccountId == kit.Account("4900")).Credit);
    }

    [Fact]
    public async Task A_posted_bill_line_over_the_threshold_is_capitalized_with_a_reclass_entry()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", Day,
            [kit.OverheadLine(6_400m, "6230", "Two engineering laptops"), kit.OverheadLine(900m, "6230", "Docking stations"), PurchasingKit.ProjectLine(kit.Fx.Wbs1, 7_000m)]));

        var lines = await Assets(kit).ListCapitalizableBillLinesAsync();
        var capitalizable = Assert.Single(lines);
        Assert.Equal((bill.Lines[0].Id, 6_400m, "6230"), (capitalizable.BillLineId, capitalizable.Amount, capitalizable.AccountNumber));

        var category = await CategoryAsync(kit);
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[1].Id, category.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[2].Id, category.Id)));

        var asset = await Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[0].Id, category.Id, SerialNumber: "SN-1"));
        Assert.Equal((6_400m, Day, "Two engineering laptops", kit.Prima.Id), (asset.Asset.Cost, asset.Asset.AcquisitionDate, asset.Asset.Description, asset.VendorId!.Value));
        Assert.Equal(kit.OrgId, asset.OrgUnitId);
        Assert.Equal($"{bill.Bill.Number} line 1", asset.SourceBillNumber);
        Assert.Equal((AssetDepreciationMethod.StraightLine, 3), (asset.Asset.Method, asset.Asset.UsefulLifeYears));

        var reclass = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        Assert.Equal(asset.CapitalizationEntryId, reclass.Id);
        Assert.Equal(6_400m, reclass.Lines.Single(l => l.AccountId == kit.Account("1510")).Debit);
        Assert.Equal(6_400m, reclass.Lines.Single(l => l.AccountId == kit.Account("6230")).Credit);
        Assert.Equal(6_400m, await kit.BalanceAsync("1510"));
        Assert.Equal(900m, await kit.BalanceAsync("6230"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[0].Id, category.Id)));
        Assert.Empty(await Assets(kit).ListCapitalizableBillLinesAsync());
    }

    [Fact]
    public async Task A_line_already_on_the_asset_account_capitalizes_without_a_reclass()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", Day, [kit.OverheadLine(5_000m, "1510", "Workstation")]));
        var asset = await Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[0].Id, (await CategoryAsync(kit)).Id));
        Assert.Null(asset.CapitalizationEntryId);
        Assert.Empty(await kit.EntriesAsync(JournalSource.Asset));
    }

    [Fact]
    public async Task Transfers_keep_history_and_government_property_is_listed_but_not_depreciated()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var category = await CategoryAsync(kit);
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CreateAsync(new SaveAssetRequest("GFP without contract", category.Id, Day, 1_000m, IsGovernmentFurnished: true)));
        var gfp = await Assets(kit).CreateAsync(new SaveAssetRequest("Spectrum analyzer", category.Id, Day, 40_000m, IsGovernmentFurnished: true,
            ContractId: kit.Fx.Contract1, GovernmentPropertyNumber: "W91-0042", Location: "Lab 1", SerialNumber: "SA-77"));

        var moved = await Assets(kit).TransferAsync(gfp.Asset.Id, new TransferAssetRequest(Day, "Lab 2", kit.Fx.AnalystId, kit.OrgId, kit.Fx.Project1, "Field test"));
        var transfer = Assert.Single(moved.Transfers);
        Assert.Equal(("Lab 1", "Lab 2", "Alex Rivera", "1001"), (transfer.FromLocation, transfer.ToLocation, transfer.ToCustodian, transfer.ToProject));
        Assert.Equal("Alex Rivera", moved.Asset.CustodianName);

        var listing = Assert.Single(await kit.Get<IAssetReportService>().GetGovernmentPropertyAsync());
        Assert.Equal(("GFP", "W91-0042", "C1", "Lab 2"), (listing.Kind, listing.GovernmentPropertyNumber, listing.ContractNumber, listing.Location));
        var csv = await kit.Get<IAssetReportService>().ExportGovernmentPropertyCsvAsync();
        Assert.Contains("W91-0042", csv.Content);

        var returned = await Assets(kit).TransferAsync(gfp.Asset.Id, new TransferAssetRequest(Day, null, null, null, null, "Returned to the government", TransferOut: true));
        Assert.Equal(AssetState.Transferred, returned.Asset.Status);

        // Contractor-owned assets are disposed of, not transferred out.
        var own = await AssetAsync(kit, 6_000m, Day);
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).TransferAsync(own.Asset.Id, new TransferAssetRequest(Day, null, null, null, null, null, TransferOut: true)));
    }

    [Fact]
    public async Task Schedules_show_book_and_tax_years_and_the_register_exports()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var asset = await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 1), life: 3);
        var book = await kit.Get<IAssetReportService>().GetScheduleAsync(asset.Asset.Id);

        // 12,000 / 36 = 333.33 a month: 3 months in 2026, then 12, 12, and 9.
        Assert.Equal([2026, 2027, 2028, 2029], book.Rows.Select(r => r.Year));
        Assert.Equal(999.99m, book.Rows[0].Depreciation);
        Assert.Equal(0m, book.Rows[^1].EndingBookValue);

        var tax = await kit.Get<IAssetReportService>().GetScheduleAsync(asset.Asset.Id, DepreciationBook.Tax);
        Assert.Equal([2_400m, 3_840m, 2_304m, 1_382.40m, 1_382.40m, 691.20m], tax.Rows.Select(r => r.Depreciation));

        var csv = await kit.Get<IAssetReportService>().ExportRegisterCsvAsync();
        Assert.Contains(asset.Asset.TagNumber, csv.Content);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    public async Task Fixed_assets_are_finance_and_admin_only(string email)
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var (oct, _) = await PeriodsAsync(kit);
        var asset = await AssetAsync(kit, 6_000m, Day);
        kit.As(email);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).ListAsync(new AssetQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).GetAsync(asset.Asset.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(Day, 0, "x")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Depreciation(kit).RunAsync(oct));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Get<IAssetReportService>().ExportRegisterCsvAsync());

        kit.As(Admin);
        Assert.Single(await Assets(kit).ListAsync(new AssetQuery()));
    }
}

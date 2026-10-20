using Microsoft.EntityFrameworkCore;
using Workbench.Application.Companies;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Domain.Accounting;
using Workbench.Domain.Companies;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assets;

/// <summary>Tax books, transfers (with postings, including across companies) and disposals per book, over <see cref="PurchasingKit"/> as finance@.</summary>
public sealed class AssetTaxBookTests
{
    private static IAssetService Assets(PurchasingKit kit) => kit.Get<IAssetService>();

    private static IAssetTaxBookService Books(PurchasingKit kit) => kit.Get<IAssetTaxBookService>();

    private static async Task<AssetDto> AssetAsync(PurchasingKit kit, decimal cost, DateOnly acquired, int life = 5, string description = "Server", Guid? org = null)
    {
        var category = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");
        return await Assets(kit).CreateAsync(new SaveAssetRequest(description, category.Id, acquired, cost, 0, life, AssetDepreciationMethod.StraightLine, OrgUnitId: org ?? kit.OrgId));
    }

    private static Task<AssetTaxBookDto> FederalAsync(PurchasingKit kit, Guid assetId, decimal s179 = 0, decimal bonus = 0, TaxConventionKind convention = TaxConventionKind.HalfYear) =>
        Books(kit).SaveBookAsync(assetId, new SaveAssetTaxBookRequest(AssetBookKind.Federal, TaxMethod.MacrsGds, 5, convention, s179, bonus));

    [Fact]
    public async Task An_asset_keeps_its_book_and_a_federal_book_side_by_side_and_only_the_book_posts()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var asset = await AssetAsync(kit, 60_000m, new DateOnly(2026, 10, 5));
        var federal = await FederalAsync(kit, asset.Asset.Id, s179: 10_000m, bonus: 40m);
        Assert.Equal((60_000m, true, 20_000m), (federal.Basis, federal.BasisIsAssetCost, federal.BonusAmount));

        var books = await Books(kit).GetBooksAsync(asset.Asset.Id);
        Assert.Equal(10, books.TaxYearStartMonth); // The default company's fiscal year starts in October.
        Assert.Equal([AssetBookKind.Book, AssetBookKind.Federal], books.Schedules.Select(s => s.Book).ToArray());

        // Book: 60,000 straight line over 5 years = 12,000 per fiscal year (Oct–Sep), from October 2026.
        var book = books.Schedules[0];
        Assert.Equal((2026, 12_000m), (book.Rows[0].TaxYear, book.Rows[0].Depreciation));
        Assert.Equal(60_000m, book.Rows.Sum(r => r.Total));

        // Federal: 10,000 Section 179 + 40% × 50,000 bonus + 20% × 30,000 MACRS in year one.
        var tax = books.Schedules[1];
        Assert.Equal((10_000m, 20_000m, 6_000m), (tax.Rows[0].Section179, tax.Rows[0].Bonus, tax.Rows[0].Depreciation));
        Assert.Equal(60_000m, tax.Rows.Sum(r => r.Total));

        // Tax books never post.
        Assert.Empty(await kit.EntriesAsync());

        // The book (GAAP) itself can't be saved here, and government property has no tax books.
        await Assert.ThrowsAsync<ArgumentException>(() => Books(kit).SaveBookAsync(asset.Asset.Id, new SaveAssetTaxBookRequest(AssetBookKind.Book, TaxMethod.MacrsGds, 5, TaxConventionKind.HalfYear)));
        var category = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");
        var gfp = await Assets(kit).CreateAsync(new SaveAssetRequest("Government analyzer", category.Id, PurchasingKit.Day, 40_000m, IsGovernmentFurnished: true, ContractId: kit.Fx.Contract1));
        await Assert.ThrowsAsync<ArgumentException>(() => FederalAsync(kit, gfp.Asset.Id));

        // Saving again replaces the book; deleting removes it.
        await Books(kit).SaveBookAsync(asset.Asset.Id, new SaveAssetTaxBookRequest(AssetBookKind.Federal, TaxMethod.MacrsAds, 6, TaxConventionKind.HalfYear));
        Assert.Equal(TaxMethod.MacrsAds, Assert.Single((await Books(kit).GetBooksAsync(asset.Asset.Id)).Books).Method);
        await Books(kit).DeleteBookAsync(asset.Asset.Id, AssetBookKind.Federal);
        Assert.Empty((await Books(kit).GetBooksAsync(asset.Asset.Id)).Books);
    }

    [Fact]
    public async Task Tax_reports_give_the_schedule_book_to_tax_differences_Form_4562_and_the_mid_quarter_test()
    {
        await using var kit = await PurchasingKit.CreateAsync();

        // FY2026 runs Oct 2026 – Sep 2027. Q1 (October): 12,000 less 2,000 Section 179; Q4 (August): 30,000 → 75% in the last quarter.
        var early = await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 5), description: "Early server");
        var late = await AssetAsync(kit, 30_000m, new DateOnly(2027, 8, 10), description: "Late server");
        await FederalAsync(kit, early.Asset.Id, s179: 2_000m);
        await FederalAsync(kit, late.Asset.Id);

        var test = await Books(kit).GetMidQuarterTestAsync(new TaxReportQuery(2026));
        Assert.True(test.MidQuarterRequired);
        Assert.Equal([10_000m, 0m, 0m, 30_000m], test.BasisByQuarter.ToArray()); // Section 179 comes off the basis.
        Assert.Equal(75m, test.LastQuarterPercent);
        Assert.All(test.Assets, a => Assert.False(a.ConventionMatches)); // Both use half-year but mid-quarter applies.

        var schedule = await Books(kit).GetDepreciationReportAsync(new TaxReportQuery(2026));
        Assert.Equal(2, schedule.Rows.Count);
        var row = schedule.Rows.Single(r => r.AssetId == early.Asset.Id);
        Assert.Equal((2_000m, 2_000m, 4_000m, 8_000m), (row.Section179, row.CurrentDepreciation, row.AccumulatedDepreciation, row.EndingBasis));
        Assert.Equal(new DateOnly(2026, 10, 1), schedule.Start);

        var form = await Books(kit).GetForm4562Async(new TaxReportQuery(2026));
        Assert.Equal((12_000m, 2_000m, 2_000m, 0m), (form.Line2TotalCost, form.Line8ElectedCost, form.Line12Section179Deduction, form.Line17PriorYearMacrs));
        var fiveYear = Assert.Single(form.Line19Gds);
        Assert.Equal(("19b", 40_000m, "HY", "200DB", 2_000m + 6_000m), (fiveYear.Line, fiveYear.Basis, fiveYear.Convention, fiveYear.Method, fiveYear.Deduction));
        Assert.Equal(2_000m + 8_000m, form.Line22Total);
        Assert.True(form.MidQuarterRequired);

        // Book: 12,000 / 60 months × 12 = 2,400 for the early server; 30,000 / 60 × 2 (Aug, Sep) = 1,000 for the late one.
        var diff = await Books(kit).GetBookTaxDifferenceAsync(new TaxReportQuery(2026));
        var e = diff.Rows.Single(r => r.AssetId == early.Asset.Id);
        Assert.Equal((2_400m, 4_000m, 1_600m, 9_600m, 8_000m, 1_600m, 336m), (e.BookDepreciation, e.TaxDepreciation, e.Difference, e.BookNetValue, e.TaxBasis, e.TemporaryDifference, e.DeferredTax));
        Assert.Equal(1_000m, diff.Rows.Single(r => r.AssetId == late.Asset.Id).BookDepreciation);
        Assert.Equal(diff.Rows.Sum(r => r.DeferredTax), diff.Totals.DeferredTax);

        var csv = await Books(kit).ExportBookTaxDifferenceCsvAsync(new TaxReportQuery(2026));
        Assert.Contains("Early server", csv.Content);
    }

    [Fact]
    public async Task Disposal_computes_the_gain_or_loss_in_every_book()
    {
        await using var kit = await PurchasingKit.CreateAsync();

        // In service October 2026: no depreciation is due before an October disposal, so book value is cost.
        var asset = await AssetAsync(kit, 20_000m, new DateOnly(2025, 10, 1));
        await FederalAsync(kit, asset.Asset.Id, bonus: 50m);
        await Books(kit).SaveBookAsync(asset.Asset.Id, new SaveAssetTaxBookRequest(AssetBookKind.State, TaxMethod.MacrsGds, 5, TaxConventionKind.HalfYear));
        await using (var db = await kit.Factory.CreateAsync())
        {
            // Post FY2025's 12 months of book depreciation (4,000) as if the runs happened.
            var a = await db.Set<Workbench.Domain.Assets.FixedAsset>().FirstAsync(x => x.Id == asset.Asset.Id);
            a.AccumulatedDepreciation = 4_000m;
            await db.SaveChangesAsync();
        }

        var disposed = await Assets(kit).DisposeAsync(asset.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 10, 20), 13_000m, "Sold to a reseller"));
        Assert.Equal(AssetDisposalType.Sale, disposed.DisposalKind);
        var byBook = disposed.BookDisposals!.ToDictionary(d => d.Book);

        // Book: 13,000 − (20,000 − 4,000) = 3,000 loss.
        Assert.Equal((16_000m, -3_000m), (byBook[AssetBookKind.Book].AdjustedBasis, byBook[AssetBookKind.Book].GainLoss));

        // Federal: 50% bonus (10,000) + 20% × 10,000 in FY2025 = 12,000; FY2026 half of 32% × 10,000 = 1,600. Basis 6,400 → 6,600 gain.
        Assert.Equal((1_600m, 6_400m, 6_600m), (byBook[AssetBookKind.Federal].DisposalYearDepreciation, byBook[AssetBookKind.Federal].AdjustedBasis, byBook[AssetBookKind.Federal].GainLoss));

        // State (no bonus): 4,000 in FY2025, half of 6,400 = 3,200 in FY2026. Basis 12,800 → 200 gain.
        Assert.Equal((12_800m, 200m), (byBook[AssetBookKind.State].AdjustedBasis, byBook[AssetBookKind.State].GainLoss));

        // Only the book posted: one disposal entry with the 3,000 loss.
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        Assert.Equal(3_000m, entry.Lines.Single(l => l.AccountId == kit.Account("4900")).Debit);

        // The disposed asset's tax books are closed.
        await Assert.ThrowsAsync<InvalidOperationException>(() => FederalAsync(kit, asset.Asset.Id));
    }

    [Fact]
    public async Task A_trade_in_debits_the_chosen_account_and_a_scrap_has_no_proceeds_line()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var traded = await AssetAsync(kit, 5_000m, new DateOnly(2026, 10, 1), description: "Old laptop");
        var scrapped = await AssetAsync(kit, 3_000m, new DateOnly(2026, 10, 1), description: "Broken monitor");

        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).DisposeAsync(traded.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 10, 15), 0, "Trade", AssetDisposalType.TradeIn)));
        var trade = await Assets(kit).DisposeAsync(traded.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 10, 15), 1_200m, "Traded toward the new model", AssetDisposalType.TradeIn, kit.Account("2010")));
        Assert.Equal(AssetDisposalType.TradeIn, trade.DisposalKind);
        var tradeEntry = (await kit.EntriesAsync(JournalSource.Asset)).Single(e => e.SourceId == traded.Asset.Id);
        Assert.Equal(1_200m, tradeEntry.Lines.Single(l => l.AccountId == kit.Account("2010")).Debit);
        Assert.Equal(3_800m, tradeEntry.Lines.Single(l => l.AccountId == kit.Account("4900")).Debit);

        await Assets(kit).DisposeAsync(scrapped.Asset.Id, new DisposeAssetRequest(new DateOnly(2026, 10, 15), 0, "Scrapped", AssetDisposalType.Scrap));
        var scrapEntry = (await kit.EntriesAsync(JournalSource.Asset)).Single(e => e.SourceId == scrapped.Asset.Id);
        Assert.DoesNotContain(scrapEntry.Lines, l => l.AccountId == kit.Account("1010"));
        Assert.Equal(3_000m, scrapEntry.Lines.Single(l => l.AccountId == kit.Account("4900")).Debit);
    }

    [Fact]
    public async Task An_organization_transfer_reclasses_cost_and_accumulated_depreciation()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        Guid eng;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var org = new OrgUnit { Code = "ENG", Name = "Engineering" };
            db.Set<OrgUnit>().Add(org);
            await db.SaveChangesAsync();
            eng = org.Id;
        }

        var asset = await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 1));
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<Workbench.Domain.Assets.FixedAsset>().FirstAsync(x => x.Id == asset.Asset.Id)).AccumulatedDepreciation = 200m;
            await db.SaveChangesAsync();
        }

        var moved = await Assets(kit).TransferAsync(asset.Asset.Id, new TransferAssetRequest(new DateOnly(2026, 10, 20), "Lab 2", null, eng, null, "Moved to engineering"));
        var transfer = Assert.Single(moved.Transfers);
        Assert.Equal(("OPS", "ENG"), (transfer.FromOrgUnit, transfer.ToOrgUnit));
        Assert.NotNull(transfer.JournalEntryId);

        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        Assert.Equal(12_000m, entry.Lines.Single(l => l.AccountId == kit.Account("1510") && l.OrgUnitId == kit.OrgId).Credit);
        Assert.Equal(12_000m, entry.Lines.Single(l => l.AccountId == kit.Account("1510") && l.OrgUnitId == eng).Debit);
        Assert.Equal(200m, entry.Lines.Single(l => l.AccountId == kit.Account("1590") && l.OrgUnitId == kit.OrgId).Debit);
        Assert.Equal(200m, entry.Lines.Single(l => l.AccountId == kit.Account("1590") && l.OrgUnitId == eng).Credit);

        // A location-only move posts nothing; the history lists both.
        await Assets(kit).TransferAsync(asset.Asset.Id, new TransferAssetRequest(new DateOnly(2026, 10, 25), "Lab 3", null, eng, null, "New bench"));
        Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        var history = await Assets(kit).ListTransfersAsync();
        Assert.Equal(new[] { "Lab 3", "Lab 2" }, history.Select(h => h.Transfer.ToLocation ?? "").ToArray());
    }

    [Fact]
    public async Task A_cross_company_transfer_moves_the_asset_through_the_intercompany_accounts()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var fed = new Company { Code = "FED", LegalName = "Federal Solutions LLC", CreatedAtUtc = DateTime.UtcNow };
        await using (var db = await kit.Factory.CreateAsync())
        {
            await CompanyService.EnsureDefaultAsync(db, default);
            db.Set<Company>().Add(fed);
            await db.SaveChangesAsync();
        }

        var asset = await AssetAsync(kit, 12_000m, new DateOnly(2026, 10, 1));
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<Workbench.Domain.Assets.FixedAsset>().FirstAsync(x => x.Id == asset.Asset.Id)).AccumulatedDepreciation = 200m;
            await db.SaveChangesAsync();
        }

        // Without intercompany accounts the ledger refuses the entry and nothing changes.
        var request = new TransferAssetRequest(new DateOnly(2026, 11, 1), null, null, kit.OrgId, null, "Sold to the subsidiary at book value", CompanyId: fed.Id);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).TransferAsync(asset.Asset.Id, request with { TransferredOn = new DateOnly(2026, 10, 20) }));
        Assert.Contains("intercompany", refused.Message);

        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<IntercompanyAccount>().AddRange(
                new IntercompanyAccount { CompanyId = Company.DefaultId, CounterpartyCompanyId = fed.Id, DueFromAccountId = kit.Account("1180"), DueToAccountId = kit.Account("2180") },
                new IntercompanyAccount { CompanyId = fed.Id, CounterpartyCompanyId = Company.DefaultId, DueFromAccountId = kit.Account("1180"), DueToAccountId = kit.Account("2180") });
            await db.SaveChangesAsync();
        }

        var moved = await Assets(kit).TransferAsync(asset.Asset.Id, request with { TransferredOn = new DateOnly(2026, 10, 20) });
        Assert.Equal((fed.Id, "FED"), (moved.CompanyId, moved.CompanyCode));
        Assert.Equal(("MAIN", "FED"), (moved.Transfers[0].FromCompany, moved.Transfers[0].ToCompany));

        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Asset));
        var main = entry.Lines.Where(l => l.CompanyId == Company.DefaultId).ToList();
        var sub = entry.Lines.Where(l => l.CompanyId == fed.Id).ToList();
        Assert.Equal(0m, main.Sum(l => l.Debit - l.Credit));
        Assert.Equal(0m, sub.Sum(l => l.Debit - l.Credit));

        // MAIN gives up the asset (Cr 1510 12,000, Dr 1590 200) and is owed the 11,800 book value; FED takes it on and owes it.
        Assert.Equal(12_000m, main.Single(l => l.AccountId == kit.Account("1510")).Credit);
        Assert.Equal(11_800m, main.Single(l => l.AccountId == kit.Account("1180")).Debit);
        Assert.Equal(12_000m, sub.Single(l => l.AccountId == kit.Account("1510")).Debit);
        Assert.Equal(200m, sub.Single(l => l.AccountId == kit.Account("1590")).Credit);
        Assert.Equal(11_800m, sub.Single(l => l.AccountId == kit.Account("2180")).Credit);
    }

    [Fact]
    public async Task Government_property_moves_between_contracts_without_a_posting()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var category = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");
        var gfp = await Assets(kit).CreateAsync(new SaveAssetRequest("Government analyzer", category.Id, PurchasingKit.Day, 40_000m, IsGovernmentFurnished: true,
            ContractId: kit.Fx.Contract1, ProjectId: kit.Fx.Project1));
        Guid other;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var c1 = await db.Set<Contract>().AsNoTracking().FirstAsync(c => c.Id == kit.Fx.Contract1);
            var c2 = new Contract { CustomerId = c1.CustomerId, ContractNumber = "W91-0099", Name = "Follow-on", PeriodStart = c1.PeriodStart, PeriodEnd = c1.PeriodEnd };
            db.Set<Contract>().Add(c2);
            await db.SaveChangesAsync();
            other = c2.Id;
        }

        var moved = await Assets(kit).TransferAsync(gfp.Asset.Id, new TransferAssetRequest(PurchasingKit.Day, "Site B", null, null, null, "Moved to the follow-on contract", ContractId: other));
        Assert.Equal(other, moved.ContractId);
        Assert.Equal("W91-0099", moved.Transfers[0].ToContract);
        Assert.Null(moved.Transfers[0].JournalEntryId);
        Assert.Empty(await kit.EntriesAsync(JournalSource.Asset));
    }

    [Fact]
    public async Task Tax_books_are_Finance_or_Admin_only()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var asset = await AssetAsync(kit, 10_000m, new DateOnly(2026, 10, 1));
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Books(kit).GetBooksAsync(asset.Asset.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Books(kit).GetForm4562Async(new TaxReportQuery(2026)));
    }
}

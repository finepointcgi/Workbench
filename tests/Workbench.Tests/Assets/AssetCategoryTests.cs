using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Assets;

/// <summary>The asset category editor's service: add, change, deactivate and delete, with validation, audit and gating.</summary>
public sealed class AssetCategoryTests
{
    private static IAssetService Assets(PurchasingKit kit) => kit.Get<IAssetService>();

    private static SaveAssetCategoryRequest Vehicles(PurchasingKit kit, string code = "veh", bool active = true, decimal? threshold = 2_500m) => new(
        code, "Vehicles", AssetDepreciationMethod.DecliningBalance, 5, kit.Account("1520"), kit.Account("1590"), kit.Account("6140"),
        MacrsRecoveryYears: 5, IsActive: active, Convention: AssetDepreciationConvention.MidMonth, CapitalizationThreshold: threshold);

    [Fact]
    public async Task Finance_adds_changes_deactivates_and_deletes_categories_with_an_audit_trail()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        Assert.Contains(await Assets(kit).ListCategoriesAsync(), c => c.Code == "COMP"); // defaults are created lazily

        var created = await Assets(kit).SaveCategoryAsync(null, Vehicles(kit));
        Assert.Equal(("VEH", AssetDepreciationConvention.MidMonth, 2_500m, "1520", 0), (created.Code, created.Convention, created.CapitalizationThreshold, created.AssetAccountNumber, created.AssetCount));

        var renamed = await Assets(kit).SaveCategoryAsync(created.Id, Vehicles(kit) with { Name = "Fleet vehicles", CapitalizationThreshold = null });
        Assert.Equal(("Fleet vehicles", (decimal?)null), (renamed.Name, renamed.CapitalizationThreshold));

        // An asset in the category: it can be deactivated (no new assets) but not deleted.
        var asset = await Assets(kit).CreateAsync(new SaveAssetRequest("Pickup truck", created.Id, PurchasingKit.Day, 40_000m));
        Assert.Equal((AssetDepreciationMethod.DecliningBalance, AssetDepreciationConvention.MidMonth), (asset.Asset.Method, asset.Convention));
        Assert.Equal(1, (await Assets(kit).ListCategoriesAsync()).Single(c => c.Id == created.Id).AssetCount);
        var inUse = await Assert.ThrowsAsync<InvalidOperationException>(() => Assets(kit).DeleteCategoryAsync(created.Id));
        Assert.Contains("deactivate", inUse.Message);

        var inactive = await Assets(kit).SaveCategoryAsync(created.Id, Vehicles(kit, active: false));
        Assert.False(inactive.IsActive);
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CreateAsync(new SaveAssetRequest("Van", created.Id, PurchasingKit.Day, 30_000m)));
        var kept = await Assets(kit).UpdateAsync(asset.Asset.Id, new SaveAssetRequest("Pickup truck (blue)", created.Id, PurchasingKit.Day, 40_000m));
        Assert.Equal("Pickup truck (blue)", kept.Asset.Description); // existing assets keep working

        // An unused category is deleted.
        var spare = await Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "SPARE"));
        await Assets(kit).DeleteCategoryAsync(spare.Id);
        Assert.DoesNotContain(await Assets(kit).ListCategoriesAsync(), c => c.Code == "SPARE");
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Assets(kit).DeleteCategoryAsync(spare.Id));

        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().Where(e => e.EntityType == "AssetCategory").ToListAsync();
        Assert.Equal(["Created", "Deactivated", "Updated"], audit.Where(e => e.EntityId == created.Id).Select(e => e.Action).Order());
        Assert.Equal(["Created", "Deleted"], audit.Where(e => e.EntityId == spare.Id).Select(e => e.Action).Order());
        Assert.All(audit, e => Assert.Equal(Finance, e.Actor));
    }

    [Fact]
    public async Task Category_changes_are_validated()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await Assets(kit).SaveCategoryAsync(null, Vehicles(kit));

        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "VEH"))); // duplicate code
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X1", threshold: -1)));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X2") with { DefaultLifeYears = 0 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X3") with { MacrsRecoveryYears = 4 }));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X4") with { AccumulatedDepreciationAccountId = kit.Account("1520") }));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X5") with { ExpenseAccountId = kit.Account("1510") })); // not an expense account
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X6") with { AssetAccountId = kit.Account("1500") })); // a parent account
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "X7") with { Convention = (AssetDepreciationConvention)9 }));
        Assert.DoesNotContain(await Assets(kit).ListCategoriesAsync(), c => c.Code.StartsWith('X'));
    }

    [Fact]
    public async Task Only_finance_and_admins_manage_categories()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var comp = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");

        foreach (var user in new[] { Owner, Viewer, Timekeeper })
        {
            kit.As(user);
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).ListCategoriesAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).SaveCategoryAsync(null, Vehicles(kit)));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Assets(kit).DeleteCategoryAsync(comp.Id));
        }

        kit.As(Admin);
        var saved = await Assets(kit).SaveCategoryAsync(null, Vehicles(kit));
        await Assets(kit).DeleteCategoryAsync(saved.Id);
    }

    [Fact]
    public async Task A_category_threshold_overrides_the_organization_threshold_when_capitalizing()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day,
            [kit.OverheadLine(3_000m, "6230", "Office chairs"), kit.OverheadLine(1_000m, "6230", "Lamps")]));
        var comp = (await Assets(kit).ListCategoriesAsync()).Single(c => c.Code == "COMP");
        Assert.Empty(await Assets(kit).ListCapitalizableBillLinesAsync()); // both under the 5,000 default

        var furniture = await Assets(kit).SaveCategoryAsync(null, Vehicles(kit, "CHAIRS") with { Name = "Chairs", CapitalizationThreshold = 2_000m });
        Assert.Equal(bill.Lines[0].Id, Assert.Single(await Assets(kit).ListCapitalizableBillLinesAsync()).BillLineId);
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[0].Id, comp.Id)));
        await Assert.ThrowsAsync<ArgumentException>(() => Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[1].Id, furniture.Id)));
        var asset = await Assets(kit).CapitalizeAsync(new CapitalizeBillLineRequest(bill.Lines[0].Id, furniture.Id));
        Assert.Equal(3_000m, asset.Asset.Cost);
    }
}

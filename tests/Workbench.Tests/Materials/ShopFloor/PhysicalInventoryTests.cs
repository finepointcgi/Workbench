using Workbench.Contracts;
using Workbench.Contracts.Materials;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Physical inventory: freezing, count tags, recount thresholds, blank tags, count sheets and posting adjustments (GFM at no value).</summary>
public sealed class PhysicalInventoryTests
{
    [Fact]
    public async Task Freezing_blocks_movements_counts_off_by_more_than_the_threshold_need_a_recount_and_posting_adjusts_with_value()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var part = await kit.PartAsync("WIDGET");
        var gfm = await kit.PartAsync("GFE-SCOPE");
        await kit.StockAsync(part.Id, 10, 2m);
        await kit.Inventory.ReceiveGovernmentFurnishedAsync(new GovernmentReceiptRequest(gfm.Id, kit.Kit.Fx.Project1, 3, Day, kit.Main.Id, kit.A2, "DD1149-0001"));

        var pi = await kit.Counts().CreateAsync(new CreatePhysicalInventoryRequest(kit.Main.Id, Day, RecountPercent: 5, RecountValue: 0));
        Assert.Equal(("PI-000001", "Whole warehouse", PhysicalInventoryState.Counting, 2), (pi.Summary.Number, pi.Summary.Scope, pi.Summary.Status, pi.Summary.TagCount));
        Assert.Equal([("A1", "WIDGET", 10m, 2m), ("A2", "GFE-SCOPE", 3m, 0m)], pi.Tags.Select(t => (t.Bin, t.PartNumber, t.FrozenQuantity, t.UnitCost)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Counts().CreateAsync(new CreatePhysicalInventoryRequest(kit.Main.Id, Day, [kit.A1]))); // Already frozen.

        // Nothing moves in or out of a frozen bin.
        var frozen = await Assert.ThrowsAsync<ArgumentException>(() => kit.StockAsync(part.Id, 1, 2m));
        Assert.Contains("frozen for physical inventory PI-000001", frozen.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 1, Day, kit.Kit.Fx.Wbs1)));

        var widget = pi.Tags[0].TagNumber;
        var scope = pi.Tags[1].TagNumber;
        var first = await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(widget, 9));
        Assert.Equal(CountTagState.RecountNeeded, first.Status); // 10% off, the threshold is 5%.
        Assert.DoesNotContain("10", first.Message); // Blind: the counter never hears the quantity on record.
        Assert.Equal(CountTagState.Counted, (await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(scope, 3))).Status);
        var blocked = await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Finance).Counts().PostAsync(pi.Summary.Id));
        Assert.Contains("1 tag needs a recount", blocked.Message);

        kit.As(MaterialsUser);
        Assert.Equal(CountTagState.Recounted, (await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(widget, 8))).Status);
        Assert.Equal(CountTagState.Counted, (await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(scope, 3))).Status);
        Assert.Equal(CountTagState.RecountNeeded, (await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(scope, 2))).Status); // Re-entered: one short now.
        await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(scope, 2));
        pi = await kit.Counts().GetAsync(pi.Summary.Id);
        Assert.Equal((-2m, -4m), (pi.Tags[0].Variance!.Value, pi.Tags[0].VarianceValue!.Value));
        Assert.Equal((-1m, 0m), (pi.Tags[1].Variance!.Value, pi.Tags[1].VarianceValue!.Value)); // Government-furnished: quantity only.

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Counts().PostAsync(pi.Summary.Id)); // Posting is Finance.
        kit.As(Finance);
        pi = await kit.Counts().PostAsync(pi.Summary.Id);
        Assert.Equal(PhysicalInventoryState.Posted, pi.Summary.Status);
        Assert.NotNull(pi.JournalEntryNumber);
        Assert.Equal(-20m + 4m, await kit.BalanceAsync("6152")); // The opening adjustment, then the count.
        Assert.Equal(20m - 4m, await kit.BalanceAsync("1410"));
        var counts = await kit.Inventory.ListTransactionsAsync(new TransactionQuery(Type: InventoryMovement.PhysicalCount));
        Assert.Equal([("GFE-SCOPE", -1m, 0m), ("WIDGET", -2m, -4m)], counts.OrderBy(t => t.PartNumber).Select(t => (t.PartNumber, t.Quantity, t.Amount)));
        Assert.Null(counts.Single(t => t.PartNumber == "GFE-SCOPE").JournalEntryNumber);
        Assert.Equal(16m, (await kit.Parts.GetAsync(part.Id)).Summary.Value);

        // Posted: the bins are open again.
        kit.As(MaterialsUser);
        await kit.StockAsync(part.Id, 1, 2m);
    }

    [Fact]
    public async Task Blank_tags_record_found_stock_bin_ranges_freeze_only_those_bins_and_count_sheets_are_blind()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var part = await kit.PartAsync("SPRING", tracking: PartTracking.Lot);
        await kit.StockAsync(part.Id, 4, 1.5m, lot: "S1");
        await kit.StockAsync(part.Id, 2, 1.5m, lot: "S2", bin: kit.A2);

        var pi = await kit.Counts().CreateAsync(new CreatePhysicalInventoryRequest(kit.Main.Id, Day, [kit.A1], RecountPercent: 0, RecountValue: 0));
        Assert.Equal("1 bin", pi.Summary.Scope);
        Assert.Equal(["A1"], pi.Bins);
        await kit.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.Main.Id, kit.A2, 1, Day, "Found", "S2", UnitCost: 1.5m)); // A2 isn't frozen.

        var csv = await kit.Counts().ExportCountSheetAsync(pi.Summary.Id);
        Assert.Equal("PI-000001-count-sheet.csv", csv.FileName);
        Assert.DoesNotContain("On record", csv.Content);
        Assert.Contains("SPRING", csv.Content);

        var blank = await kit.Counts().AddTagAsync(pi.Summary.Id, new AddCountTagRequest(kit.A1, part.Id, 3, Lot: "s9"));
        Assert.Equal((2, CountTagState.Counted), (blank.TagNumber, blank.Status));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Counts().AddTagAsync(pi.Summary.Id, new AddCountTagRequest(kit.A2, part.Id, 1, Lot: "S2"))); // Not in this count.
        var uncounted = await Assert.ThrowsAsync<ArgumentException>(() => kit.As(Finance).Counts().PostAsync(pi.Summary.Id));
        Assert.Contains("1 tag isn't counted", uncounted.Message);

        kit.As(MaterialsUser);
        await kit.Counts().EnterCountAsync(pi.Summary.Id, new EnterCountRequest(1, 4));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Counts().VoidTagAsync(pi.Summary.Id, 1)); // Stock on record: count 0 instead.
        kit.As(Finance);
        pi = await kit.Counts().PostAsync(pi.Summary.Id);
        var lots = (await kit.Inventory.ListBalancesAsync(new BalanceQuery(part.Id))).Select(b => (b.Bin, b.Lot, b.Quantity)).OrderBy(b => b.Lot).ToList();
        Assert.Equal([("A1", "S1", 4m), ("A2", "S2", 3m), ("A1", "S9", 3m)], lots);
        Assert.Equal(15m, await kit.BalanceAsync("1410")); // 4 + 2 + 1 found in A2 + 3 found on the blank tag, at 1.50.
    }
}

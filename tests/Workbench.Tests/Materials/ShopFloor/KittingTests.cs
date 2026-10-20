using Workbench.Contracts.Materials;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Kits: staging to a kit bin, reserved stock, shortages, issuing in one step, cancelling and project kits.</summary>
public sealed class KittingTests
{
    private static async Task<Guid> KitBinAsync(MaterialsKit kit) =>
        (await kit.Parts.SaveBinAsync(kit.Main.Id, null, new SaveBinRequest("KIT", "Kit staging"))).Bins.Single(b => b.Code == "KIT").Id;

    [Fact]
    public async Task A_work_order_kit_stages_what_is_available_reserves_it_and_issues_it_in_one_step()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var chip = await kit.PartAsync("CHIP");
        var screw = await kit.PartAsync("SCREW");
        await kit.StockAsync(chip.Id, 10, 5m);
        await kit.StockAsync(screw.Id, 1, 1m);
        var bin = await KitBinAsync(kit);
        var wo = await kit.ReleasedOrderAsync("BOARD", 2, [(chip.Id, 2, null), (screw.Id, 3, null)]);

        var k = await kit.Kits().CreateForWorkOrderAsync(new CreateWorkOrderKitRequest(wo.Summary.Id, bin));
        Assert.Equal(("KIT-000001", KitState.Open, "MAIN / KIT"), (k.Summary.Number, k.Summary.Status, k.Summary.StagingLocation));
        Assert.Equal([("CHIP", 4m), ("SCREW", 6m)], k.Lines.Select(l => (l.PartNumber, l.QuantityRequired)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Kits().CreateForWorkOrderAsync(new CreateWorkOrderKitRequest(wo.Summary.Id, bin)));

        k = await kit.Kits().StageAsync(k.Summary.Id, new StageKitRequest(Day));
        Assert.Equal([(4m, 0m), (1m, 5m)], k.Lines.Select(l => (l.QuantityStaged, l.QuantityShort)));
        Assert.Equal((KitState.Open, 5m, 1), (k.Summary.Status, k.Summary.QuantityShort, k.Summary.ShortLines));
        Assert.Equal(6m, k.Lines[0].Available); // Ten on hand less the four staged.
        Assert.Equal(4m, (await kit.Inventory.ListBalancesAsync(new BalanceQuery(chip.Id))).Single(b => b.BinId == bin).Quantity);

        // Staged stock is reserved: issuing seven chips elsewhere finds only six.
        var reserved = await Assert.ThrowsAsync<ArgumentException>(() => kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(chip.Id, 7, Day, kit.Kit.Fx.Wbs1)));
        Assert.Contains("6 available", reserved.Message);

        k = await kit.Kits().IssueAsync(k.Summary.Id, new IssueKitRequest(Day));
        Assert.Equal(KitState.Issued, k.Summary.Status);
        Assert.All(k.Picks, p => Assert.True(p.IsIssued));
        wo = await kit.Manufacturing.GetWorkOrderAsync(wo.Summary.Id);
        Assert.Equal([(4m, 0m), (1m, 5m)], wo.Components.Select(c => (c.QuantityIssued, c.QuantityOpen)));
        Assert.Equal((WorkOrderState.InProgress, 21m), (wo.Summary.Status, wo.Cost.MaterialCost));
        Assert.All(wo.Transactions.Where(t => t.Type == InventoryMovement.IssueToWorkOrder), t => Assert.Equal("Kit KIT-000001", t.Reason));
        Assert.Empty(await kit.Inventory.ListBalancesAsync(new BalanceQuery(WarehouseId: kit.Main.Id, Search: "SCREW")));
        Assert.Equal(21m, await kit.BalanceAsync("1450"));
    }

    [Fact]
    public async Task Cancelling_a_kit_puts_staged_stock_back_where_it_was_picked()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var nut = await kit.PartAsync("NUT", tracking: PartTracking.Lot);
        await kit.StockAsync(nut.Id, 2, 1m, lot: "N1", bin: kit.A2);
        var bin = await KitBinAsync(kit);
        var wo = await kit.ReleasedOrderAsync("BRACKET", 1, [(nut.Id, 2, null)]);
        var k = await kit.Kits().CreateForWorkOrderAsync(new CreateWorkOrderKitRequest(wo.Summary.Id, bin));
        k = await kit.Kits().StageAsync(k.Summary.Id, new StageKitRequest(Day));
        Assert.Equal(KitState.Staged, k.Summary.Status);
        Assert.Equal(("MAIN / A2", "N1"), (Assert.Single(k.Picks).FromLocation, k.Picks[0].Lot));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Kits().CancelAsync(k.Summary.Id, new CancelKitRequest(Day, " ")));
        k = await kit.Kits().CancelAsync(k.Summary.Id, new CancelKitRequest(Day, "Order rescheduled"));
        Assert.Equal(KitState.Cancelled, k.Summary.Status);
        var balance = Assert.Single(await kit.Inventory.ListBalancesAsync(new BalanceQuery(nut.Id)));
        Assert.Equal((kit.A2, 2m, "N1"), (balance.BinId, balance.Quantity, balance.Lot));
        Assert.Equal(0m, Assert.Single((await kit.Manufacturing.GetWorkOrderAsync(wo.Summary.Id)).Components).QuantityIssued);
    }

    [Fact]
    public async Task A_project_kit_covers_open_requirements_and_issues_to_their_wbs_elements()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var cable = await kit.PartAsync("CABLE");
        await kit.StockAsync(cable.Id, 5, 20m);
        await kit.Parts.AddRequirementAsync(new SaveMaterialRequirementRequest(kit.Kit.Fx.Wbs1, cable.Id, 3, Day.AddDays(10)));
        var bin = await KitBinAsync(kit);

        var k = await kit.Kits().CreateForProjectAsync(new CreateProjectKitRequest(kit.Kit.Fx.Project1, bin));
        Assert.Equal(("1001", "1001.01"), (k.Summary.ProjectCode, Assert.Single(k.Lines).WbsCode));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Kits().CreateForProjectAsync(new CreateProjectKitRequest(kit.Kit.Fx.Project1, bin))); // Already kitted.
        k = await kit.Kits().StageAsync(k.Summary.Id, new StageKitRequest(Day));
        k = await kit.Kits().IssueAsync(k.Summary.Id, new IssueKitRequest(Day));
        Assert.Equal(3m, k.Lines[0].QuantityIssued);
        Assert.Empty(await kit.Parts.ListRequirementsAsync(kit.Kit.Fx.Project1));
        var issue = (await kit.Inventory.ListTransactionsAsync(new TransactionQuery(cable.Id, Type: InventoryMovement.IssueToProject))).Single();
        Assert.Equal(("1001.01", -60m), (issue.WbsCode, issue.Amount));
    }
}

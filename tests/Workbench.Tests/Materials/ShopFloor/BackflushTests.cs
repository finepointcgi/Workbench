using Workbench.Contracts.Materials;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Issue-on-completion: quantities, FIFO lots, configured locations, operations, shortages and the GL.</summary>
public sealed class BackflushTests
{
    [Fact]
    public async Task Completing_units_backflushes_their_share_of_each_component_oldest_lot_first()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var bolt = await kit.PartAsync("BOLT", tracking: PartTracking.Lot);
        var label = await kit.PartAsync("LABEL");
        // The older lot sorts after the newer one by name: FIFO goes by the date received, not the lot number.
        await kit.Inventory.AdjustAsync(new AdjustInventoryRequest(bolt.Id, kit.Main.Id, kit.A1, 3, Day.AddDays(-5), "Opening stock", "Z-OLD", UnitCost: 1m));
        await kit.Inventory.AdjustAsync(new AdjustInventoryRequest(bolt.Id, kit.Main.Id, kit.A1, 5, Day, "Opening stock", "A-NEW", UnitCost: 2m));
        await kit.StockAsync(label.Id, 10, 0.5m);
        var setting = await kit.Backflush().SaveSettingAsync(new SaveBackflushSettingRequest(bolt.Id, true));
        Assert.Equal(("BOLT", true, null), (setting.PartNumber, setting.IsBackflushed, setting.Location));

        var wo = await kit.ReleasedOrderAsync("ASSY", 3, [(bolt.Id, 2, null), (label.Id, 1, null)]);
        var components = await kit.Backflush().ListComponentsAsync();
        Assert.Equal([("BOLT", true), ("LABEL", false)], components.Where(c => c.ParentPartNumber == "ASSY").Select(c => (c.PartNumber, c.IsBackflushed)));

        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day));
        var issues = wo.Transactions.Where(t => t.Type == InventoryMovement.IssueToWorkOrder).ToList();
        Assert.Equal([("Z-OLD", -3m), ("A-NEW", -1m)], issues.Select(t => (t.Lot, t.Quantity)));
        Assert.All(issues, t => Assert.Equal("Backflushed at completion", t.Reason));
        Assert.Equal((4m, 0m), (wo.Components.Single(c => c.PartNumber == "BOLT").QuantityIssued, wo.Components.Single(c => c.PartNumber == "LABEL").QuantityIssued));
        Assert.Empty(await kit.Backflush().ListShortagesAsync(wo.Summary.Id));

        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(1, Day));
        Assert.Equal(6m, wo.Components.Single(c => c.PartNumber == "BOLT").QuantityIssued);
        Assert.Equal(-2m, wo.Transactions.Where(t => t.Type == InventoryMovement.IssueToWorkOrder).Last().Quantity);
        Assert.Equal(2m, (await kit.Parts.GetAsync(bolt.Id)).Summary.OnHand);

        // Genealogy: the finished units were made from the backflushed lot.
        var trace = await kit.Inventory.TraceAsync("Z-OLD", null, bolt.Id);
        Assert.NotEmpty(trace.UsedIn);

        // Backflushed material went through WIP and out with the completions; the books still tie out.
        kit.As(Finance);
        var valuation = await kit.Reports.ValuationAsync();
        Assert.Equal(valuation.TotalValue, valuation.GlInventory);
        Assert.All(await kit.EntriesAsync(), e => Assert.Equal(e.TotalDebit, e.TotalCredit));
    }

    [Fact]
    public async Task Operation_components_follow_labor_reported_from_the_configured_bin_and_shortages_are_reported_then_retried()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var paint = await kit.PartAsync("PAINT");
        await kit.StockAsync(paint.Id, 1, 3m, bin: kit.A1);
        await kit.StockAsync(paint.Id, 5, 3m, bin: kit.A2);
        await kit.Backflush().SaveSettingAsync(new SaveBackflushSettingRequest(paint.Id, true, BinId: kit.A1));
        var center = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("PNT", "Paint booth"));
        var wo = await kit.ReleasedOrderAsync("FRAME", 2, [(paint.Id, 2, 20)], [(10, center.Id, 0, 1), (20, center.Id, 0, 1)]);

        // Labor on operation 20 for one unit: half the paint is due; the bin has only one.
        wo = await kit.Manufacturing.ReportLaborAsync(wo.Summary.Id, new ReportLaborRequest(wo.Operations.Single(o => o.Sequence == 20).Id, Day, 1, 1, kit.MaterialsEmployeeId));
        var issued = Assert.Single(wo.Transactions, t => t.Type == InventoryMovement.IssueToWorkOrder);
        Assert.Equal((-1m, "MAIN / A1", "Backflushed at operation 20"), (issued.Quantity, issued.Location, issued.Reason));
        var shortage = Assert.Single(await kit.Backflush().ListShortagesAsync(wo.Summary.Id));
        Assert.Equal((1m, BackflushWhen.Operation, true), (shortage.QuantityShort, shortage.Trigger, shortage.IsOpen));
        Assert.Contains("only 1 was in the backflush bin", shortage.Message);

        // Stock arrives in the backflush bin: a retry issues it and closes the shortage.
        await kit.StockAsync(paint.Id, 3, 3m, bin: kit.A1);
        var retry = await kit.Backflush().RetryAsync(wo.Summary.Id, Day);
        Assert.Equal(-1m, Assert.Single(retry.Issued).Quantity);
        Assert.Empty(retry.Shortages);
        Assert.Empty(await kit.Backflush().ListShortagesAsync(wo.Summary.Id));
        Assert.False(Assert.Single(await kit.Backflush().ListShortagesAsync(wo.Summary.Id, openOnly: false)).IsOpen);

        // Completing both units issues the rest, still only from A1; A2 is never touched.
        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day));
        Assert.Equal(4m, Assert.Single(wo.Components).QuantityIssued);
        Assert.All(wo.Transactions.Where(t => t.Type == InventoryMovement.IssueToWorkOrder), t => Assert.Equal("MAIN / A1", t.Location));
        Assert.Equal(5m, (await kit.Inventory.ListBalancesAsync(new BalanceQuery(paint.Id))).Single(b => b.BinId == kit.A2).Quantity);

        // Backflushed paint is in WIP (3.00 each) and relieved with the completions.
        Assert.Equal(12m, wo.Cost.MaterialCost);
    }

    [Fact]
    public async Task A_bom_setting_overrides_the_parts_default_and_nothing_is_skipped_silently_when_stock_is_missing()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var glue = await kit.PartAsync("GLUE");
        await kit.Backflush().SaveSettingAsync(new SaveBackflushSettingRequest(glue.Id, false));
        var wo = await kit.ReleasedOrderAsync("PANEL", 1, [(glue.Id, 1, null)]);
        var bom = (await kit.Manufacturing.ListBomsAsync()).Single(b => b.PartNumber == "PANEL");
        var over = await kit.Backflush().SaveSettingAsync(new SaveBackflushSettingRequest(glue.Id, true, bom.Id));
        Assert.Equal(("PANEL", "A"), (over.ParentPartNumber, over.BomRevision));
        Assert.Equal("This BOM", Assert.Single(await kit.Backflush().ListComponentsAsync(bom.Id)).Source);

        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(1, Day));
        Assert.Equal(WorkOrderState.Completed, wo.Summary.Status);
        var shortage = Assert.Single(await kit.Backflush().ListShortagesAsync());
        Assert.Equal(("GLUE", 1m, BackflushWhen.Completion), (shortage.PartNumber, shortage.QuantityShort, shortage.Trigger));
        Assert.Contains("none in stock", shortage.Message);

        // Viewers can't change settings.
        kit.As(Viewer);
        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() => kit.Backflush().SaveSettingAsync(new SaveBackflushSettingRequest(glue.Id, true)));
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials;

/// <summary>Work order lifecycle and WIP costing, lot/serial genealogy, and government-furnished material.</summary>
public sealed class WorkOrderTests
{
    private static async Task<(PartSummaryDto Board, PartSummaryDto Chip, PartSummaryDto Case)> BoardAsync(MaterialsKit kit)
    {
        var chip = await kit.PartAsync("CHIP", method: PartCostMethod.Standard, standard: 10m, tracking: PartTracking.Lot);
        var housing = await kit.PartAsync("CASE", tracking: PartTracking.Serial);
        var board = await kit.PartAsync("BOARD", PartSourceKind.Make, PartCostMethod.Standard, 60m, PartTracking.Serial);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(board.Id, "A", [new SaveBomComponentRequest(chip.Id, 2), new SaveBomComponentRequest(housing.Id, 1)]));
        var center = await kit.Manufacturing.SaveWorkCenterAsync(null, new SaveWorkCenterRequest("asm", "Assembly bench"));
        await kit.Manufacturing.SaveRoutingAsync(null, new SaveRoutingRequest(board.Id, "A", [new SaveRoutingOperationRequest(10, "Assemble", center.Id, 0.5m, 1m)]));
        await kit.StockAsync(chip.Id, 4, 0m, lot: "L1");
        await kit.StockAsync(housing.Id, 1, 15m, serial: "C1");
        await kit.StockAsync(housing.Id, 1, 15m, serial: "C2");
        return (board, chip, housing);
    }

    [Fact]
    public async Task A_stock_work_order_collects_wip_relieves_it_at_standard_and_posts_the_variance_at_close()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (board, _, _) = await BoardAsync(kit);
        var wo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(board.Id, 2, Day, Day.AddDays(5)));
        Assert.Equal(WorkOrderState.Planned, wo.Summary.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day)));

        wo = await kit.Manufacturing.ReleaseAsync(wo.Summary.Id);
        Assert.Equal([("CHIP", 4m), ("CASE", 2m)], wo.Components.Select(c => (c.PartNumber, c.QuantityRequired)));
        Assert.Equal(2.5m, Assert.Single(wo.Operations).PlannedHours);

        wo = await kit.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day));
        Assert.Equal(WorkOrderState.InProgress, wo.Summary.Status);
        Assert.Equal(70m, wo.Cost.MaterialCost);
        Assert.Equal(70m, await kit.BalanceAsync("1450"));

        wo = await kit.Manufacturing.ReportLaborAsync(wo.Summary.Id, new ReportLaborRequest(wo.Operations[0].Id, Day, 2m, 2, kit.Kit.Fx.AnalystId));
        Assert.Null(wo.Cost.LaborCost); // Materials doesn't see labor cost (cost rates).
        Assert.Null(Assert.Single(wo.Labor).TimesheetHours); // A stock order without a labor charge code has no timesheet hours to compare.
        kit.As(Finance);
        var finance = await kit.Manufacturing.GetWorkOrderAsync(wo.Summary.Id);
        Assert.Equal((175m, 245m), (finance.Cost.LaborCost, finance.Cost.WipBalance)); // 2 h × 87.50
        Assert.Equal(245m, await kit.BalanceAsync("1450"));
        Assert.Equal(-175m, await kit.BalanceAsync("6195"));

        kit.As(MaterialsUser);
        var short1 = await Assert.ThrowsAsync<ArgumentException>(() => kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day, Serials: ["B1"])));
        Assert.Contains("one serial number per unit", short1.Message);
        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day, Serials: ["b1", "b2"]));
        Assert.Equal(WorkOrderState.Completed, wo.Summary.Status);
        Assert.Equal(120m, wo.Cost.CompletedCost);
        Assert.Equal(120m, await kit.BalanceAsync("1420"));
        Assert.Equal(2m, (await kit.Parts.GetAsync(board.Id)).Summary.OnHand);

        wo = await kit.Manufacturing.CloseAsync(wo.Summary.Id, new CloseWorkOrderRequest(Day));
        Assert.Equal((WorkOrderState.Closed, 125m), (wo.Summary.Status, wo.Cost.VarianceCost));
        Assert.Equal(0m, await kit.BalanceAsync("1450"));
        Assert.Equal(125m, await kit.BalanceAsync("6151"));
        foreach (var e in await kit.EntriesAsync())
        {
            Assert.Equal(e.TotalDebit, e.TotalCredit);
        }

        kit.As(Finance);
        var valuation = await kit.Reports.ValuationAsync();
        Assert.Equal(valuation.TotalValue, valuation.GlInventory);
        Assert.Equal(0m, valuation.GlWip);
    }

    [Fact]
    public async Task Lots_and_serials_trace_from_component_to_finished_unit_and_back()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (board, _, _) = await BoardAsync(kit);
        var wo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(board.Id, 2, Day, Day));
        await kit.Manufacturing.ReleaseAsync(wo.Summary.Id);
        await kit.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day));
        await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day, Serials: ["B1", "B2"]));
        var unit = Assert.Single(await kit.Inventory.ListBalancesAsync(new BalanceQuery(board.Id, Search: "B1")));
        await kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(board.Id, 1, Day, kit.Kit.Fx.Wbs1, unit.Id));

        var lot = await kit.Inventory.TraceAsync("l1", null);
        Assert.Equal([InventoryMovement.Adjustment, InventoryMovement.IssueToWorkOrder], lot.History.Select(h => h.Type));
        Assert.Equal(["B1", "B2"], lot.UsedIn.Select(u => u.Serial).Order());

        var serial = await kit.Inventory.TraceAsync(null, "B1", board.Id);
        Assert.Equal([InventoryMovement.WorkOrderCompletion, InventoryMovement.IssueToProject], serial.History.Select(h => h.Type));
        Assert.Equal("1001.01", serial.History[1].WbsCode);
        Assert.Contains(serial.MadeFrom, m => m.Lot == "L1" && m.Quantity == 4);
        Assert.Equal(["C1", "C2"], serial.MadeFrom.Where(m => m.Serial.Length > 0).Select(m => m.Serial).Order());
        Assert.All(serial.MadeFrom, m => Assert.Equal(wo.Summary.Number, m.Document));
    }

    [Fact]
    public async Task Government_furnished_material_is_tracked_but_never_costed()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var sensor = await kit.PartAsync("SENSOR");
        var housing = await kit.PartAsync("HOUSING");
        var unit = await kit.PartAsync("UNIT", PartSourceKind.Make);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(unit.Id, "A", [new SaveBomComponentRequest(sensor.Id, 1), new SaveBomComponentRequest(housing.Id, 1)]));
        await kit.StockAsync(housing.Id, 2, 20m);
        var gfm = Assert.Single(await kit.Inventory.ReceiveGovernmentFurnishedAsync(new GovernmentReceiptRequest(sensor.Id, kit.Kit.Fx.Project1, 3, Day, kit.Main.Id, kit.A2, "DD1149-77")));
        Assert.Equal((StockOwnership.GovernmentFurnished, 0m, (string?)null), (gfm.Ownership, gfm.Amount, gfm.JournalEntryNumber));
        Assert.Equal((0m, 3m, 0m), ((await kit.Parts.GetAsync(sensor.Id)).Summary.OnHand, (await kit.Parts.GetAsync(sensor.Id)).Summary.OnHandGovernment, (await kit.Parts.GetAsync(sensor.Id)).Summary.Value));

        // It can't go to another project, nor into a stock build.
        var gfmBalance = Assert.Single(await kit.Inventory.ListBalancesAsync(new BalanceQuery(sensor.Id)));
        await using (var db = await kit.Kit.Factory.CreateAsync())
        {
            var other = await db.Set<WbsElement>().FirstAsync(w => w.ProjectId == kit.Kit.Fx.Project2);
            var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(sensor.Id, 1, Day, other.Id, gfmBalance.Id)));
            Assert.Contains("FAR 52.245-1", refused.Message);
        }

        var stockBuild = await kit.Manufacturing.ReleaseAsync((await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(unit.Id, 1, Day, Day))).Summary.Id);
        var shortage = await Assert.ThrowsAsync<ArgumentException>(() => kit.Manufacturing.IssueComponentsAsync(stockBuild.Summary.Id, new IssueComponentsRequest(Day)));
        Assert.Contains("Short 1", shortage.Message);

        // A project build uses it at no cost; only the housing is charged to the WBS.
        var before = await kit.JournalCountAsync();
        var wo = await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(unit.Id, 2, Day, Day, WbsElementId: kit.Kit.Fx.Wbs1));
        await kit.Manufacturing.ReleaseAsync(wo.Summary.Id);
        wo = await kit.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day));
        Assert.Equal(40m, wo.Cost.MaterialCost);
        var issues = wo.Transactions.Where(t => t.Type == InventoryMovement.IssueToWorkOrder).ToList();
        Assert.All(issues.Where(t => t.Ownership == StockOwnership.GovernmentFurnished), t => Assert.Equal((0m, (string?)null), (t.Amount, t.JournalEntryNumber)));
        Assert.Equal(before + 1, await kit.JournalCountAsync());
        var entry = (await kit.EntriesAsync(JournalSource.Manufacturing)).Single();
        Assert.Equal(40m, entry.TotalDebit);
        Assert.All(entry.Lines.Where(l => l.Debit > 0), l => Assert.Equal((kit.Account("5120"), kit.Kit.Fx.Wbs1), (l.AccountId, l.WbsElementId!.Value)));

        wo = await kit.Manufacturing.CompleteAsync(wo.Summary.Id, new CompleteWorkOrderRequest(2, Day));
        Assert.Equal(InventoryMovement.WorkOrderShipment, wo.Transactions.Last().Type);
        Assert.Equal(0m, (await kit.Parts.GetAsync(unit.Id)).Summary.OnHand);
        Assert.Equal(before + 1, await kit.JournalCountAsync());

        var listing = Assert.Single(await kit.Reports.GovernmentFurnishedAsync());
        Assert.Equal(("1001", 1m, "DD1149-77"), (listing.ProjectCode, listing.Quantity, listing.Reference));
        var valuation = await kit.Reports.ValuationAsync();
        Assert.DoesNotContain(valuation.Rows, r => r.PartNumber == "SENSOR");
        var projectCost = await kit.Reports.ProjectMaterialCostAsync(kit.Kit.Fx.Project1);
        Assert.Equal(40m, projectCost.Total);
        Assert.Equal((0m, 2m), projectCost.Rows.Where(r => r.PartNumber == "SENSOR").Select(r => (r.Amount, r.GovernmentQuantity)).Single());

        kit.As(Finance);
        var cost = await kit.Kit.Get<IBurdenedCostService>().GetProjectCostAsync(kit.Kit.Fx.Project1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), RateBasis.Provisional);
        Assert.Equal(40m, cost.Lines.Single(l => l.WbsElementId == kit.Kit.Fx.Wbs1).DirectOdc);
    }

    [Fact]
    public async Task Work_orders_with_work_on_them_cant_be_cancelled_and_boms_cant_use_themselves()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var (board, chip, _) = await BoardAsync(kit);
        var assembly = await kit.PartAsync("ASSY", PartSourceKind.Make);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(assembly.Id, "A", [new SaveBomComponentRequest(board.Id, 1)]));
        var boardBom = (await kit.Manufacturing.ListBomsAsync(board.Id)).Single();
        var loop = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Manufacturing.SaveBomAsync(boardBom.Id, new SaveBomRequest(board.Id, "A", [new SaveBomComponentRequest(chip.Id, 2), new SaveBomComponentRequest(assembly.Id, 1)])));
        Assert.Contains("use itself", loop.Message);
        Assert.Equal([(1, "BOARD"), (2, "ASSY")], (await kit.Manufacturing.WhereUsedAsync(chip.Id)).Select(w => (w.Level, w.ParentNumber)));
        Assert.Equal([(1, "BOARD"), (2, "CHIP"), (2, "CASE")], (await kit.Manufacturing.ExplodeAsync(assembly.Id, 1, Day)).Select(r => (r.Level, r.PartNumber)));

        var wo = await kit.Manufacturing.ReleaseAsync((await kit.Manufacturing.CreateWorkOrderAsync(new SaveWorkOrderRequest(board.Id, 1, Day, Day))).Summary.Id);
        await kit.Manufacturing.IssueComponentsAsync(wo.Summary.Id, new IssueComponentsRequest(Day, [new IssueComponentLine(wo.Components[0].Id, 2)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Manufacturing.CancelAsync(wo.Summary.Id, new CancelWorkOrderRequest("Not needed")));
        var scrapped = await kit.Manufacturing.ScrapAsync(wo.Summary.Id, new ScrapRequest(1, Day, "Dropped"));
        Assert.Equal(WorkOrderState.Completed, scrapped.Summary.Status);
        Assert.Equal(60m, scrapped.Cost.ScrapCost); // Scrap relieves WIP at standard.
    }
}

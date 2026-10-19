using Workbench.Contracts.Billing;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials;

/// <summary>Moving-average and standard costing, PO receipts into stock, issues to projects and their GL postings.</summary>
public sealed class InventoryCostingTests
{
    private static async Task AssertBalancedAsync(MaterialsKit kit)
    {
        foreach (var e in await kit.EntriesAsync())
        {
            Assert.Equal(e.TotalDebit, e.TotalCredit);
        }
    }

    [Fact]
    public async Task Moving_average_re_averages_on_receipt_and_issues_relieve_the_book_value_exactly()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var part = await kit.PartAsync("RES-10K");

        await kit.StockAsync(part.Id, 10, 5m);
        await kit.StockAsync(part.Id, 10, 8m);
        var detail = await kit.Parts.GetAsync(part.Id);
        Assert.Equal((20m, 6.5m, 130m), (detail.Summary.OnHand, detail.Summary.AverageCost, detail.Summary.Value));
        Assert.Equal(130m, await kit.BalanceAsync("1410"));

        var issued = Assert.Single(await kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 5, Day, kit.Kit.Fx.Wbs1)));
        Assert.Equal((-5m, 6.5m, -32.5m), (issued.Quantity, issued.UnitCost, issued.Amount));
        Assert.Equal(("1001", "1001.01"), (issued.ProjectCode, issued.WbsCode));
        var entry = (await kit.EntriesAsync(JournalSource.Inventory)).Single(e => e.Number == issued.JournalEntryNumber);
        var debit = entry.Lines.Single(l => l.Debit > 0);
        Assert.Equal((kit.Account("5120"), 32.5m, kit.Kit.Fx.Project1, kit.Kit.Fx.Wbs1), (debit.AccountId, debit.Debit, debit.ProjectId!.Value, debit.WbsElementId!.Value));
        Assert.Equal((kit.Account("1410"), 32.5m), (entry.Lines.Single(l => l.Credit > 0).AccountId, entry.Lines.Single(l => l.Credit > 0).Credit));

        // Issuing the last units relieves what's left of the book value (no rounding residue).
        var rest = Assert.Single(await kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(part.Id, 15, Day, kit.Kit.Fx.Wbs1)));
        Assert.Equal(-97.5m, rest.Amount);
        Assert.Equal(0m, (await kit.Parts.GetAsync(part.Id)).Summary.Value);
        Assert.Equal(0m, await kit.BalanceAsync("1410"));
        Assert.Equal(130m, await kit.BalanceAsync("5120"));
        await AssertBalancedAsync(kit);

        // Material on the WBS is a direct ODC in burdened cost (and the project financials).
        kit.As(Finance);
        var cost = await kit.Kit.Get<IBurdenedCostService>().GetProjectCostAsync(kit.Kit.Fx.Project1, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), RateBasis.Provisional);
        Assert.Equal(130m, cost.Lines.Single(l => l.WbsElementId == kit.Kit.Fx.Wbs1).DirectOdc);
    }

    [Fact]
    public async Task Standard_cost_receipts_post_the_purchase_price_variance_and_the_bill_clears_the_clearing_account()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var chip = await kit.PartAsync("CHIP", method: PartCostMethod.Standard, standard: 10m, tracking: PartTracking.Lot);
        var po = await kit.PartOrderAsync(chip.Id, 10, 12m);

        var received = Assert.Single(await kit.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, Day,
            [new ReceiveIntoInventoryLine(po.Lines[0].Id, 10, Lot: "l-100")])));
        Assert.Equal((InventoryMovement.PurchaseReceipt, "L-100", 10m, 100m, 20m), (received.Type, received.Lot, received.UnitCost, received.Amount, received.Variance));
        Assert.Equal(po.Order.Number, received.PurchaseOrderNumber);
        Assert.Equal(100m, await kit.BalanceAsync("1410"));
        Assert.Equal(20m, await kit.BalanceAsync("6150"));
        Assert.Equal(-120m, await kit.BalanceAsync("1405"));

        kit.As(Finance);
        Assert.Equal(10m, (await kit.Kit.Orders().GetAsync(po.Order.Id)).Lines[0].QuantityReceived);

        // The three-way match is unchanged: the bill from the PO matches and, once posted, clears received-not-invoiced.
        var bill = await kit.Kit.PostedBillAsync(await kit.Kit.Match().BuildBillAsync(new CreateBillFromPurchaseOrderRequest(po.Order.Id, "INV-77", Day)));
        Assert.Empty(await kit.Kit.Match().ListExceptionsAsync());
        Assert.Equal(BillState.Posted, bill.Bill.Status);
        Assert.Equal(0m, await kit.BalanceAsync("1405"));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task Purchasing_receipts_of_part_lines_go_into_stock_but_lot_tracked_parts_need_materials_receiving()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var bolt = await kit.PartAsync("BOLT");
        var chip = await kit.PartAsync("CHIP", method: PartCostMethod.Standard, standard: 10m, tracking: PartTracking.Lot);
        var boltOrder = await kit.PartOrderAsync(bolt.Id, 4, 2.5m);
        var chipOrder = await kit.PartOrderAsync(chip.Id, 5, 10m);

        kit.As(Finance);
        await kit.Kit.ReceiveAsync(boltOrder, 4);
        var stock = Assert.Single(await kit.As(MaterialsUser).Inventory.ListBalancesAsync(new BalanceQuery(bolt.Id)));
        Assert.Equal(("MAIN", "A1", 4m, 10m), (stock.Warehouse, stock.Bin, stock.Quantity, stock.Value));

        kit.As(Finance);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Kit.ReceiveAsync(chipOrder, 5));
        Assert.Contains("lot tracked", refused.Message);
        Assert.Equal(0m, (await kit.Kit.Orders().GetAsync(chipOrder.Order.Id)).Lines[0].QuantityReceived);
    }

    [Fact]
    public async Task A_standard_cost_change_revalues_stock_on_hand_and_needs_finance()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var chip = await kit.PartAsync("CHIP", method: PartCostMethod.Standard, standard: 10m);
        await kit.StockAsync(chip.Id, 7, 0m);
        Assert.Equal(70m, await kit.BalanceAsync("1410"));

        await Assert.ThrowsAsync<Workbench.Contracts.WorkbenchForbiddenException>(() =>
            kit.Inventory.ChangeStandardCostAsync(chip.Id, new ChangeStandardCostRequest(11m, Day, "Vendor price increase")));
        var direct = await Assert.ThrowsAsync<ArgumentException>(() => kit.Parts.UpdateAsync(chip.Id,
            new SavePartRequest("CHIP", null, "Chip", "EA", PartSourceKind.Buy, PartTracking.None, PartCostMethod.Standard, 11m)));
        Assert.Contains("revaluation", direct.Message);

        var reval = await kit.As(Finance).Inventory.ChangeStandardCostAsync(chip.Id, new ChangeStandardCostRequest(11m, Day, "Vendor price increase"));
        Assert.Equal((InventoryMovement.Revaluation, 7m), (reval.Type, reval.Amount));
        Assert.Equal(77m, await kit.BalanceAsync("1410"));
        Assert.Equal(77m, (await kit.Parts.GetAsync(chip.Id)).Summary.Value);
    }

    [Fact]
    public async Task Transfers_move_stock_without_posting_and_cycle_counts_post_the_difference()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var part = await kit.PartAsync("NUT");
        await kit.StockAsync(part.Id, 10, 1m);
        var entries = await kit.JournalCountAsync();
        var balance = Assert.Single(await kit.Inventory.ListBalancesAsync(new BalanceQuery(part.Id)));

        var moved = await kit.Inventory.TransferAsync(new TransferRequest(balance.Id, 4, Day, kit.Main.Id, kit.A2));
        Assert.Equal([InventoryMovement.TransferOut, InventoryMovement.TransferIn], moved.Select(m => m.Type));
        Assert.Equal(entries, await kit.JournalCountAsync());
        var balances = await kit.Inventory.ListBalancesAsync(new BalanceQuery(part.Id));
        Assert.Equal([("A1", 6m), ("A2", 4m)], balances.Select(b => (b.Bin, b.Quantity)));

        Assert.Null(await kit.Inventory.CycleCountAsync(new CycleCountRequest(balances[0].Id, 6, Day)));
        var count = await kit.Inventory.CycleCountAsync(new CycleCountRequest(balances[1].Id, 3, Day, "One missing"));
        Assert.Equal((InventoryMovement.CycleCount, -1m, -1m), (count!.Type, count.Quantity, count.Amount));
        Assert.Contains("Cycle count", count.Reason);
        Assert.Equal(9m, await kit.BalanceAsync("1410"));
        Assert.Equal(-9m, await kit.BalanceAsync("6152"));

        var noReason = await Assert.ThrowsAsync<ArgumentException>(() => kit.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.Main.Id, kit.A1, -1, Day, " ")));
        Assert.Contains("reason", noReason.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Inventory.AdjustAsync(new AdjustInventoryRequest(part.Id, kit.Main.Id, kit.A1, -7, Day, "Damaged")));
    }
}

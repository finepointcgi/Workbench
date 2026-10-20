using Workbench.Application.Materials;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials.ShopFloor;

/// <summary>Landed cost: allocation rounding, on-hand and issued shares, standard cost, AP bill lines and postings.</summary>
public sealed class LandedCostTests
{
    /// <summary>Orders, receives and returns the inventory receipt transaction.</summary>
    private static async Task<InventoryTransactionDto> ReceivedAsync(MaterialsKit kit, Guid partId, decimal quantity, decimal price)
    {
        var po = await kit.PartOrderAsync(partId, quantity, price);
        return Assert.Single(await kit.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, Day, [new ReceiveIntoInventoryLine(po.Lines[0].Id, quantity)])));
    }

    [Fact]
    public async Task A_charge_splits_by_value_in_whole_cents_and_raises_the_value_of_stock_on_hand()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var lines = new List<InventoryTransactionDto>();
        foreach (var number in new[] { "AAA", "BBB", "CCC" })
        {
            var part = await kit.PartAsync(number);
            lines.Add(await ReceivedAsync(kit, part.Id, 1, 10m));
        }

        var charge = await kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Freight, "Inbound freight", Day, LandedCostBasis.Value,
            lines.Select(l => new LandedChargeTargetRequest(l.Id)).ToList(), Amount: 100m));
        Assert.Equal((LandedChargeState.Draft, "1406 Landed cost clearing"), (charge.Summary.Status, charge.Summary.CreditAccount));
        Assert.Equal(100m, charge.Lines.Sum(l => l.Amount)); // Preview.
        Assert.Equal([33.33m, 33.33m, 33.34m], charge.Lines.Select(l => l.Amount).Order());

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.LandedCost().PostAsync(charge.Summary.Id)); // Materials drafts, Finance posts.
        kit.As(Finance);
        charge = await kit.LandedCost().PostAsync(charge.Summary.Id);
        Assert.Equal(LandedChargeState.Posted, charge.Summary.Status);
        Assert.NotNull(charge.Summary.JournalEntryNumber);
        Assert.Equal(100m, charge.Lines.Sum(l => l.ToInventory));
        Assert.Equal(130m, await kit.GlAsync("1410")); // Three receipts at 10.00, plus the freight.
        Assert.Equal(-100m, await kit.GlAsync("1406"));
        var parts = await kit.Parts.ListAsync(new PartQuery());
        Assert.Equal(130m, parts.Sum(p => p.Value));
        Assert.Contains(parts, p => p.AverageCost == 43.34m);
        var valuation = await kit.Reports.ValuationAsync();
        Assert.Equal(valuation.TotalValue, valuation.GlInventory);

        var receiptId = (await kit.LandedCost().ListReceiptLinesAsync()).Single(r => r.ReceiptTransactionId == lines[0].Id).PurchaseReceiptId;
        var receipt = await kit.LandedCost().GetReceiptAsync(receiptId);
        Assert.Equal((10m, 33.34m), (receipt.ReceivedValue, receipt.LandedCost));
        Assert.Equal(charge.Summary.Number, Assert.Single(receipt.Charges).Number);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.LandedCost().PostAsync(charge.Summary.Id));
    }

    [Fact]
    public async Task Units_already_issued_send_their_share_to_the_project_and_standard_cost_parts_take_it_as_variance()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var wire = await kit.PartAsync("WIRE");
        var fan = await kit.PartAsync("FAN", method: PartCostMethod.Standard, standard: 5m);
        var wireIn = await ReceivedAsync(kit, wire.Id, 10, 4m);
        var fanIn = await ReceivedAsync(kit, fan.Id, 2, 5m);
        await kit.Inventory.IssueToProjectAsync(new IssueToProjectRequest(wire.Id, 4, Day, kit.Kit.Fx.Wbs1));

        kit.As(Finance);
        var charge = await kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Duty, "Import duty", Day, LandedCostBasis.Quantity,
            [new LandedChargeTargetRequest(wireIn.Id), new LandedChargeTargetRequest(fanIn.Id)], Amount: 60m));
        charge = await kit.LandedCost().PostAsync(charge.Summary.Id);
        var wireLine = charge.Lines.Single(l => l.Receipt.PartNumber == "WIRE");
        var fanLine = charge.Lines.Single(l => l.Receipt.PartNumber == "FAN");
        Assert.Equal((50m, 30m, 20m, 0m), (wireLine.Amount, wireLine.ToInventory, wireLine.ToIssued, wireLine.ToVariance));
        Assert.Equal((10m, 0m, 10m), (fanLine.Amount, fanLine.ToInventory, fanLine.ToVariance));

        Assert.Equal(40m - 16m + 10m + 30m, await kit.GlAsync("1410")); // Receipts less the issue, plus the share still on hand.
        Assert.Equal(10m, await kit.GlAsync("6150"));
        var direct = (await kit.EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == kit.Account("5120") && l.Memo.Contains("landed cost")).ToList();
        Assert.Equal((20m, kit.Kit.Fx.Project1, kit.Kit.Fx.Wbs1), (Assert.Single(direct).Debit, direct[0].ProjectId!.Value, direct[0].WbsElementId!.Value));
        var wirePart = await kit.Parts.GetAsync(wire.Id);
        Assert.Equal((54m, 9m), (wirePart.Summary.Value, wirePart.Summary.AverageCost)); // 6 × 4 + 30, re-averaged.
        Assert.Equal(5m, (await kit.Parts.GetAsync(fan.Id)).Summary.StandardCost);

        // The project's material cost (and its financials) carries the landed cost on what it already received.
        var projectCost = await kit.Reports.ProjectMaterialCostAsync(kit.Kit.Fx.Project1);
        Assert.Equal(36m, projectCost.Total);
        await using var db = await kit.Kit.Factory.CreateAsync();
        var actuals = await MaterialCostActuals.LoadAsync(db, [kit.Kit.Fx.Project1], Day.AddDays(-30), Day.AddDays(30), default);
        Assert.Equal(36m, actuals.Sum(a => a.Amount));
        Assert.All(await kit.EntriesAsync(), e => Assert.Equal(e.TotalDebit, e.TotalCredit));
    }

    [Fact]
    public async Task A_charge_from_an_ap_bill_line_clears_its_account_by_weight_and_is_allocated_once()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var a = await kit.PartAsync("HEAVY");
        var b = await kit.PartAsync("LIGHT");
        var heavy = await ReceivedAsync(kit, a.Id, 1, 100m);
        var light = await ReceivedAsync(kit, b.Id, 1, 100m);
        kit.As(Finance);
        var clearing = (await kit.LandedCost().GetOptionsAsync()).ClearingAccountId!.Value;
        var bill = await kit.Kit.PostedBillAsync(new SaveBillRequest(kit.Kit.Acme.Id, "FRT-77", Day,
            [new SaveBillLineRequest(BillLineKind.Account, "Truck freight", 25m, AccountId: clearing, OrgUnitId: kit.Kit.OrgId)]));
        Assert.Equal(25m, await kit.GlAsync("1406"));
        var billLine = Assert.Single(await kit.LandedCost().ListBillLinesAsync(), l => l.BillId == bill.Bill.Id);
        Assert.True(billLine.OnClearingAccount);

        var weightless = await Assert.ThrowsAsync<ArgumentException>(() => kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Freight, "Freight", Day,
            LandedCostBasis.Weight, [new LandedChargeTargetRequest(heavy.Id), new LandedChargeTargetRequest(light.Id)], ApBillLineId: billLine.ApBillLineId)));
        Assert.Contains("weight", weightless.Message);
        var charge = await kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Freight, "Freight", Day, LandedCostBasis.Weight,
            [new LandedChargeTargetRequest(heavy.Id, 200), new LandedChargeTargetRequest(light.Id, 100)], ApBillLineId: billLine.ApBillLineId));
        Assert.Equal((25m, $"{bill.Bill.Number} line 1"), (charge.Summary.Amount, charge.Summary.BillNumber));
        Assert.NotNull(charge.Summary.Vendor);
        charge = await kit.LandedCost().PostAsync(charge.Summary.Id);
        Assert.Equal([16.67m, 8.33m], charge.Lines.OrderByDescending(l => l.Weight).Select(l => l.Amount));
        Assert.Equal(0m, await kit.GlAsync("1406"));
        Assert.DoesNotContain(await kit.LandedCost().ListBillLinesAsync(), l => l.BillId == bill.Bill.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Freight, "Again", Day, LandedCostBasis.Value,
            [new LandedChargeTargetRequest(heavy.Id)], ApBillLineId: billLine.ApBillLineId)));

        // Only purchase receipts take landed cost (not adjustments, and never government-furnished material).
        kit.As(MaterialsUser);
        var adjustment = await kit.StockAsync(a.Id, 1, 50m);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.LandedCost().CreateAsync(new SaveLandedChargeRequest(LandedChargeType.Other, "Bad", Day, LandedCostBasis.Quantity,
            [new LandedChargeTargetRequest(adjustment.Id)], Amount: 5m)));
    }
}

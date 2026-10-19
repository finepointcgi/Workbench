using Workbench.Application.Materials;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Materials;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;
using static Workbench.Tests.Materials.MaterialsKit;

namespace Workbench.Tests.Materials;

/// <summary>MRP netting (the pure engine) and the planning run, firming into requisitions and work orders.</summary>
public sealed class MrpTests
{
    private static readonly DateOnly AsOf = new(2026, 10, 1);
    private static readonly Guid Top = Guid.NewGuid();
    private static readonly Guid Sub = Guid.NewGuid();
    private static readonly Guid Leaf = Guid.NewGuid();

    private static MrpPart Make(Guid id, string number, int lead, decimal onHand = 0, decimal safety = 0) => new(id, number, PartSource.Make, lead, safety, 0, 0, onHand);

    private static MrpPart Buy(Guid id, string number, int lead, decimal onHand = 0, decimal safety = 0, decimal min = 0, decimal multiple = 0) =>
        new(id, number, PartSource.Buy, lead, safety, min, multiple, onHand);

    [Fact]
    public void Netting_explodes_through_levels_and_offsets_each_by_its_lead_time()
    {
        // TOP (make, 5 days) needs 2 SUB each; SUB (make, 3 days) needs 3 LEAF each; LEAF is bought (10 days).
        var parts = new[] { Make(Top, "TOP", 5), Make(Sub, "SUB", 3, onHand: 1), Buy(Leaf, "LEAF", 10, onHand: 4) };
        var components = new[] { new MrpComponent(Top, Sub, 2), new MrpComponent(Sub, Leaf, 3) };
        var demand = new[] { new MrpDemand(Top, AsOf.AddDays(30), 4, "Order 1") };

        var result = MrpEngine.Plan(parts, components, demand, [], AsOf, 90);

        var top = result.Plans.Single(p => p.PartId == Top);
        Assert.Equal((PlannedOrderKind.Make, 4m, AsOf.AddDays(30), AsOf.AddDays(25), false), (top.Kind, top.Quantity, top.NeedDate, top.ReleaseDate, top.IsLate));
        var sub = result.Plans.Single(p => p.PartId == Sub);
        Assert.Equal((7m, AsOf.AddDays(25), AsOf.AddDays(22)), (sub.Quantity, sub.NeedDate, sub.ReleaseDate)); // 8 needed, 1 on hand.
        var leaf = result.Plans.Single(p => p.PartId == Leaf);
        Assert.Equal((PlannedOrderKind.Buy, 17m, AsOf.AddDays(22), AsOf.AddDays(12)), (leaf.Kind, leaf.Quantity, leaf.NeedDate, leaf.ReleaseDate)); // 21 needed, 4 on hand.
        Assert.Contains("planned SUB 7", leaf.Pegging);
    }

    [Fact]
    public void Scheduled_receipts_safety_stock_and_lot_sizing_shape_the_plan_and_short_lead_times_are_late()
    {
        var parts = new[] { Buy(Leaf, "LEAF", 10, onHand: 5, safety: 2, min: 10, multiple: 5) };
        var demand = new[] { new MrpDemand(Leaf, AsOf.AddDays(3), 6, "WO-1"), new MrpDemand(Leaf, AsOf.AddDays(40), 8, "WO-2") };
        var supply = new[] { new MrpSupply(Leaf, AsOf.AddDays(20), 4, "PO-1") };

        var result = MrpEngine.Plan(parts, [], demand, supply, AsOf, 90);

        // Day 3: 5 − 6 = −1 < 2 safety → need 3, raised to the minimum 10 (a multiple of 5); released 7 days ago: late.
        var first = result.Plans[0];
        Assert.Equal((10m, AsOf.AddDays(3), AsOf.AddDays(-7), true), (first.Quantity, first.NeedDate, first.ReleaseDate, first.IsLate));
        Assert.Contains("safety stock 2", first.Pegging);
        Assert.Contains(result.Messages, m => m.Contains("expedite"));
        // Day 40: 9 + 4 (PO-1) − 8 = 5 ≥ 2: nothing more.
        Assert.Single(result.Plans);

        // A demand past the horizon isn't planned.
        Assert.Single(MrpEngine.Plan(parts, [], demand, supply, AsOf, 30).Plans);
        Assert.Equal(15m, MrpEngine.LotSize(11, 10, 5));
        Assert.Equal(11m, MrpEngine.LotSize(11, 0, 0));
    }

    [Fact]
    public void Components_follow_their_effectivity_on_the_release_date()
    {
        var newLeaf = Guid.NewGuid();
        var parts = new[] { Make(Top, "TOP", 0), Buy(Leaf, "OLD", 0), Buy(newLeaf, "NEW", 0) };
        var cutover = AsOf.AddDays(10);
        var components = new[] { new MrpComponent(Top, Leaf, 1, EffectiveTo: cutover.AddDays(-1)), new MrpComponent(Top, newLeaf, 1, EffectiveFrom: cutover) };
        var demand = new[] { new MrpDemand(Top, AsOf.AddDays(5), 1, "Early"), new MrpDemand(Top, AsOf.AddDays(20), 1, "Late") };

        var plans = MrpEngine.Plan(parts, components, demand, [], AsOf, 90).Plans;

        Assert.Equal(AsOf.AddDays(5), plans.Single(p => p.PartId == Leaf).NeedDate);
        Assert.Equal(AsOf.AddDays(20), plans.Single(p => p.PartId == newLeaf).NeedDate);
    }

    [Fact]
    public async Task The_planning_run_firms_buys_into_submitted_requisitions_and_makes_into_work_orders()
    {
        await using var kit = await MaterialsKit.CreateAsync();
        var bolt = await kit.PartAsync("BOLT", method: PartCostMethod.Standard, standard: 1.5m, leadTime: 10, vendor: kit.Kit.Acme.Id);
        var frame = await kit.PartAsync("FRAME", PartSourceKind.Make, leadTime: 5);
        await kit.Manufacturing.SaveBomAsync(null, new SaveBomRequest(frame.Id, "A", [new SaveBomComponentRequest(bolt.Id, 4)]));
        await kit.StockAsync(bolt.Id, 10, 0);
        await kit.Parts.AddRequirementAsync(new SaveMaterialRequirementRequest(kit.Kit.Fx.Wbs1, frame.Id, 3, Day.AddDays(30)));

        var run = await kit.Mrp.RunAsync(new RunMrpRequest(90, Day));
        var make = run.PlannedOrders.Single(p => p.PartId == frame.Id);
        Assert.Equal((PlannedOrderType.Make, 3m, Day.AddDays(25), "1001"), (make.Kind, make.Quantity, make.ReleaseDate, make.ProjectCode));
        var buy = run.PlannedOrders.Single(p => p.PartId == bolt.Id);
        Assert.Equal((PlannedOrderType.Buy, 2m, Day.AddDays(15), "Acme Federal"), (buy.Kind, buy.Quantity, buy.ReleaseDate, buy.PreferredVendor));

        var firmed = await kit.Mrp.FirmAsync(new FirmPlannedOrdersRequest([make.Id, buy.Id]));
        var reqNumber = Assert.Single(firmed.Requisitions);
        var woNumber = Assert.Single(firmed.WorkOrders);

        kit.As(Finance);
        var req = (await kit.Kit.Requisitions().ListAsync(new RequisitionQuery())).Single(r => r.Number == reqNumber);
        Assert.Equal(RequisitionState.PendingApproval, req.Status);
        var line = Assert.Single((await kit.Kit.Requisitions().GetAsync(req.Id)).Lines);
        Assert.Equal(("1405", 2m, 1.5m, kit.Kit.Acme.Id), (line.AccountNumber, line.Quantity, line.UnitCost, line.SuggestedVendorId!.Value));

        kit.As(MaterialsUser);
        var wo = (await kit.Manufacturing.ListWorkOrdersAsync(new WorkOrderQuery(Search: woNumber))).Single();
        Assert.Equal((WorkOrderState.Planned, 3m, Day.AddDays(30)), (wo.Status, wo.Quantity, wo.DueDate));

        // Rerun: the requisition and the planned work order now cover everything.
        var again = await kit.Mrp.RunAsync(new RunMrpRequest(90, Day));
        Assert.Empty(again.PlannedOrders);
        var latest = await kit.Mrp.GetLatestAsync();
        Assert.Equal(again.Id, latest!.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Mrp.FirmAsync(new FirmPlannedOrdersRequest([make.Id])));

        // Once ordered and received, the requisition line's part goes into stock.
        kit.As(Admin);
        await kit.Kit.Requisitions().ApproveAsync(req.Id, new RequisitionDecisionRequest());
        kit.As(Finance);
        var po = await kit.Kit.Orders().CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest([req.Id], kit.Kit.Acme.Id, Day));
        po = await kit.Kit.Orders().IssueAsync(po.Order.Id);
        kit.As(MaterialsUser);
        var receivable = Assert.Single(await kit.Inventory.ListReceivableLinesAsync());
        Assert.Equal(("BOLT", 2m), (receivable.PartNumber, receivable.Open));
        await kit.Inventory.ReceivePurchaseOrderAsync(new ReceiveIntoInventoryRequest(po.Order.Id, Day, [new ReceiveIntoInventoryLine(receivable.PurchaseOrderLineId, 2)]));
        Assert.Equal(12m, (await kit.Parts.GetAsync(bolt.Id)).Summary.OnHand);
    }
}

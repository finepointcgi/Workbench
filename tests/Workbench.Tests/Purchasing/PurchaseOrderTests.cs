using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class PurchaseOrderTests
{
    [Fact]
    public async Task Approved_requisitions_become_a_purchase_order_and_are_marked_ordered()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var a = await kit.ApproveAllAsync(await kit.SubmitRequisitionAsync(kit.ProjectLine(4, 2_600m)));
        var b = await kit.ApproveAllAsync(await kit.SubmitRequisitionAsync(kit.ProjectLine(10, 25m, "Docking cables")));

        // Only approved requisitions can be ordered.
        var pending = await kit.SubmitRequisitionAsync(kit.ProjectLine(1, 5m));
        kit.As(Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders().CreateFromRequisitionsAsync(
            new CreatePurchaseOrderFromRequisitionsRequest([pending.Requisition.Id], kit.Acme.Id, PurchasingKit.Day)));

        Assert.Equal(2, (await kit.Orders().ListOrderableRequisitionLinesAsync()).Count);
        var po = await kit.Orders().CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest(
            [a.Requisition.Id, b.Requisition.Id], kit.Acme.Id, PurchasingKit.Day, ShipTo: "HQ dock 2", Receivers: [Editor]));
        Assert.Equal(PurchaseOrderState.Draft, po.Order.Status);
        Assert.Equal(10_650m, po.Order.Total);
        Assert.Equal("Net 30", po.Terms);
        Assert.Equal([a.Requisition.Number, b.Requisition.Number], po.Lines.Select(l => l.RequisitionNumber));
        Assert.Equal(0, po.Order.Committed);
        Assert.Empty(await kit.Orders().ListOrderableRequisitionLinesAsync());
        Assert.Equal(RequisitionState.Ordered, (await kit.Requisitions().GetAsync(a.Requisition.Id)).Requisition.Status);

        var issued = await kit.Orders().IssueAsync(po.Order.Id);
        Assert.Equal(PurchaseOrderState.Issued, issued.Order.Status);
        Assert.Equal(10_650m, issued.Order.Committed);

        // Cancelling puts the requisitions back to approved.
        var cancelled = await kit.Orders().CancelAsync(po.Order.Id, new PurchaseOrderReasonRequest("Wrong vendor"));
        Assert.Equal(PurchaseOrderState.Cancelled, cancelled.Order.Status);
        Assert.Equal(RequisitionState.Approved, (await kit.Requisitions().GetAsync(a.Requisition.Id)).Requisition.Status);
        Assert.Equal(2, (await kit.Orders().ListOrderableRequisitionLinesAsync()).Count);
    }

    [Fact]
    public async Task Change_orders_keep_a_revision_history()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m));
        var line = po.Lines[0];
        SavePurchaseOrderLineRequest Same(decimal quantity, decimal price) =>
            new(line.Description, quantity, price, WbsElementId: line.WbsElementId, OdcType: OdcCostType.Equipment, LineId: line.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders().ChangeOrderAsync(po.Order.Id, new ChangeOrderRequest("", [Same(5, 2_600m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders().ChangeOrderAsync(po.Order.Id, new ChangeOrderRequest("No change", [Same(4, 2_600m)])));

        var rev1 = await kit.Orders().ChangeOrderAsync(po.Order.Id, new ChangeOrderRequest("Added a fifth unit", [Same(5, 2_600m)]));
        Assert.Equal(1, rev1.Order.Revision);
        Assert.Equal(13_000m, rev1.Order.Total);

        var rev2 = await kit.Orders().ChangeOrderAsync(po.Order.Id, new ChangeOrderRequest("Vendor discount and a case",
            [Same(5, 2_500m), new SavePurchaseOrderLineRequest("Carry cases", 5, 40m, WbsElementId: kit.Fx.Wbs1)], Terms: "Net 45"));
        Assert.Equal(2, rev2.Order.Revision);
        Assert.Equal(12_700m, rev2.Order.Total);
        Assert.Equal(2, rev2.Lines.Count);
        Assert.Equal("Net 45", rev2.Terms);

        Assert.Collection(rev2.Revisions,
            r =>
            {
                Assert.Equal((1, "Added a fifth unit", 10_400m, 13_000m), (r.Revision, r.Reason, r.TotalBefore, r.TotalAfter));
                Assert.Equal(["Line 1 quantity: 4 → 5"], r.Changes);
            },
            r =>
            {
                Assert.Equal((2, 13_000m, 12_700m), (r.Revision, r.TotalBefore, r.TotalAfter));
                Assert.Contains("Line 1 unit price: 2600 → 2500", r.Changes);
                Assert.Contains(r.Changes, c => c.StartsWith("Line 2 added: Carry cases"));
                Assert.Contains("Order terms: Net 30 → Net 45", r.Changes);
            });

        // Once received, a line can't go below the received quantity or be removed.
        await kit.ReceiveAsync(rev2, 3, 0);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders().ChangeOrderAsync(po.Order.Id, new ChangeOrderRequest("Cut", [Same(2, 2_500m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Orders().ChangeOrderAsync(po.Order.Id,
            new ChangeOrderRequest("Drop laptops", [new SavePurchaseOrderLineRequest("Carry cases", 5, 40m, WbsElementId: kit.Fx.Wbs1, LineId: rev2.Lines[1].Id)])));
    }

    [Fact]
    public async Task Partial_receipts_move_the_order_through_partially_received_to_received()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m), kit.ProjectLine(1, 1_200m, "Site survey (service)"));

        var first = await kit.ReceiveAsync(po, 2, 0);
        Assert.Equal(PurchaseOrderState.PartiallyReceived, first.Order.Status);
        Assert.Equal([2m, 0m], first.Lines.Select(l => l.QuantityReceived));
        Assert.Equal(5_200m, first.Order.Received);
        var receipt = Assert.Single(first.Receipts);
        Assert.Equal(("RCV-000001", Finance), (receipt.Number, receipt.ReceivedBy));

        // Can't receive more than ordered.
        var over = await Assert.ThrowsAsync<ArgumentException>(() => kit.ReceiveAsync(first, 3, 0));
        Assert.Contains("2 still open", over.Message);

        var second = await kit.ReceiveAsync(first, 1.5m, 0);
        Assert.Equal(3.5m, second.Lines[0].QuantityReceived);
        Assert.Equal(PurchaseOrderState.PartiallyReceived, second.Order.Status);

        var done = await kit.ReceiveAsync(second, 0.5m, 1);
        Assert.Equal(PurchaseOrderState.Received, done.Order.Status);
        Assert.Equal(3, done.Receipts.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.ReceiveAsync(done, 1, 0));
    }

    [Fact]
    public async Task Service_lines_are_accepted_rather_than_delivered()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        kit.As(Finance);
        var po = await kit.Orders().CreateAsync(new SavePurchaseOrderRequest(kit.Acme.Id, PurchasingKit.Day,
            [new SavePurchaseOrderLineRequest("Network assessment", 1, 4_000m, WbsElementId: kit.Fx.Wbs1, OdcType: OdcCostType.Consultant, IsService: true)]));
        po = await kit.Orders().IssueAsync(po.Order.Id);
        var accepted = await kit.ReceiveAsync(po, 1);
        Assert.True(accepted.Receipts[0].Lines[0].IsAcceptance);
        Assert.Equal(PurchaseOrderState.Received, accepted.Order.Status);
    }

    [Fact]
    public async Task Open_po_amounts_are_commitments_until_billed_and_posted_and_never_hit_the_ledger()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var entriesBefore = (await kit.EntriesAsync()).Count;
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m));
        Assert.Equal(entriesBefore, (await kit.EntriesAsync()).Count);

        var commitments = await kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1);
        Assert.Equal(10_400m, commitments.Total);
        Assert.Equal(("1001.01", 10_400m), (commitments.ByWbs[0].WbsCode, commitments.ByWbs[0].Committed));

        // Receiving doesn't change the commitment; a submitted bill doesn't either (until it posts).
        po = await kit.ReceiveAsync(po, 2);
        var bill = await kit.BillPoLineAsync(po, "A-100", 2);
        Assert.Equal(10_400m, (await kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1)).Total);

        kit.As(Owner);
        var posted = await kit.Bills.ApproveAsync(bill.Bill.Id);
        Assert.Equal(BillState.Posted, posted.Bill.Status);
        kit.As(Finance);
        Assert.Equal(5_200m, (await kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1)).Total);
        Assert.Equal(5_200m, (await kit.Orders().GetAsync(po.Order.Id)).Order.Committed);

        // Closing the order releases what's left.
        await kit.Orders().CloseAsync(po.Order.Id);
        Assert.Equal(0m, (await kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1)).Total);

        // A project manager can see their project's commitments; others can't.
        kit.As(Owner);
        await kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1);
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Commitments().GetProjectCommitmentsAsync(kit.Fx.Project1));
    }

    [Fact]
    public async Task Orders_are_finance_only_except_for_their_receivers()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var req = await kit.ApproveAllAsync(await kit.SubmitRequisitionAsync(kit.ProjectLine(4, 100m)));
        var po = await kit.Orders().CreateFromRequisitionsAsync(new CreatePurchaseOrderFromRequisitionsRequest([req.Requisition.Id], kit.Acme.Id, PurchasingKit.Day, Receivers: [Editor]));
        po = await kit.Orders().IssueAsync(po.Order.Id);

        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders().ListAsync(new PurchaseOrderQuery()));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Orders().GetAsync(po.Order.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.ReceiveAsync(po, 1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders().CreateAsync(new SavePurchaseOrderRequest(kit.Acme.Id, PurchasingKit.Day, [])));
        Assert.Empty(await kit.Orders().ListReceivableAsync());

        // The configured receiver can see and receive it, but not change it.
        kit.As(Editor);
        Assert.Single(await kit.Orders().ListReceivableAsync());
        var received = await kit.ReceiveAsync(po, 2);
        Assert.True(received.CanReceive);
        Assert.False(received.CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Orders().CloseAsync(po.Order.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Match().ListExceptionsAsync());
    }
}

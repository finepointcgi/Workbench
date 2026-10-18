using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class ThreeWayMatchTests
{
    [Fact]
    public async Task A_bill_within_received_quantity_and_price_tolerance_matches_and_posts()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.ReceiveAsync(await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)), 2);

        // Within the 5% tolerance: 2,700 is 3.8% over.
        var bill = await kit.BillPoLineAsync(po, "A-100", 2, unitPrice: 2_700m);
        var line = Assert.Single(bill.Lines);
        Assert.Equal((5_400m, 2m, 2_700m, BillLineKind.ProjectCost, "1001.01"), (line.Amount, line.Quantity, line.UnitPrice, line.Kind, line.WbsCode));
        Assert.Null(line.MatchProblem);
        Assert.Equal($"{po.Order.Number} line 1", line.PurchaseOrderNumber);
        Assert.Empty(await kit.Match().ListExceptionsAsync());

        kit.As(Owner);
        Assert.Equal(BillState.Posted, (await kit.Bills.ApproveAsync(bill.Bill.Id)).Bill.Status);
        kit.As(Finance);
        var after = await kit.Orders().GetAsync(po.Order.Id);
        Assert.Equal((2m, 5_400m), (after.Lines[0].QuantityBilled, after.Lines[0].AmountBilled));
        Assert.Single(after.Bills);
    }

    [Fact]
    public async Task Billing_more_than_received_blocks_approval_until_it_is_received()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.ReceiveAsync(await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)), 2);
        var bill = await kit.BillPoLineAsync(po, "A-100", 3);
        Assert.Contains("Billed quantity 3 is more than the 2 received", bill.Lines[0].MatchProblem);

        var exception = Assert.Single(await kit.Match().ListExceptionsAsync());
        Assert.Equal((3m, 2m, 4m), (exception.QuantityBilled, exception.QuantityReceived, exception.QuantityOrdered));

        kit.As(Owner);
        var blocked = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));
        Assert.Contains("Three-way match failed", blocked.Message);

        // Receiving the third unit clears it.
        kit.As(Finance);
        await kit.ReceiveAsync(await kit.Orders().GetAsync(po.Order.Id), 1);
        Assert.Empty(await kit.Match().ListExceptionsAsync());
        kit.As(Owner);
        Assert.Equal(BillState.Posted, (await kit.Bills.ApproveAsync(bill.Bill.Id)).Bill.Status);
    }

    [Fact]
    public async Task A_second_bill_counts_what_earlier_bills_already_billed()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.ReceiveAsync(await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)), 2);
        await kit.BillPoLineAsync(po, "A-100", 2);
        var second = await kit.BillPoLineAsync(po, "A-101", 1);
        Assert.Contains("Billed quantity 3", second.Lines[0].MatchProblem);
    }

    [Fact]
    public async Task A_price_outside_the_tolerance_fails_and_finance_can_override_with_a_reason()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.ReceiveAsync(await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)), 2);
        var bill = await kit.BillPoLineAsync(po, "A-100", 2, unitPrice: 2_900m);
        Assert.Contains("11.54% over the PO price 2600 (tolerance 5%)", bill.Lines[0].MatchProblem);

        kit.As(Owner);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));

        // Not the PM (not Finance), not the person who entered the bill, and never without a reason.
        var lineId = bill.Lines[0].Id;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Match().OverrideAsync(lineId, new MatchOverrideRequest("Fine")));
        kit.As(Finance);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Match().OverrideAsync(lineId, new MatchOverrideRequest("Fine")));
        kit.As(Admin);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Match().OverrideAsync(lineId, new MatchOverrideRequest("")));
        var overridden = await kit.Match().OverrideAsync(lineId, new MatchOverrideRequest("Vendor price increase accepted by email 10/14"));
        Assert.Equal(Admin, overridden.OverrideBy);
        Assert.Empty(await kit.Match().ListExceptionsAsync());
        Assert.Single(await kit.Match().ListExceptionsAsync(includeOverridden: true));

        kit.As(Owner);
        var posted = await kit.Bills.ApproveAsync(bill.Bill.Id);
        Assert.Equal(BillState.Posted, posted.Bill.Status);
        Assert.Equal("Vendor price increase accepted by email 10/14", posted.Lines[0].MatchOverrideReason);

        kit.As(Admin);
        var audit = await kit.Get<Workbench.Contracts.ProjectAccounting.IAuditLogService>().ListAsync("ApBill", bill.Bill.Id);
        Assert.Contains(audit, a => a.Action == "MatchOverridden" && a.Reason == "Vendor price increase accepted by email 10/14");
    }

    [Fact]
    public async Task Create_bill_from_po_fills_in_what_is_received_and_not_yet_billed()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m), kit.AccountLine(2, 6_000m));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Match().BuildBillAsync(new CreateBillFromPurchaseOrderRequest(po.Order.Id, "A-1", PurchasingKit.Day)));

        po = await kit.ReceiveAsync(po, 2, 1);
        var request = await kit.Match().BuildBillAsync(new CreateBillFromPurchaseOrderRequest(po.Order.Id, "A-1", PurchasingKit.Day));
        Assert.Equal(kit.Acme.Id, request.VendorId);
        Assert.Collection(request.Lines,
            l => Assert.Equal((BillLineKind.ProjectCost, 2m, 5_200m), (l.Kind, l.Quantity!.Value, l.Amount)),
            l => Assert.Equal((BillLineKind.Account, 1m, 6_000m, kit.OrgId), (l.Kind, l.Quantity!.Value, l.Amount, l.OrgUnitId!.Value)));

        var bill = await kit.Bills.CreateAsync(request);
        await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest());

        // What's on that bill isn't offered again.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Match().BuildBillAsync(new CreateBillFromPurchaseOrderRequest(po.Order.Id, "A-2", PurchasingKit.Day)));
    }

    [Fact]
    public async Task A_bill_can_only_bill_its_own_vendors_open_orders()
    {
        await using var kit = await ProcurementKit.CreateAsync();
        var po = await kit.ReceiveAsync(await kit.IssuedOrderAsync(kit.ProjectLine(4, 2_600m)), 2);
        var wrongVendor = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 0, PurchaseOrderLineId: po.Lines[0].Id, Quantity: 1)])));
        Assert.Contains("another vendor", wrongVendor.Message);

        await kit.Orders().CloseAsync(po.Order.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.BillPoLineAsync(po, "A-1", 1));
    }
}

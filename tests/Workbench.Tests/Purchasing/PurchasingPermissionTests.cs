using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

/// <summary>Purchasing is Finance/Admin only, except that a project manager sees and decides the bill lines routed to them.</summary>
public sealed class PurchasingPermissionTests
{
    [Theory]
    [InlineData(Owner)]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    public async Task Everyone_outside_finance_is_refused(string email)
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(100m)]));
        kit.As(email);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Vendors.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Vendors.CreateAsync(new SaveVendorRequest("X", "X")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Vendors.SetTaxIdAsync(kit.Acme.Id, new SetVendorTaxIdRequest("47-1234821")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Subcontracts.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Subcontracts.AddFundingAsync(kit.Sub.Id, new AddSubcontractFundingRequest("M9", PurchasingKit.Day, 1m, 0, "x")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ListAsync(new BillQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "P-9", PurchasingKit.Day, [kit.OverheadLine(1m)])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Payments.ListDueBillsAsync(new DueBillQuery()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Ach, [new PaymentRunItemRequest(bill.Bill.Id, 1m)])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.GetAgingAsync(PurchasingKit.Day));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Reports.Export1099CsvAsync(2026));

        // A bill with no line routed to them doesn't exist as far as they're concerned.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Bills.GetAsync(bill.Bill.Id));
        Assert.Empty(await kit.Bills.ListApprovalQueueAsync());
    }

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    public async Task Finance_and_admins_can_use_purchasing(string email)
    {
        await using var kit = await PurchasingKit.CreateAsync();
        kit.As(email);
        Assert.Equal(3, (await kit.Vendors.ListAsync()).Count);
        Assert.Single(await kit.Subcontracts.ListAsync());
        Assert.Empty(await kit.Bills.ListAsync(new BillQuery()));
        Assert.Empty(await kit.Payments.ListRunsAsync());
        Assert.Empty((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows);
    }

    [Fact]
    public async Task A_project_manager_sees_the_bills_routed_to_them_and_their_projects_ODC_links()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.SubmitAsync(kit.Acme.Id, "A-1", PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m));

        kit.As(Owner);
        Assert.Equal(bill.Bill.Id, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Id);
        var posted = await kit.Bills.ApproveAsync(bill.Bill.Id);
        var odcId = posted.Lines[0].OdcEntryId!.Value;

        var links = await kit.Bills.GetOdcLinksAsync([odcId, kit.Fx.ExpenseOdcId]);
        var link = Assert.Single(links);
        Assert.Equal(kit.Acme.Id, link.VendorId);
        Assert.Equal(bill.Bill.Number, link.BillNumber);

        // Someone who doesn't manage the project gets nothing.
        kit.As(Viewer);
        Assert.Empty(await kit.Bills.GetOdcLinksAsync([odcId]));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Bills.GetAsync(bill.Bill.Id));
    }
}

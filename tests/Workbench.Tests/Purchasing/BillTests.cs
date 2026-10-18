using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class BillTests
{
    [Fact]
    public async Task An_on_hold_vendor_blocks_new_bills_and_payments()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(700m)]));
        var draft = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "P-2", PurchasingKit.Day, [kit.OverheadLine(50m)]));

        await kit.Vendors.UpdateAsync(kit.Prima.Id, new SaveVendorRequest("PRIMA", "Primavera Reseller LLC", VendorState.OnHold, Is1099: true));

        var create = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "P-3", PurchasingKit.Day, [kit.OverheadLine(10m)])));
        Assert.Contains("on hold", create.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(bill.Bill.Id, 700m)])));

        // A run approved before the hold can't be paid out while it's on hold either.
        await kit.Vendors.UpdateAsync(kit.Prima.Id, new SaveVendorRequest("PRIMA", "Primavera Reseller LLC", VendorState.Active, Is1099: true));
        var run = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(bill.Bill.Id, 700m)]));
        kit.As(Admin);
        await kit.Payments.ApproveRunAsync(run.Id);
        await kit.Vendors.UpdateAsync(kit.Prima.Id, new SaveVendorRequest("PRIMA", "Primavera Reseller LLC", VendorState.OnHold, Is1099: true));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordPaymentsAsync(run.Id,
            new RecordPaymentsRequest(PurchasingKit.Day, [new PaymentReferenceRequest(run.Payments[0].Id, "5001")])));
    }

    [Fact]
    public async Task Duplicate_invoices_are_detected()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await kit.SubmitAsync(kit.Prima.Id, "INV-77", kit.OverheadLine(1_250m));

        // Same vendor and invoice number: refused outright (ignoring case and spaces).
        var sameNumber = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, " inv-77 ", PurchasingKit.Day.AddDays(3), [kit.OverheadLine(5m)])));
        Assert.Contains("V-000001", sameNumber.Message);

        // Another vendor may use the same number.
        await kit.Bills.CreateAsync(new SaveBillRequest(kit.Bolt.Id, "INV-77", PurchasingKit.Day, [kit.OverheadLine(5m)]));

        // Same vendor, amount and date under a different number: a warning that must be confirmed.
        var check = await kit.Bills.CheckDuplicatesAsync(new DuplicateCheckRequest(kit.Prima.Id, "INV-78", PurchasingKit.Day, 1_250m));
        Assert.Equal("Same amount and invoice date", Assert.Single(check).Reason);
        var second = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "INV-78", PurchasingKit.Day, [kit.OverheadLine(1_250m)]));
        Assert.Single(second.PossibleDuplicates);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.SubmitAsync(second.Bill.Id, new SubmitBillRequest()));
        var submitted = await kit.Bills.SubmitAsync(second.Bill.Id, new SubmitBillRequest(ConfirmPossibleDuplicate: true));
        Assert.Equal(BillState.PendingApproval, submitted.Bill.Status);

        // Void bills don't count.
        await kit.Bills.VoidAsync(second.Bill.Id, new BillReasonRequest("Entered twice"));
        Assert.Single(await kit.Bills.CheckDuplicatesAsync(new DuplicateCheckRequest(kit.Prima.Id, "INV-78", PurchasingKit.Day, 1_250m)));
    }

    [Fact]
    public async Task Lines_route_to_the_project_manager_and_to_finance_and_nobody_approves_their_own_entry()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.SubmitAsync(kit.Acme.Id, "A-1", PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m), kit.OverheadLine(100m));
        Assert.Equal([Owner, "Finance"], bill.Lines.Select(l => l.RoutedTo));
        Assert.False(bill.CanApprove);

        // finance@ entered it: Finance-routed lines can't be approved by them.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));
        Assert.Empty(await kit.Bills.ListApprovalQueueAsync());

        // The PM sees and approves only the project line.
        kit.As(Owner);
        var queue = Assert.Single(await kit.Bills.ListApprovalQueueAsync());
        Assert.Equal(BillLineKind.ProjectCost, Assert.Single(queue.Lines).Kind);
        Assert.True((await kit.Bills.GetAsync(bill.Bill.Id)).CanApprove);
        var afterPm = await kit.Bills.ApproveAsync(bill.Bill.Id);
        Assert.Equal(BillState.PendingApproval, afterPm.Bill.Status);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));

        // Someone who's neither the PM nor Finance can't.
        kit.As(Viewer);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));

        // Another Finance/Admin user approves the overhead line; the bill posts.
        kit.As(Admin);
        var posted = await kit.Bills.ApproveAsync(bill.Bill.Id);
        Assert.Equal(BillState.Posted, posted.Bill.Status);
        Assert.All(posted.Lines, l => Assert.Equal(BillLineState.Approved, l.Status));
        Assert.Equal(Owner, posted.Lines[0].DecidedBy);
        Assert.Equal(Admin, posted.Lines[1].DecidedBy);
    }

    [Fact]
    public async Task A_project_line_from_the_projects_manager_goes_to_finance()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            // pm@ joins Finance so they can enter bills.
            var user = await db.Set<Domain.Identity.AppUser>().SingleAsync(u => u.Email == Owner);
            user.IsFinance = true;
            await db.SaveChangesAsync();
        }

        kit.As(Owner);
        var draft = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "A-9", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 80m)]));
        var bill = await kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest());
        Assert.Equal("Finance", Assert.Single(bill.Lines).RoutedTo);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(bill.Bill.Id));
        kit.As(Finance);
        Assert.Equal(BillState.Posted, (await kit.Bills.ApproveAsync(bill.Bill.Id)).Bill.Status);
    }

    [Fact]
    public async Task Approving_a_project_line_creates_an_approved_vendor_ODC_and_posts_it()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-2", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m)]));

        var odcId = Assert.Single(bill.Lines).OdcEntryId!.Value;
        await using var db = await kit.Factory.CreateAsync();
        var odc = await db.Set<OdcEntry>().SingleAsync(o => o.Id == odcId);
        Assert.Equal(OdcStatus.Approved, odc.Status);
        Assert.Equal(kit.Acme.Id, odc.VendorId);
        Assert.Equal("A-2", odc.InvoiceNumber);
        Assert.Equal(Owner, odc.ApprovedBy);

        // Debit project cost (the ODC rule), credit AP; nothing else for a bill with only project lines.
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Odc));
        Assert.Equal(odcId, entry.SourceId);
        Assert.Empty(await kit.EntriesAsync(JournalSource.AP));
        Assert.Equal(400m, await kit.BalanceAsync("5120"));
        Assert.Equal(-400m, await kit.BalanceAsync("2010"));
    }

    [Fact]
    public async Task A_linked_ODC_that_already_posted_is_never_posted_twice()
    {
        await using var kit = await PurchasingKit.CreateAsync();

        // The fixture's vendor ODC (500.00) was approved and posted through the ODC workflow before the invoice reached AP.
        await using (var db = await kit.Factory.CreateAsync())
        {
            var odc = await db.Set<OdcEntry>().SingleAsync(o => o.Id == kit.Fx.VendorOdcId);
            odc.VendorId = kit.Acme.Id;
            odc.Status = OdcStatus.Approved;
            odc.ApprovedBy = Owner;
            await db.SaveChangesAsync();
        }

        await kit.Get<PostingEngine>().PostOdcAsync(kit.Fx.VendorOdcId);
        var before = Assert.Single(await kit.EntriesAsync(JournalSource.Odc));

        var linkable = await kit.Bills.ListLinkableOdcsAsync(kit.Acme.Id);
        Assert.Contains(linkable, o => o.OdcEntryId == kit.Fx.VendorOdcId);
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "INV-1", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 0m, OdcEntryId: kit.Fx.VendorOdcId)]));

        var line = Assert.Single(bill.Lines);
        Assert.Equal(kit.Fx.VendorOdcId, line.OdcEntryId);
        Assert.Equal(500m, line.Amount);
        Assert.Equal(500m, bill.Bill.Payable);

        // Still one ODC entry, no AP entry, and accounts payable is credited once.
        Assert.Equal(before.Id, Assert.Single(await kit.EntriesAsync(JournalSource.Odc)).Id);
        Assert.Empty(await kit.EntriesAsync(JournalSource.AP));
        Assert.Equal(-500m, await kit.BalanceAsync("2010"));

        // Retrying the post is a no-op, and the ODC can't go on a second bill.
        Assert.Empty(await kit.Bills.ListLinkableOdcsAsync(kit.Acme.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "INV-1b", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 0m, OdcEntryId: kit.Fx.VendorOdcId)])));
        Assert.Single(await kit.EntriesAsync(JournalSource.Odc));
    }

    [Fact]
    public async Task Linking_an_unapproved_ODC_approves_and_posts_it_once()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "INV-1", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.ProjectCost, "", 500m, OdcEntryId: kit.Fx.VendorOdcId)]));
        Assert.Equal(BillState.Posted, bill.Bill.Status);

        await using var db = await kit.Factory.CreateAsync();
        Assert.Equal(OdcStatus.Approved, (await db.Set<OdcEntry>().SingleAsync(o => o.Id == kit.Fx.VendorOdcId)).Status);
        Assert.Equal(kit.Fx.VendorOdcId, Assert.Single(await kit.EntriesAsync(JournalSource.Odc)).SourceId);

        // The GL backlog doesn't see it as unposted.
        Assert.DoesNotContain(await kit.Get<PostingEngine>().ListUnpostedAsync(), i => i.SourceId == kit.Fx.VendorOdcId);
    }

    [Fact]
    public async Task Overhead_lines_post_to_their_account_and_org_and_credit_AP()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(2_400m), kit.OverheadLine(600m, "6120", "Rent")]));

        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.AP));
        Assert.Equal(bill.Bill.Id, entry.SourceId);
        Assert.Equal(3_000m, entry.TotalDebit);
        Assert.All(entry.Lines.Where(l => l.Debit > 0), l => Assert.Equal(kit.OrgId, l.OrgUnitId));
        Assert.Equal(2_400m, await kit.BalanceAsync("6230"));
        Assert.Equal(600m, await kit.BalanceAsync("6120"));
        Assert.Equal(-3_000m, await kit.BalanceAsync("2010"));
        Assert.Equal(bill.JournalEntryId, entry.Id);
    }

    [Fact]
    public async Task Retainage_on_subcontract_bills_moves_to_the_retainage_liability()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "S-1", PurchasingKit.Day, [kit.SubLine(1_000m)]));

        Assert.Equal(100m, bill.Bill.RetainageAmount);
        Assert.Equal(900m, bill.Bill.Payable);
        Assert.Equal(100m, Assert.Single(bill.Lines).RetainageAmount);

        // Cost and AP through the subcontract ODC; retainage reclassified from AP to 2020.
        Assert.Equal(1_000m, await kit.BalanceAsync("5140"));
        Assert.Equal(-900m, await kit.BalanceAsync("2010"));
        Assert.Equal(-100m, await kit.BalanceAsync("2020"));
        var ap = Assert.Single(await kit.EntriesAsync(JournalSource.AP));
        Assert.Equal(100m, ap.TotalDebit);

        // Only the payable part can be paid.
        var due = Assert.Single(await kit.Payments.ListDueBillsAsync(new DueBillQuery()));
        Assert.Equal(900m, due.Available);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Ach, [new PaymentRunItemRequest(bill.Bill.Id, 1_000m)])));

        var sub = (await kit.Subcontracts.GetAsync(kit.Sub.Id)).Subcontract;
        Assert.Equal(1_000m, sub.BilledToDate);
        Assert.Equal(100m, sub.RetainageHeld);
        Assert.Equal(9_000m, sub.RemainingFunding);
        Assert.Equal(19_000m, sub.RemainingCeiling);
    }

    [Fact]
    public async Task The_retainage_account_is_added_to_older_charts_when_needed()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Account>().Remove(await db.Set<Account>().SingleAsync(a => a.Number == "2020"));
            await db.SaveChangesAsync();
        }

        await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "S-1", PurchasingKit.Day, [kit.SubLine(500m)]));
        await using var check = await kit.Factory.CreateAsync();
        var account = await check.Set<Account>().SingleAsync(a => a.Number == "2020");
        Assert.Equal(AccountType.Liability, account.Type);
    }

    [Fact]
    public async Task Subcontract_invoices_over_funding_need_a_finance_override_and_never_pass_the_ceiling()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "S-1", PurchasingKit.Day, [kit.SubLine(8_000m)]));

        var draft = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "S-2", PurchasingKit.Day.AddDays(1), [kit.SubLine(3_000m)]));
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest()));
        Assert.Contains("funded value", refused.Message);

        var overridden = await kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest(FundingOverrideReason: "Mod P00002 in signature"));
        Assert.Equal(Finance, overridden.FundingOverrideBy);
        Assert.Equal("Mod P00002 in signature", overridden.FundingOverrideReason);
        Assert.Equal(-1_000m, (await kit.Subcontracts.GetAsync(kit.Sub.Id)).Subcontract.RemainingFunding);

        // The ceiling (20,000) is a hard stop, override or not.
        var big = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "S-3", PurchasingKit.Day.AddDays(2), [kit.SubLine(9_500m)]));
        var ceiling = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.SubmitAsync(big.Bill.Id, new SubmitBillRequest(FundingOverrideReason: "Please")));
        Assert.Contains("ceiling", ceiling.Message);

        // A modification raising the funding is the normal path.
        await kit.Subcontracts.AddFundingAsync(kit.Sub.Id, new AddSubcontractFundingRequest("M002", PurchasingKit.Day, 10_000m, 0, "Incremental funding"));
        await kit.Bills.UpdateAsync(big.Bill.Id, new SaveBillRequest(kit.Acme.Id, "S-3", PurchasingKit.Day.AddDays(2), [kit.SubLine(8_000m)]));
        Assert.Equal(BillState.PendingApproval, (await kit.Bills.SubmitAsync(big.Bill.Id, new SubmitBillRequest())).Bill.Status);
    }

    [Fact]
    public async Task Funding_actions_keep_a_running_total_within_the_ceiling()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var detail = await kit.Subcontracts.AddFundingAsync(kit.Sub.Id, new AddSubcontractFundingRequest("M002", PurchasingKit.Day, 5_000m, 5_000m, "Option 1"));
        Assert.Equal(15_000m, detail.Subcontract.FundedValue);
        Assert.Equal(25_000m, detail.Subcontract.CeilingValue);
        Assert.Equal(["M001", "M002"], detail.Funding.Select(f => f.Number));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Subcontracts.AddFundingAsync(kit.Sub.Id, new AddSubcontractFundingRequest("M003", PurchasingKit.Day, 20_000m, 0, "Too much")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Subcontracts.AddFundingAsync(kit.Sub.Id, new AddSubcontractFundingRequest("M002", PurchasingKit.Day, 1m, 0, "Repeat")));

        var flowDowns = await kit.Subcontracts.AddStandardFlowDownsAsync(kit.Sub.Id);
        Assert.Equal(PurchasingCatalog.StandardFlowDowns.Count, flowDowns.FlowDowns.Count);
        var saved = await kit.Subcontracts.SaveFlowDownAsync(kit.Sub.Id, new SaveFlowDownRequest("252.204-7012", null, true, "CMMC L2"));
        Assert.True(saved.FlowDowns.Single(f => f.ClauseNumber == "252.204-7012").IsIncluded);
        Assert.Equal(1, saved.Subcontract.FlowDownsIncluded);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Subcontracts.SaveFlowDownAsync(kit.Sub.Id, new SaveFlowDownRequest("FAR 52", null, true, null)));
    }

    [Fact]
    public async Task A_rejected_bill_goes_back_to_its_author_for_changes()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.SubmitAsync(kit.Acme.Id, "A-3", PurchasingKit.ProjectLine(kit.Fx.Wbs1, 50m));
        kit.As(Owner);
        var rejected = await kit.Bills.RejectAsync(bill.Bill.Id, new BillReasonRequest("Wrong WBS"));
        Assert.Equal(BillState.Rejected, rejected.Bill.Status);
        Assert.True(rejected.CanEdit);
        kit.As(Finance);
        await kit.Bills.UpdateAsync(bill.Bill.Id, new SaveBillRequest(kit.Acme.Id, "A-3", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 55m)]));
        Assert.Equal(BillState.PendingApproval, (await kit.Bills.SubmitAsync(bill.Bill.Id, new SubmitBillRequest())).Bill.Status);
    }
}

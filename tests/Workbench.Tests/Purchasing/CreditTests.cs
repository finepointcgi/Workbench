using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

/// <summary>Vendor credit and debit memos, applying them, payment-run netting, voiding posted bills and retainage releases.</summary>
public sealed class CreditTests
{
    private static IApCreditService Credits(PurchasingKit kit) => kit.Get<IApCreditService>();

    /// <summary>Enters (finance@) and approves a memo: pm@ for project lines, admin@ for the rest.</summary>
    private static async Task<BillDto> ApproveAsync(PurchasingKit kit, BillDto draft)
    {
        kit.As(Finance);
        var bill = await kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest());
        if (bill.Lines.Any(l => l.RoutedTo == Owner))
        {
            kit.As(Owner);
            bill = await kit.Bills.ApproveAsync(bill.Bill.Id);
        }

        if (bill.Bill.Status == BillState.PendingApproval)
        {
            kit.As(Admin);
            bill = await kit.Bills.ApproveAsync(bill.Bill.Id);
        }

        kit.As(Finance);
        Assert.Equal(BillState.Posted, bill.Bill.Status);
        return bill;
    }

    private static async Task<decimal> VendorProjectCostAsync(PurchasingKit kit, Guid vendorId)
    {
        await using var db = await kit.Factory.CreateAsync();
        return (await db.Set<OdcEntry>().AsNoTracking().Where(o => o.VendorId == vendorId && o.Status == OdcStatus.Approved && o.WbsElementId == kit.Fx.Wbs1).ToListAsync())
            .Sum(o => o.Amount);
    }

    private static async Task AssertBalancedAsync(PurchasingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        foreach (var period in await db.Set<FiscalPeriod>().AsNoTracking().Select(p => p.Id).ToListAsync())
        {
            Assert.True((await kit.Get<IAccountingReportService>().GetTrialBalanceAsync(period)).IsBalanced);
        }
    }

    [Fact]
    public async Task Crediting_a_project_bill_takes_the_cost_back_off_the_same_WBS_and_applies_to_the_bill()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day,
            [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m), kit.OverheadLine(600m, "6120", "Office supplies")]));
        Assert.Equal(400m, await VendorProjectCostAsync(kit, kit.Acme.Id));

        // "Credit this bill" for part of the project line: a draft memo with the line's charging.
        var draft = await Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-7", Reason: "Two servers returned",
            Lines: [new CreditBillLineRequest(bill.Lines[0].Id, 150m)]));
        Assert.Equal(BillKind.CreditMemo, draft.Bill.Kind);
        Assert.Equal(BillState.Draft, draft.Bill.Status);
        Assert.Equal(bill.Bill.Id, draft.Bill.CreditsBillId);
        var line = Assert.Single(draft.Lines);
        Assert.Equal((BillLineKind.ProjectCost, kit.Fx.Wbs1, Contracts.Odcs.OdcCostType.Materials, bill.Lines[0].Id), (line.Kind, line.WbsElementId!.Value, line.OdcType!.Value, line.CreditsLineId!.Value));

        // Same workflow as bills: the person who entered it can't approve; the project manager approves the project line.
        await kit.Bills.SubmitAsync(draft.Bill.Id, new SubmitBillRequest());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(draft.Bill.Id));
        Assert.Equal([Owner], (await kit.Bills.GetAsync(draft.Bill.Id)).Lines.Select(l => l.RoutedTo));
        kit.As(Owner);
        var memo = await kit.Bills.ApproveAsync(draft.Bill.Id);
        kit.As(Finance);
        Assert.Equal(BillState.Posted, memo.Bill.Status);

        // A negative approved ODC on the same WBS and cost type: project cost and the GL both drop by 150.
        Assert.Equal(250m, await VendorProjectCostAsync(kit, kit.Acme.Id));
        Assert.Equal(250m, await kit.BalanceAsync("5120"));
        Assert.Equal(-850m, await kit.BalanceAsync("2010"));

        // It went straight onto the bill it credits.
        var after = await kit.Bills.GetAsync(bill.Bill.Id);
        Assert.Equal(150m, after.Bill.AppliedAmount);
        Assert.Equal(850m, after.Bill.Balance);
        Assert.Equal(0m, (await kit.Bills.GetAsync(memo.Bill.Id)).Bill.Balance);
        var trail = await Credits(kit).GetTrailAsync(bill.Bill.Id);
        Assert.Equal(memo.Bill.Id, Assert.Single(trail.Related).Id);
        Assert.Equal(150m, Assert.Single(trail.Applications).Amount);

        // What's left to credit: 250 of the project line; more than that is refused.
        var tooMuch = await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-8",
            Lines: [new CreditBillLineRequest(bill.Lines[0].Id, 300m)])));
        Assert.Contains("250.00", tooMuch.Message);

        // Crediting everything else (the account line reverses the expense and its org).
        var rest = await ApproveAsync(kit, await Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-9")));
        Assert.Equal([250m, 600m], rest.Lines.Select(l => l.Amount));
        Assert.Equal(0m, await kit.BalanceAsync("5120"));
        Assert.Equal(0m, await kit.BalanceAsync("6120"));
        Assert.Equal(0m, await kit.BalanceAsync("2010"));
        Assert.True((await kit.Bills.GetAsync(bill.Bill.Id)).Bill.IsPaid);
        await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-10")));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task A_subcontract_credit_reverses_its_share_of_retainage_and_lowers_billed_to_date()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.SubLine(1_000m)]));
        var memo = await ApproveAsync(kit, await Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-1",
            Lines: [new CreditBillLineRequest(bill.Lines[0].Id, 400m)])));
        Assert.Equal(40m, memo.Bill.RetainageAmount);

        // Cost 1,000 − 400; AP 900 − 360; retainage 100 − 40.
        Assert.Equal(600m, await kit.BalanceAsync("5140"));
        Assert.Equal(-540m, await kit.BalanceAsync("2010"));
        Assert.Equal(-60m, await kit.BalanceAsync("2020"));
        var sub = (await kit.Subcontracts.GetAsync(kit.Sub.Id)).Subcontract;
        Assert.Equal(600m, sub.BilledToDate);
        Assert.Equal(60m, sub.RetainageHeld);
        Assert.Equal(540m, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Balance);
        var retainage = Assert.Single(await Credits(kit).ListRetainageAsync(kit.Sub.Id));
        Assert.Equal((100m, 40m, 60m), (retainage.Held, retainage.Credited, retainage.Open));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task A_debit_memo_stays_open_until_applied_and_can_be_unapplied_or_refunded()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(1_000m)]));
        var other = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(100m)]));

        // Our own memo: its number defaults to ours; Dr AP, Cr the expense.
        var draft = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "", PurchasingKit.Day, [kit.OverheadLine(300m, description: "Overbilled licenses")], Kind: BillKind.DebitMemo));
        Assert.Equal(draft.Bill.Number, draft.Bill.InvoiceNumber);
        var memo = await ApproveAsync(kit, draft);
        Assert.Equal(800m, await kit.BalanceAsync("6230"));
        Assert.Equal(-800m, await kit.BalanceAsync("2010"));
        var open = Assert.Single(await Credits(kit).ListOpenCreditsAsync(kit.Prima.Id));
        Assert.Equal(300m, open.Available);

        // A credit can't be paid, can't go to another vendor's bill, and can't exceed the credit or the bill.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(memo.Bill.Id, 1m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).ApplyAsync(memo.Bill.Id, new ApplyCreditRequest([new ApplyCreditItem(other.Bill.Id, 50m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).ApplyAsync(memo.Bill.Id, new ApplyCreditRequest([new ApplyCreditItem(bill.Bill.Id, 301m)])));

        // Apply 200, then take it back: no GL entry either way.
        var entries = (await kit.EntriesAsync()).Count;
        var applied = await Credits(kit).ApplyAsync(memo.Bill.Id, new ApplyCreditRequest([new ApplyCreditItem(bill.Bill.Id, 200m)]));
        Assert.Equal(100m, applied.Bill.Balance);
        Assert.Equal(800m, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Balance);
        var application = Assert.Single((await Credits(kit).GetTrailAsync(memo.Bill.Id)).Applications);
        var unapplied = await Credits(kit).UnapplyAsync(application.Id, new BillReasonRequest("Wrong bill"));
        Assert.Equal(300m, unapplied.Bill.Balance);
        Assert.Equal(1_000m, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Balance);
        Assert.Equal(CreditApplicationState.Reversed, Assert.Single((await Credits(kit).GetTrailAsync(memo.Bill.Id)).Applications).Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits(kit).UnapplyAsync(application.Id, new BillReasonRequest("Again")));
        Assert.Equal(entries, (await kit.EntriesAsync()).Count);

        // The vendor sends a check for 120: Dr cash, Cr AP; the rest stays open. Voiding it puts the credit back.
        var refunded = await Credits(kit).RecordRefundAsync(memo.Bill.Id, new RecordRefundRequest(120m, PurchasingKit.Day, "PR-55"));
        Assert.Equal(180m, refunded.Bill.Balance);
        Assert.Equal(120m, await kit.BalanceAsync("1010"));
        await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).RecordRefundAsync(memo.Bill.Id, new RecordRefundRequest(181m, PurchasingKit.Day, "PR-56")));
        var refund = Assert.Single((await Credits(kit).GetTrailAsync(memo.Bill.Id)).Refunds);
        Assert.Equal(300m, (await Credits(kit).VoidRefundAsync(refund.Id, new BillReasonRequest("Bounced"))).Bill.Balance);
        Assert.Equal(0m, await kit.BalanceAsync("1010"));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task Payment_runs_net_open_credits_and_cancelling_a_run_gives_them_back()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var big = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(1_000m)]));
        var small = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-2", PurchasingKit.Day.AddDays(1), [kit.OverheadLine(100m)]));
        var credit = await ApproveAsync(kit, await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "PC-1", PurchasingKit.Day, [kit.OverheadLine(400m)], Kind: BillKind.CreditMemo)));

        var due = await kit.Payments.ListDueBillsAsync(new DueBillQuery(VendorId: kit.Prima.Id));
        Assert.Equal(2, due.Count);
        Assert.All(due, d => Assert.Equal(400m, d.VendorCredits));

        // The run takes the credit off the first bill: one check for 600 + 100.
        var run = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check,
            [new PaymentRunItemRequest(big.Bill.Id, 1_000m), new PaymentRunItemRequest(small.Bill.Id, 100m)]));
        Assert.Equal(700m, run.Total);
        Assert.Equal(400m, run.CreditTotal);
        Assert.Equal(CreditApplicationState.Scheduled, Assert.Single(run.Credits!).Status);
        Assert.Equal(0m, Assert.Single(await Credits(kit).ListOpenCreditsAsync(kit.Prima.Id)).Available);
        Assert.Empty(await kit.Payments.ListDueBillsAsync(new DueBillQuery(VendorId: kit.Prima.Id)).ContinueWith(t => t.Result.Where(d => d.Available > 0).ToList()));

        // Cancelling releases the credit.
        await kit.Payments.CancelRunAsync(run.Id);
        Assert.Equal(400m, Assert.Single(await Credits(kit).ListOpenCreditsAsync(kit.Prima.Id)).Available);

        // A credit that covers a bill entirely needs no check; paying the run applies it on the payment date.
        var credit2 = await ApproveAsync(kit, await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "PC-2", PurchasingKit.Day, [kit.OverheadLine(700m)], Kind: BillKind.CreditMemo)));
        var paid = await kit.PayAsync(PurchasingKit.Day.AddDays(2), "40001", (big.Bill.Id, 1_000m), (small.Bill.Id, 100m));
        Assert.Equal(PaymentRunState.Completed, paid.Status);
        Assert.Equal(1_100m, paid.CreditTotal);
        Assert.Empty(paid.Payments);
        Assert.All(paid.Credits!, c => Assert.Equal((CreditApplicationState.Applied, PurchasingKit.Day.AddDays(2)), (c.Status, c.AppliedOn)));
        Assert.True((await kit.Bills.GetAsync(big.Bill.Id)).Bill.IsPaid);
        Assert.True((await kit.Bills.GetAsync(small.Bill.Id)).Bill.IsPaid);
        Assert.Equal(0m, (await kit.Bills.GetAsync(credit.Bill.Id)).Bill.Balance);
        Assert.Equal(0m, (await kit.Bills.GetAsync(credit2.Bill.Id)).Bill.Balance);
        Assert.Equal(0m, await kit.BalanceAsync("2010"));
        Assert.Equal(0m, await kit.BalanceAsync("1010"));

        // Without netting a run pays the bill in full.
        var third = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-3", PurchasingKit.Day, [kit.OverheadLine(50m)]));
        await ApproveAsync(kit, await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "PC-3", PurchasingKit.Day, [kit.OverheadLine(20m)], Kind: BillKind.CreditMemo)));
        var gross = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(third.Bill.Id, 50m)], NetCredits: false));
        Assert.Equal(50m, gross.Total);
        Assert.Empty(gross.Credits!);
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task The_aging_and_vendor_ledger_include_open_credits()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(1_000m)]));
        var credit = await ApproveAsync(kit, await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "PC-1", PurchasingKit.Day, [kit.OverheadLine(250m)], Kind: BillKind.CreditMemo)));

        var row = Assert.Single((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows);
        Assert.Equal((750m, -250m), (row.Total, row.Credits));
        Assert.Contains("OpenCredits", (await kit.Reports.ExportAgingCsvAsync(PurchasingKit.Day)).Content);

        // Applying moves nothing on the vendor's balance, it only settles the two.
        await Credits(kit).ApplyAsync(credit.Bill.Id, new ApplyCreditRequest([new ApplyCreditItem(bill.Bill.Id, 250m)], PurchasingKit.Day.AddDays(5)));
        Assert.Equal((750m, -250m), Assert.Single((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows) is var before ? (before.Total, before.Credits) : default);
        var later = Assert.Single((await kit.Reports.GetAgingAsync(PurchasingKit.Day.AddDays(5))).Rows);
        Assert.Equal((750m, 0m), (later.Total, later.Credits));

        var ledger = await kit.Reports.GetVendorLedgerAsync(kit.Prima.Id);
        Assert.Equal(["Bill", "Credit memo"], ledger.Lines.Select(l => l.Kind));
        Assert.Equal(750m, ledger.ClosingBalance);
        Assert.Equal(750m, -await kit.BalanceAsync("2010"));
    }

    [Fact]
    public async Task Voiding_a_posted_bill_reverses_every_posting_and_is_blocked_while_paid()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day,
            [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m), kit.OverheadLine(600m), kit.SubLine(1_000m)]));
        Assert.Equal(1_400m, await VendorProjectCostAsync(kit, kit.Acme.Id));

        // Paid: refused until the payment is voided.
        var run = await kit.PayAsync(PurchasingKit.Day, "50001", (bill.Bill.Id, 100m));
        var paid = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Duplicate")));
        Assert.Contains("Void the payment first", paid.Message);
        await kit.Payments.VoidPaymentAsync(run.Payments[0].Id, new VoidPaymentRequest("Duplicate"));

        // In a run: refused until the run is cancelled.
        var open = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(bill.Bill.Id, 100m)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Duplicate")));
        await kit.Payments.CancelRunAsync(open.Id);

        // Credited: refused until the memo is voided.
        var memo = await Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("CM-1", Lines: [new CreditBillLineRequest(bill.Lines[1].Id, 10m)]));
        Assert.Contains(memo.Bill.Number, (await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Duplicate")))).Message);
        await kit.Bills.VoidAsync(memo.Bill.Id, new BillReasonRequest("Not needed"));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest(" ")));
        var voided = await kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Duplicate invoice"));
        Assert.Equal(BillState.Void, voided.Bill.Status);
        Assert.Equal(PurchasingKit.Day, voided.VoidReversalDate);
        Assert.Equal(Finance, voided.VoidedBy);
        Assert.All(voided.Lines.Where(l => l.Kind != BillLineKind.Account), l => Assert.NotNull(l.OdcEntryId));

        // Project cost and every GL account back to zero; the subcontract isn't billed any more.
        Assert.Equal(0m, await VendorProjectCostAsync(kit, kit.Acme.Id));
        foreach (var account in new[] { "5120", "5140", "6230", "2010", "2020", "1010" })
        {
            Assert.Equal(0m, await kit.BalanceAsync(account));
        }

        var sub = (await kit.Subcontracts.GetAsync(kit.Sub.Id)).Subcontract;
        Assert.Equal((0m, 0m), (sub.BilledToDate, sub.RetainageHeld));
        Assert.Empty(await kit.Bills.ListLinkableOdcsAsync(kit.Acme.Id));
        Assert.Empty((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows);

        // The trail shows the original postings, their reversals and the void.
        var trail = await Credits(kit).GetTrailAsync(bill.Bill.Id);
        Assert.Contains(trail.JournalEntries, e => e.What.StartsWith("Void: project cost", StringComparison.Ordinal));
        Assert.Contains(trail.JournalEntries, e => e.What == "Reversal: Bill posting");
        Assert.Contains(trail.History, h => h.Action == "Voided" && h.Reason == "Duplicate invoice");
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Again")));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task A_void_in_a_closed_period_posts_in_the_current_period()
    {
        var today = new DateTimeOffset(2026, 11, 5, 15, 0, 0, TimeSpan.Zero);
        await using var kit = await PurchasingKit.CreateAsync(s => s.AddSingleton<TimeProvider>(new MyWork.MyWorkServiceTests.FixedClock(today)));
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [PurchasingKit.ProjectLine(kit.Fx.Wbs1, 400m), kit.OverheadLine(100m)]));
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<FiscalPeriod>().SingleAsync(p => p.Name == "2026-10")).Status = PeriodStatus.Closed;
            await db.SaveChangesAsync();
        }

        // No open period today: refused, nothing changes.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Wrong vendor")));
        Assert.Equal(BillState.Posted, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Status);

        await using (var db = await kit.Factory.CreateAsync())
        {
            var october = await db.Set<FiscalPeriod>().SingleAsync(p => p.Name == "2026-10");
            db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = october.FiscalYearId, Name = "2026-11", Number = 2, Start = new DateOnly(2026, 11, 1), End = new DateOnly(2026, 11, 30) });
            await db.SaveChangesAsync();
        }

        var voided = await kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("Wrong vendor"));
        Assert.Equal(new DateOnly(2026, 11, 5), voided.VoidReversalDate);
        var reversals = (await kit.EntriesAsync()).Where(e => e.Date == new DateOnly(2026, 11, 5)).ToList();
        Assert.Equal(2, reversals.Count); // the AP entry's reversal and the project cost offset
        Assert.Equal(0m, await VendorProjectCostAsync(kit, kit.Acme.Id));
        Assert.Equal(0m, await kit.BalanceAsync("2010"));

        // October keeps its original postings; November takes the reversal.
        var octoberAp = (await kit.EntriesAsync()).Where(e => e.Date.Month == 10).SelectMany(e => e.Lines).Where(l => l.AccountId == kit.Account("2010")).Sum(l => l.Debit - l.Credit);
        Assert.Equal(-500m, octoberAp);
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task Retainage_release_creates_a_payable_within_what_is_held()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var first = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.SubLine(1_000m)]));
        var second = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-2", PurchasingKit.Day, [kit.SubLine(2_000m)]));
        Assert.Equal([100m, 200m], (await Credits(kit).ListRetainageAsync(kit.Sub.Id)).Select(r => r.Open));

        // More than held is refused; a release goes for approval to the PM (never the person releasing).
        var over = await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id, [new RetainageReleaseItem(first.Bill.Id, 100.01m)], PurchasingKit.Day)));
        Assert.Contains("100.00", over.Message);
        var release = await Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id,
            [new RetainageReleaseItem(first.Bill.Id, 100m), new RetainageReleaseItem(second.Bill.Id, 50m)], PurchasingKit.Day, "A-RET-1"));
        Assert.Equal((BillKind.RetainageRelease, BillState.PendingApproval, 150m), (release.Bill.Kind, release.Bill.Status, release.Bill.Amount));
        Assert.All(release.Lines, l => Assert.Equal(Owner, l.RoutedTo));
        Assert.False(release.CanEdit);

        // While it waits, the amounts are reserved.
        Assert.Equal([0m, 150m], (await Credits(kit).ListRetainageAsync(kit.Sub.Id)).Select(r => r.Open));
        await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id, [new RetainageReleaseItem(first.Bill.Id, 1m)], PurchasingKit.Day)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.ApproveAsync(release.Bill.Id));

        kit.As(Owner);
        var posted = await kit.Bills.ApproveAsync(release.Bill.Id);
        kit.As(Finance);
        Assert.Equal(BillState.Posted, posted.Bill.Status);

        // Dr AP retainage, Cr AP; billed to date doesn't change; it's visible on the subcontract and paid like a bill.
        Assert.Equal(-150m, await kit.BalanceAsync("2020"));
        Assert.Equal(-2_850m, await kit.BalanceAsync("2010"));
        var sub = (await kit.Subcontracts.GetAsync(kit.Sub.Id));
        Assert.Equal((3_000m, 150m, 150m), (sub.Subcontract.BilledToDate, sub.Subcontract.RetainageHeld, sub.Subcontract.RetainageReleased));
        Assert.Contains(sub.Invoices, i => i.Kind == BillKind.RetainageRelease && i.BillId == release.Bill.Id);
        Assert.Contains(await kit.Payments.ListDueBillsAsync(new DueBillQuery()), d => d.Bill.Id == release.Bill.Id);
        await kit.PayAsync(PurchasingKit.Day, "60001", (release.Bill.Id, 150m));
        Assert.Equal(0m, Assert.Single((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows).Total - 2_700m);
        Assert.Equal(150m, Assert.Single((await kit.Reports.GetAgingAsync(PurchasingKit.Day)).Rows).Retainage);

        // The source bill can't be voided while its retainage is released.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Bills.VoidAsync(first.Bill.Id, new BillReasonRequest("x")));
        await AssertBalancedAsync(kit);
    }

    [Fact]
    public async Task Retainage_release_respects_the_subcontract_funding()
    {
        await using var kit = await PurchasingKit.CreateAsync();

        // Billed 10,500 against 10,000 funded (Finance overrode on submit).
        var bill = await kit.SubmitAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.SubLine(10_500m)]), new SubmitBillRequest(FundingOverrideReason: "Modification in signature"));
        kit.As(Owner);
        await kit.Bills.ApproveAsync(bill.Bill.Id);
        kit.As(Finance);

        var refused = await Assert.ThrowsAsync<ArgumentException>(() => Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id, [new RetainageReleaseItem(bill.Bill.Id, 100m)], PurchasingKit.Day)));
        Assert.Contains("funded value", refused.Message);
        var release = await Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id, [new RetainageReleaseItem(bill.Bill.Id, 100m)], PurchasingKit.Day,
            FundingOverrideReason: "Funding modification signed"));
        Assert.Equal("Funding modification signed", release.FundingOverrideReason);
        Assert.Equal(Finance, release.FundingOverrideBy);
    }

    [Fact]
    public async Task A_quality_return_to_vendor_becomes_an_AP_debit_memo()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        Guid qualityId;
        var clearing = new Account { Number = "1405", Name = "Inventory received not invoiced", Type = AccountType.Asset, NormalBalance = NormalBalance.Debit, IsActive = true };
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Account>().Add(clearing);
            var part = new Domain.Materials.Part { Number = "WIDGET-1", Description = "Widget" };
            db.Set<Domain.Materials.Part>().Add(part);
            var ncr = new Domain.Quality.Nonconformance { Number = "NCR-000001", PartId = part.Id, Description = "Cracked housing", VendorId = kit.Prima.Id, Quantity = 4, CreatedBy = Finance };
            db.Set<Domain.Quality.Nonconformance>().Add(ncr);
            var quality = new Domain.Quality.VendorDebitMemo
            {
                Number = "DM-000001", VendorId = kit.Prima.Id, NonconformanceId = ncr.Id, PartId = part.Id, Quantity = 4, UnitPrice = 25m, Amount = 100m,
                ReturnAuthorization = "RMA-88", Date = PurchasingKit.Day, CreatedBy = Finance, CreatedAtUtc = DateTime.UtcNow,
            };
            db.Set<Domain.Quality.VendorDebitMemo>().Add(quality);
            await db.SaveChangesAsync();
            qualityId = quality.Id;
        }

        var memo = await Credits(kit).CreateFromQualityReturnAsync(qualityId);
        Assert.Equal((BillKind.DebitMemo, BillState.Draft, "RMA RMA-88", 100m), (memo.Bill.Kind, memo.Bill.Status, memo.Bill.InvoiceNumber, memo.Bill.Amount));
        Assert.Equal("1405", Assert.Single(memo.Lines).AccountNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Credits(kit).CreateFromQualityReturnAsync(qualityId));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(Domain.Quality.DebitMemoStatus.Applied, (await db.Set<Domain.Quality.VendorDebitMemo>().SingleAsync()).Status);
        }

        await ApproveAsync(kit, memo);
        Assert.Equal(-100m, (await kit.EntriesAsync()).SelectMany(e => e.Lines).Where(l => l.AccountId == clearing.Id).Sum(l => l.Debit - l.Credit));
        Assert.Equal(100m, await kit.BalanceAsync("2010"));

        // Voiding the AP memo puts the return back to open.
        await kit.Bills.VoidAsync(memo.Bill.Id, new BillReasonRequest("Vendor replaced the parts"));
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Equal(Domain.Quality.DebitMemoStatus.Open, (await db.Set<Domain.Quality.VendorDebitMemo>().SingleAsync()).Status);
        }

        Assert.Equal(0m, await kit.BalanceAsync("2010"));
    }

    [Fact]
    public async Task A_bill_can_pay_a_card_issuer_liability_but_never_AP_itself()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var liability = (await kit.Get<IChartOfAccountsService>().ListAccountsAsync()).First(a => a.Type == AccountKind.Liability && !a.HasChildren && a.Number is not ("2010" or "2020"));
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "STMT-10", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.Account, "Card statement", 900m, AccountId: liability.Id, OrgUnitId: kit.OrgId)]));
        Assert.Equal(900m, await kit.BalanceAsync(liability.Number));
        Assert.Equal(-900m, await kit.BalanceAsync("2010"));
        Assert.Equal(BillState.Posted, bill.Bill.Status);
        var refused = await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "X-1", PurchasingKit.Day,
            [new SaveBillLineRequest(BillLineKind.Account, "AP", 1m, AccountId: kit.Account("2010"), OrgUnitId: kit.OrgId)])));
        Assert.Contains("other than accounts payable", refused.Message);
    }

    [Theory]
    [InlineData(Owner)]
    [InlineData(Viewer)]
    public async Task Credits_are_for_finance(string email)
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.SubLine(1_000m)]));
        var credit = await ApproveAsync(kit, await kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "AC-1", PurchasingKit.Day, [kit.OverheadLine(10m)], Kind: BillKind.CreditMemo)));
        kit.As(email);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "AC-2", PurchasingKit.Day, [kit.OverheadLine(1m)], Kind: BillKind.CreditMemo)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).CreditBillAsync(bill.Bill.Id, new CreditBillRequest("X")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).ListOpenCreditsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).ApplyAsync(credit.Bill.Id, new ApplyCreditRequest([new ApplyCreditItem(bill.Bill.Id, 1m)])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).RecordRefundAsync(credit.Bill.Id, new RecordRefundRequest(1m, PurchasingKit.Day, "R")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).ListRetainageAsync(kit.Sub.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).ReleaseRetainageAsync(new ReleaseRetainageRequest(kit.Sub.Id, [new RetainageReleaseItem(bill.Bill.Id, 1m)], PurchasingKit.Day)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Credits(kit).GetTrailAsync(bill.Bill.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Bills.VoidAsync(bill.Bill.Id, new BillReasonRequest("x")));
    }
}

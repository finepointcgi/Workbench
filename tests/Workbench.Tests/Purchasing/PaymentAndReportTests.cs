using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Purchasing;

public sealed class PaymentAndReportTests
{
    [Fact]
    public async Task A_payment_run_posts_debit_AP_credit_cash_and_a_void_reverses_it()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(2_400m)]));

        // The person who creates a run can't approve it, and it can't be paid before approval.
        var run = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(bill.Bill.Id, 1_000m)]));
        Assert.Equal(PaymentRunState.Draft, run.Status);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Payments.ApproveRunAsync(run.Id));
        var payment = Assert.Single(run.Payments);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kit.Payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(PurchasingKit.Day, [new PaymentReferenceRequest(payment.Id, "10001")])));

        // Scheduled amounts aren't offered again.
        Assert.Equal(1_400m, Assert.Single(await kit.Payments.ListDueBillsAsync(new DueBillQuery())).Available);

        kit.As(Admin);
        await kit.Payments.ApproveRunAsync(run.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(PurchasingKit.Day, [])));
        var paid = await kit.Payments.RecordPaymentsAsync(run.Id, new RecordPaymentsRequest(PurchasingKit.Day, [new PaymentReferenceRequest(payment.Id, "10001")]));
        Assert.Equal(PaymentRunState.Completed, paid.Status);
        var p = Assert.Single(paid.Payments);
        Assert.Equal(PaymentState.Paid, p.Status);
        Assert.Equal("10001", p.Reference);

        var entry = (await kit.EntriesAsync(JournalSource.AP)).Single(e => e.SourceId == p.Id);
        Assert.Equal(1_000m, entry.Lines.Single(l => l.AccountId == kit.Account("2010")).Debit);
        Assert.Equal(1_000m, entry.Lines.Single(l => l.AccountId == kit.Account("1010")).Credit);
        Assert.Equal(-1_400m, await kit.BalanceAsync("2010"));
        Assert.Equal(-1_000m, await kit.BalanceAsync("1010"));

        // Partial payment: the rest stays due.
        kit.As(Finance);
        var after = await kit.Bills.GetAsync(bill.Bill.Id);
        Assert.Equal(1_000m, after.Bill.PaidAmount);
        Assert.Equal(1_400m, after.Bill.Balance);
        Assert.False(after.Bill.IsPaid);

        // Voiding reverses the entry and reopens the balance.
        var voided = await kit.Payments.VoidPaymentAsync(p.Id, new VoidPaymentRequest("Check lost in the mail"));
        Assert.Equal(PaymentState.Void, voided.Status);
        Assert.Equal(0m, await kit.BalanceAsync("1010"));
        Assert.Equal(-2_400m, await kit.BalanceAsync("2010"));
        Assert.Equal(JournalStatus.Reversed, (await kit.EntriesAsync(JournalSource.AP)).Single(e => e.SourceId == p.Id).Status);
        Assert.Single(await kit.EntriesAsync(JournalSource.Reversal));
        Assert.Equal(2_400m, (await kit.Bills.GetAsync(bill.Bill.Id)).Bill.Balance);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Payments.VoidPaymentAsync(p.Id, new VoidPaymentRequest("Again")));

        // Paying the full balance marks it paid.
        await kit.PayAsync(PurchasingKit.Day, "10002", (bill.Bill.Id, 2_400m));
        Assert.True((await kit.Bills.GetAsync(bill.Bill.Id)).Bill.IsPaid);
        Assert.Empty(await kit.Payments.ListDueBillsAsync(new DueBillQuery()));

        // The trial balance still balances.
        var period = (await kit.Get<Contracts.ProjectAccounting.IFiscalCalendarService>().ListPeriodsAsync()).Single().Id;
        Assert.True((await kit.Get<IAccountingReportService>().GetTrialBalanceAsync(period)).IsBalanced);
    }

    [Fact]
    public async Task Check_numbers_are_unique_and_exports_list_the_run()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var a = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(100m)]));
        var b = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(200m)]));
        var run = await kit.PayAsync(PurchasingKit.Day, "20001", (a.Bill.Id, 100m));

        var ach = await kit.Payments.ExportAchAsync(run.Id);
        Assert.Contains("PRIMA", ach.Content);
        Assert.Contains("100.00", ach.Content);
        var positive = await kit.Payments.ExportPositivePayAsync(run.Id);
        Assert.StartsWith("AccountNumber,CheckNumber", positive.Content);
        Assert.Contains("20001", positive.Content);

        var second = await kit.Payments.CreateRunAsync(new CreatePaymentRunRequest(PurchasingKit.Day, PaymentKind.Check, [new PaymentRunItemRequest(b.Bill.Id, 200m)]));
        kit.As(Admin);
        await kit.Payments.ApproveRunAsync(second.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordPaymentsAsync(second.Id,
            new RecordPaymentsRequest(PurchasingKit.Day, [new PaymentReferenceRequest(second.Payments[0].Id, "20001")])));
    }

    [Fact]
    public async Task The_1099_report_lists_1099_vendors_paid_600_or_more_by_box()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var prima1 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(400m)]));
        var prima2 = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-2", PurchasingKit.Day, [kit.OverheadLine(200m)]));
        var bolt = await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(599.99m)]));
        var acme = await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.OverheadLine(5_000m)]));
        await kit.PayAsync(PurchasingKit.Day, "30001", (prima1.Bill.Id, 400m), (prima2.Bill.Id, 200m), (bolt.Bill.Id, 599.99m), (acme.Bill.Id, 5_000m));
        await kit.Vendors.SetTaxIdAsync(kit.Prima.Id, new SetVendorTaxIdRequest("58-9061177"));

        var report = await kit.Reports.Get1099Async(PurchasingKit.Day.Year);
        var row = Assert.Single(report.Rows);
        Assert.Equal("PRIMA", row.VendorCode);
        Assert.Equal(600m, row.Amount);
        Assert.Equal(2, row.Payments);
        Assert.Equal("NEC-1", row.Box);
        Assert.Equal("•••••1177", row.TaxIdMasked);
        Assert.Equal("BOLT", Assert.Single(report.BelowThreshold).VendorCode);
        Assert.Empty((await kit.Reports.Get1099Async(PurchasingKit.Day.Year + 1)).Rows);

        // The export carries the masked TIN only.
        var csv = await kit.Reports.Export1099CsvAsync(PurchasingKit.Day.Year);
        Assert.Contains("•••••1177", csv.Content);
        Assert.DoesNotContain("9061177", csv.Content);
        Assert.DoesNotContain("ACME", csv.Content);
    }

    [Fact]
    public async Task Aging_buckets_open_balances_by_days_past_due()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        var asOf = new DateOnly(2027, 2, 1);
        async Task<BillDto> BillAsync(string invoice, decimal amount, DateOnly due) =>
            await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, invoice, PurchasingKit.Day, [kit.OverheadLine(amount)], due));
        await BillAsync("CUR", 10m, new DateOnly(2027, 2, 10));     // not yet due
        await BillAsync("D17", 20m, new DateOnly(2027, 1, 15));     // 17 days past due
        await BillAsync("D30", 25m, new DateOnly(2027, 1, 2));      // exactly 30
        await BillAsync("D48", 30m, new DateOnly(2026, 12, 15));    // 48
        await BillAsync("D78", 40m, new DateOnly(2026, 11, 15));    // 78
        var old = await BillAsync("D104", 50m, new DateOnly(2026, 10, 20));   // 104
        await kit.PayAsync(PurchasingKit.Day, "40001", (old.Bill.Id, 20m)); // partly paid
        await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "S-1", PurchasingKit.Day, [kit.SubLine(1_000m)], new DateOnly(2027, 2, 1)));

        var aging = await kit.Reports.GetAgingAsync(asOf);
        var prima = aging.Rows.Single(r => r.VendorCode == "PRIMA");
        Assert.Equal(10m, prima.Current);
        Assert.Equal(45m, prima.Days1To30);
        Assert.Equal(30m, prima.Days31To60);
        Assert.Equal(40m, prima.Days61To90);
        Assert.Equal(30m, prima.Over90);
        Assert.Equal(155m, prima.Total);

        // Retainage is held, not due: ACME owes 900 (due today, current) with 100 held.
        var acme = aging.Rows.Single(r => r.VendorCode == "ACME");
        Assert.Equal(900m, acme.Current);
        Assert.Equal(100m, acme.Retainage);

        // Before the invoices existed there's nothing; payments after the as-of date don't count.
        Assert.Empty((await kit.Reports.GetAgingAsync(PurchasingKit.Day.AddDays(-1))).Rows);

        var ledger = await kit.Reports.GetVendorLedgerAsync(kit.Prima.Id);
        Assert.Equal(155m, ledger.ClosingBalance);
        Assert.Contains(ledger.Lines, l => l.Kind == "Payment" && l.Payments == 20m);
    }

    [Fact]
    public async Task Small_business_report_splits_posted_dollars_by_size_and_category()
    {
        await using var kit = await PurchasingKit.CreateAsync();
        await kit.PostedBillAsync(new SaveBillRequest(kit.Acme.Id, "A-1", PurchasingKit.Day, [kit.OverheadLine(300m)]));
        await kit.PostedBillAsync(new SaveBillRequest(kit.Bolt.Id, "B-1", PurchasingKit.Day, [kit.OverheadLine(100m)]));

        var report = await kit.Reports.GetSmallBusinessAsync(new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31));
        Assert.Equal(400m, report.Total);
        Assert.Equal(75m, report.BySize.Single(r => r.Category == "Small").Percent);
        Assert.Equal(100m, report.BySize.Single(r => r.Category == "Large").Amount);
        Assert.Equal(300m, report.BySocioEconomic.Single(r => r.Category == "SDVOSB").Amount);
        Assert.Equal(0m, (await kit.Reports.GetSmallBusinessAsync(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))).Total);
    }
}

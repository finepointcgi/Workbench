using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Currencies;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Purchasing;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Currencies;

/// <summary>
/// Multicurrency over the real registrations (<see cref="PurchasingKit"/>: period 2026-10 open, default chart). Adds
/// period 2026-11, EUR and GBP with spot rates on Oct 1 (EUR 1.10, GBP 1.32) and Oct 20 (EUR 1.12, GBP 1.35), and month-end
/// rates on Oct 31 (EUR 1.15, GBP 1.30).
/// </summary>
public sealed class MulticurrencyTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);
    private static readonly DateOnly Oct20 = new(2026, 10, 20);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);

    private static async Task<PurchasingKit> KitAsync()
    {
        var kit = await PurchasingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            var year = await db.Set<FiscalYear>().FirstAsync();
            db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-11", Number = 2, Start = Nov1, End = new DateOnly(2026, 11, 30) });
            await db.SaveChangesAsync();
        }

        var fx = kit.Get<ICurrencyService>();
        await fx.SaveCurrencyAsync(new SaveCurrencyRequest("EUR", "Euro"));
        await fx.SaveCurrencyAsync(new SaveCurrencyRequest("GBP", "Pound sterling"));
        var result = await fx.ImportRatesAsync(new ImportExchangeRatesRequest(
            "From,To,Type,EffectiveDate,Rate\n" +
            "EUR,USD,Spot,2026-10-01,1.10\nEUR,USD,Spot,2026-10-20,1.12\nEUR,USD,MonthEnd,2026-10-31,1.15\n" +
            "GBP,USD,Spot,2026-10-01,1.32\nGBP,USD,Spot,2026-10-20,1.35\nGBP,USD,MonthEnd,2026-10-31,1.30\n"));
        Assert.Equal(6, result.Added);
        Assert.Empty(result.Errors);
        return kit;
    }

    private static SaveBillRequest EuroBill(PurchasingKit kit, decimal euros, string invoice = "BT-1") =>
        new(kit.Prima.Id, invoice, PurchasingKit.Day, [kit.OverheadLine(euros, description: "Engineering review")], CurrencyCode: "EUR");

    private static void AssertBalanced(JournalEntry entry) => Assert.Equal(entry.Lines.Sum(l => l.Debit), entry.Lines.Sum(l => l.Credit));

    // ---- Rates ----

    [Fact]
    public async Task Rates_are_found_on_or_before_the_date_inverted_and_with_a_flagged_fallback()
    {
        await using var kit = await KitAsync();
        var fx = kit.Get<ICurrencyService>();

        Assert.Equal(1.10m, (await fx.GetRateAsync("EUR", "USD", new DateOnly(2026, 10, 19))).Rate);
        Assert.Equal(1.12m, (await fx.GetRateAsync("EUR", "USD", Oct20)).Rate);
        Assert.Equal(CurrencyMathInverse(1.12m), (await fx.GetRateAsync("USD", "EUR", Oct20)).Rate);

        var monthEnd = await fx.GetRateAsync("EUR", "USD", Oct20, RateKind.MonthEnd); // no month-end rate yet on Oct 20
        Assert.True(monthEnd.IsFallback);
        Assert.Equal(RateKind.Spot, monthEnd.RateType);
        Assert.False((await fx.GetRateAsync("EUR", "USD", Oct31, RateKind.MonthEnd)).IsFallback);

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => fx.GetRateAsync("EUR", "USD", new DateOnly(2026, 9, 30)));
    }

    private static decimal CurrencyMathInverse(decimal rate) => Application.Currencies.CurrencyMath.Invert(rate);

    [Fact]
    public async Task Csv_import_replaces_a_dated_rate_reports_problems_and_dry_runs_save_nothing()
    {
        await using var kit = await KitAsync();
        var fx = kit.Get<ICurrencyService>();

        var dry = await fx.ImportRatesAsync(new ImportExchangeRatesRequest("From,To,Type,EffectiveDate,Rate\nEUR,USD,Spot,2026-10-01,1.11\n", DryRun: true));
        Assert.Equal((0, 1, true), (dry.Added, dry.Replaced, dry.DryRun));
        Assert.Equal(1.10m, (await fx.GetRateAsync("EUR", "USD", Oct1)).Rate);

        var result = await fx.ImportRatesAsync(new ImportExchangeRatesRequest(
            "From,To,Type,EffectiveDate,Rate\nEUR,USD,Spot,2026-10-01,1.11\nEUR,USD,Spot,2026-10-20,1.12\nCHF,USD,Spot,2026-10-01,1.1\nEUR,USD,Weekly,2026-10-01,1\nEUR,USD,Spot,10/02/2026,1\n"));
        Assert.Equal((0, 1, 1), (result.Added, result.Replaced, result.Unchanged));
        Assert.Equal(3, result.Errors.Count);
        Assert.Contains(result.Errors, e => e.Contains("CHF"));
        Assert.Equal(1.11m, (await fx.GetRateAsync("EUR", "USD", Oct1)).Rate);
    }

    [Fact]
    public async Task Without_a_registered_provider_nothing_is_fetched()
    {
        await using var kit = await KitAsync();
        var fx = kit.Get<ICurrencyService>();
        Assert.Empty(await fx.ListRateProvidersAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => fx.FetchRatesAsync(new FetchExchangeRatesRequest("ECB", Oct1)));
    }

    // ---- Accounts payable ----

    [Fact]
    public async Task A_euro_bill_keeps_its_currency_rate_and_functional_amount()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(EuroBill(kit, 1_000m));

        Assert.Equal(("EUR", 1.10m, 1_000m, 1_100m), (bill.Bill.CurrencyCode, bill.Bill.ExchangeRate, bill.Bill.TransactionAmount, bill.Bill.Amount));
        Assert.Equal(1_000m, bill.Bill.TransactionBalance);
        Assert.Equal(1_000m, bill.Lines.Single().TransactionAmount);
        var entry = (await kit.EntriesAsync(JournalSource.AP)).Single(e => e.SourceId == bill.Bill.Id);
        AssertBalanced(entry);
        var payable = entry.Lines.Single(l => l.AccountId == kit.Account("2010"));
        Assert.Equal((1_100m, "EUR", 1_000m, 1.10m), (payable.Credit, payable.TransactionCurrency, payable.TransactionAmount, payable.ExchangeRate));
    }

    [Fact]
    public async Task A_vendors_currency_is_the_default_and_po_lines_are_refused_in_another_currency()
    {
        await using var kit = await KitAsync();
        await kit.Get<ICurrencyService>().SetPartyCurrencyAsync(new SetPartyCurrencyRequest(CurrencyPartyKind.Vendor, kit.Prima.Id, "EUR"));
        var bill = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "BT-2", PurchasingKit.Day, [kit.OverheadLine(500m)]));
        Assert.Equal(("EUR", 550m), (bill.Bill.CurrencyCode, bill.Bill.Amount));

        // Choosing the functional currency explicitly overrides the vendor's.
        var usd = await kit.Bills.CreateAsync(new SaveBillRequest(kit.Prima.Id, "BT-3", PurchasingKit.Day, [kit.OverheadLine(500m)], CurrencyCode: "USD"));
        Assert.Null(usd.Bill.CurrencyCode);
        Assert.Equal(500m, usd.Bill.Amount);

        var odcLine = new SaveBillLineRequest(BillLineKind.ProjectCost, "Linked", 500m, OdcEntryId: kit.Fx.VendorOdcId);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Bills.CreateAsync(new SaveBillRequest(kit.Acme.Id, "BT-4", PurchasingKit.Day, [odcLine], CurrencyCode: "EUR")));
    }

    [Fact]
    public async Task Paying_at_another_rate_posts_a_balanced_realized_loss_and_settles_the_bill()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(EuroBill(kit, 1_000m));

        var run = await kit.PayAsync(Oct20, "5001", (bill.Bill.Id, 1_000m));

        var payment = run.Payments.Single();
        Assert.Equal(("EUR", 1_000m, 1.12m, 1_100m, 1_120m, -20m), (payment.CurrencyCode, payment.TransactionAmount, payment.ExchangeRate, payment.Amount, payment.CashAmount, payment.RealizedGainLoss));
        var entry = (await kit.EntriesAsync(JournalSource.AP)).Single(e => e.SourceId == payment.Id);
        AssertBalanced(entry);
        Assert.Equal(1_100m, entry.Lines.Single(l => l.AccountId == kit.Account("2010")).Debit);
        Assert.Equal(1_120m, entry.Lines.Single(l => l.AccountId == kit.Account("1010")).Credit);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == kit.Account("4920")).Debit);

        var paid = await kit.Bills.GetAsync(bill.Bill.Id);
        Assert.Equal((0m, 0m, true), (paid.Bill.Balance, paid.Bill.TransactionBalance, paid.Bill.IsPaid));
        Assert.Equal(0m, await kit.BalanceAsync("2010"));
    }

    [Fact]
    public async Task Partial_payments_at_different_rates_close_the_bill_without_a_rounding_cent()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(EuroBill(kit, 1_000m));

        var first = (await kit.PayAsync(Oct20, "5001", (bill.Bill.Id, 400m))).Payments.Single();
        Assert.Equal((440m, 448m, -8m), (first.Amount, first.CashAmount!.Value, first.RealizedGainLoss));

        await kit.Get<ICurrencyService>().SaveRateAsync(new SaveExchangeRateRequest("EUR", "USD", RateKind.Spot, new DateOnly(2026, 10, 25), 1.0833m));
        var second = (await kit.PayAsync(new DateOnly(2026, 10, 25), "5002", (bill.Bill.Id, 600m))).Payments.Single();
        Assert.Equal((660m, 649.98m, 10.02m), (second.Amount, second.CashAmount!.Value, second.RealizedGainLoss));

        var paid = await kit.Bills.GetAsync(bill.Bill.Id);
        Assert.Equal((0m, 0m), (paid.Bill.Balance, paid.Bill.TransactionBalance));
        Assert.All(await kit.EntriesAsync(JournalSource.AP), AssertBalanced);
        Assert.Equal(-2.02m, await kit.BalanceAsync("4920")); // 8.00 loss (debit) less 10.02 gain (credit)
    }

    [Fact]
    public async Task Voiding_a_foreign_payment_reverses_it_and_reopens_the_bill_in_its_currency()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(EuroBill(kit, 1_000m));
        var payment = (await kit.PayAsync(Oct20, "5001", (bill.Bill.Id, 1_000m))).Payments.Single();

        await kit.Payments.VoidPaymentAsync(payment.Id, new VoidPaymentRequest("Check lost"));

        var reopened = await kit.Bills.GetAsync(bill.Bill.Id);
        Assert.Equal((1_100m, 1_000m), (reopened.Bill.Balance, reopened.Bill.TransactionBalance));
        var reversal = (await kit.EntriesAsync(JournalSource.Reversal)).Single();
        AssertBalanced(reversal);
        Assert.All(reversal.Lines.Where(l => l.AccountId != kit.Account("4920")), l => Assert.Equal("EUR", l.TransactionCurrency));
        Assert.Equal(0m, await kit.BalanceAsync("4920"));
    }

    [Fact]
    public async Task Dollar_bills_and_payments_post_exactly_as_before()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "P-1", PurchasingKit.Day, [kit.OverheadLine(1_000m)]));
        var payment = (await kit.PayAsync(Oct20, "5001", (bill.Bill.Id, 1_000m))).Payments.Single();

        Assert.Equal((null, 1m, 0m, null, 0m), (payment.CurrencyCode, payment.ExchangeRate, payment.TransactionAmount, payment.CashAmount, payment.RealizedGainLoss));
        Assert.Null(bill.Bill.CurrencyCode);
        var entries = await kit.EntriesAsync(JournalSource.AP);
        Assert.All(entries.SelectMany(e => e.Lines), l => Assert.Null(l.TransactionCurrency));
        var pay = entries.Single(e => e.SourceId == payment.Id);
        Assert.Equal(2, pay.Lines.Count);
        Assert.Equal((1_000m, 1_000m), (pay.Lines.Single(l => l.AccountId == kit.Account("2010")).Debit, pay.Lines.Single(l => l.AccountId == kit.Account("1010")).Credit));
        Assert.DoesNotContain(await kit.Get<IChartOfAccountsService>().ListAccountsAsync(), a => a.HasPostings && a.Number is "4920" or "4930");
    }

    // ---- Accounts receivable ----

    /// <summary>An approved invoice on contract C1 for $1,320.00, issued in GBP (customer Army billed in pounds).</summary>
    private static async Task<InvoiceDto> PoundInvoiceAsync(PurchasingKit kit, decimal dollars = 1_320m)
    {
        Guid customerId;
        var invoice = new Invoice
        {
            ContractId = kit.Fx.Contract1,
            Sequence = 1,
            Number = "C1-0001",
            Type = InvoiceType.FixedPriceMilestone,
            PeriodFrom = Oct1,
            PeriodTo = Oct31,
            InvoiceDate = Oct1,
            DueDate = Oct1.AddDays(30),
            CreatedBy = Finance,
            CreatedAtUtc = DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow,
        };
        await using (var db = await kit.Factory.CreateAsync())
        {
            customerId = (await db.Set<Contract>().FirstAsync(c => c.Id == kit.Fx.Contract1)).CustomerId;
            invoice.CustomerId = customerId;
            invoice.Lines.Add(new InvoiceLine { InvoiceId = invoice.Id, LineNumber = 1, Kind = InvoiceLineKind.Milestone, Description = "Milestone 1", Quantity = 1, Rate = dollars, Amount = dollars, IsGenerated = false });
            invoice.Recalculate();
            db.Set<Invoice>().Add(invoice);
            await db.SaveChangesAsync();
        }

        kit.As(Finance);
        await kit.Get<ICurrencyService>().SetInvoiceCurrencyAsync(invoice.Id, new SetInvoiceCurrencyRequest("GBP"));
        return await kit.Get<IInvoiceService>().ApproveAsync(invoice.Id, new ApproveInvoiceRequest());
    }

    [Fact]
    public async Task An_invoice_to_a_foreign_customer_is_issued_in_their_currency_at_approval()
    {
        await using var kit = await KitAsync();
        var invoice = await PoundInvoiceAsync(kit);

        var s = invoice.Summary;
        Assert.Equal(("GBP", 1.32m, 1_000m, 1_000m, 1_320m), (s.CurrencyCode, s.ExchangeRate, s.TransactionTotal, s.TransactionBalance, s.Total));
        var entry = (await kit.EntriesAsync(JournalSource.Billing)).Single(e => e.SourceId == s.Id);
        AssertBalanced(entry);
        var ar = entry.Lines.Single(l => l.AccountId == kit.Account("1110"));
        Assert.Equal((1_320m, "GBP", 1_000m, 1.32m), (ar.Debit, ar.TransactionCurrency, ar.TransactionAmount, ar.ExchangeRate));
        Assert.All(entry.Lines.Where(l => l.AccountId != kit.Account("1110")), l => Assert.Null(l.TransactionCurrency)); // revenue stays functional
    }

    [Fact]
    public async Task A_receipt_at_another_rate_posts_a_balanced_realized_gain_and_settles_the_invoice()
    {
        await using var kit = await KitAsync();
        var invoice = await PoundInvoiceAsync(kit);
        var payments = kit.Get<IPaymentService>();

        await Assert.ThrowsAsync<ArgumentException>(() => payments.RecordAsync(new RecordPaymentRequest(invoice.Summary.CustomerId, Oct20, 1_320m, CashReceiptMethod.Wire,
            [new PaymentApplicationInput(invoice.Summary.Id, 1_320m)])));

        var receipt = await payments.RecordAsync(new RecordPaymentRequest(invoice.Summary.CustomerId, Oct20, 1_000m, CashReceiptMethod.Wire,
            [new PaymentApplicationInput(invoice.Summary.Id, 1_000m)], Reference: "SWIFT-1", CurrencyCode: "GBP"));

        Assert.Equal(("GBP", 1_000m, 1.35m, 1_350m, 30m), (receipt.CurrencyCode, receipt.TransactionAmount, receipt.ExchangeRate, receipt.Amount, receipt.RealizedGainLoss));
        Assert.Equal((1_320m, 1_000m), (receipt.Applications.Single().Amount, receipt.Applications.Single().TransactionAmount!.Value));
        var entry = (await kit.EntriesAsync(JournalSource.Billing)).Single(e => e.SourceId == receipt.Id);
        AssertBalanced(entry);
        Assert.Equal(1_350m, entry.Lines.Single(l => l.AccountId == kit.Account("1010")).Debit);
        Assert.Equal(1_320m, entry.Lines.Single(l => l.AccountId == kit.Account("1110")).Credit);
        Assert.Equal(30m, entry.Lines.Single(l => l.AccountId == kit.Account("4920")).Credit);

        var paid = await kit.Get<IInvoiceService>().GetAsync(invoice.Summary.Id);
        Assert.Equal((InvoiceState.Paid, 0m, 0m), (paid.Summary.Status, paid.Summary.Balance, paid.Summary.TransactionBalance));
        Assert.Equal(0m, await kit.BalanceAsync("1110"));

        await payments.VoidAsync(receipt.Id, new VoidInvoiceRequest("Bounced"));
        var reopened = await kit.Get<IInvoiceService>().GetAsync(invoice.Summary.Id);
        Assert.Equal((1_320m, 1_000m), (reopened.Summary.Balance, reopened.Summary.TransactionBalance));
        Assert.Equal(0m, await kit.BalanceAsync("4920"));
    }

    // ---- Revaluation ----

    [Fact]
    public async Task Revaluation_previews_posts_balanced_entries_and_reverses_on_the_next_periods_first_day()
    {
        await using var kit = await KitAsync();
        var bill = await kit.PostedBillAsync(EuroBill(kit, 1_000m));     // carried at 1,100; worth 1,150 at month end: loss 50
        var invoice = await PoundInvoiceAsync(kit);                       // carried at 1,320; worth 1,300 at month end: loss 20
        var revaluation = kit.Get<IRevaluationService>();
        Guid october;
        await using (var db = await kit.Factory.CreateAsync())
        {
            october = (await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-10")).Id;
        }

        var preview = await revaluation.PreviewAsync(october);
        Assert.False(preview.IsPosted);
        Assert.True(preview.CanPost);
        Assert.Empty(preview.Warnings);
        Assert.Equal((Oct31, Nov1, -70m), (preview.RateDate, preview.ReverseOn, preview.NetGainLoss));
        var payable = preview.Lines.Single(l => l.Kind == RevaluationKind.Payable);
        Assert.Equal((1_000m, 1_100m, 1.15m, 1_150m, -50m), (payable.TransactionBalance, payable.CarryingAmount, payable.Rate, payable.RevaluedAmount, payable.GainLoss));
        var receivable = preview.Lines.Single(l => l.Kind == RevaluationKind.Receivable);
        Assert.Equal((1_000m, 1_320m, 1_300m, -20m), (receivable.TransactionBalance, receivable.CarryingAmount, receivable.RevaluedAmount, receivable.GainLoss));
        Assert.Empty(await kit.EntriesAsync(JournalSource.Revaluation)); // a preview posts nothing

        var close = await kit.Get<IPeriodCloseService>().GetChecklistAsync(october);
        Assert.False(close.Checks.Single(c => c.Kind == CloseCheckKind.CurrencyRevalued).Passed);

        var posted = await revaluation.PostAsync(october);
        Assert.True(posted.IsPosted);
        Assert.Equal(-70m, posted.NetGainLoss);
        var entry = (await kit.EntriesAsync(JournalSource.Revaluation)).Single();
        AssertBalanced(entry);
        Assert.Equal(Oct31, entry.Date);
        Assert.Equal(50m, entry.Lines.Single(l => l.AccountId == kit.Account("2010")).Credit);
        Assert.Equal(20m, entry.Lines.Single(l => l.AccountId == kit.Account("1110")).Credit);
        Assert.Equal(70m, entry.Lines.Single(l => l.AccountId == kit.Account("4930")).Debit);
        Assert.All(entry.Lines, l => Assert.Null(l.TransactionCurrency));

        var reversal = (await kit.EntriesAsync(JournalSource.Reversal)).Single(e => e.ReversalOfId == entry.Id);
        AssertBalanced(reversal);
        Assert.Equal(Nov1, reversal.Date);
        Assert.Equal(70m, reversal.Lines.Single(l => l.AccountId == kit.Account("4930")).Credit);

        // After the reversal the books carry each document at its own rate again, so payment realizes against that rate.
        Assert.Equal(0m, await kit.BalanceAsync("4930"));
        Assert.Equal(-1_100m, await kit.BalanceAsync("2010"));
        Assert.Equal(1_320m, await kit.BalanceAsync("1110"));

        Assert.True((await kit.Get<IPeriodCloseService>().GetChecklistAsync(october)).Checks.Single(c => c.Kind == CloseCheckKind.CurrencyRevalued).Passed);
        await Assert.ThrowsAsync<InvalidOperationException>(() => revaluation.PostAsync(october));
        Assert.Single(await revaluation.ListAsync());

        var report = await revaluation.GetReportAsync(Oct31, Oct1);
        Assert.Equal(-70m, report.UnrealizedTotal);
        Assert.Equal(2, report.Documents.Count);
        Assert.Equal((1_000m, 1_000m), (report.ByCurrency.Single(c => c.CurrencyCode == "EUR").PayableBalance, report.ByCurrency.Single(c => c.CurrencyCode == "GBP").ReceivableBalance));
    }

    [Fact]
    public async Task Revaluation_needs_the_next_period_and_skips_balances_without_a_rate()
    {
        await using var kit = await KitAsync();
        await kit.Get<ICurrencyService>().SaveCurrencyAsync(new SaveCurrencyRequest("CAD", "Canadian dollar"));
        await kit.Get<ICurrencyService>().SaveRateAsync(new SaveExchangeRateRequest("CAD", "USD", RateKind.Spot, Oct1, 0.73m));
        var bill = await kit.PostedBillAsync(new SaveBillRequest(kit.Prima.Id, "CA-1", PurchasingKit.Day, [kit.OverheadLine(1_000m)], CurrencyCode: "CAD"));
        Guid november;
        await using (var db = await kit.Factory.CreateAsync())
        {
            november = (await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-11")).Id;
        }

        // November has no period after it.
        var preview = await kit.Get<IRevaluationService>().PreviewAsync(november);
        Assert.False(preview.CanPost);
        Assert.Contains(preview.Warnings, w => w.Contains("no fiscal period after"));
        Assert.Contains(preview.Warnings, w => w.Contains("CAD: used the spot rate")); // falls back to spot, flagged
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Get<IRevaluationService>().PostAsync(november));
        Assert.Equal(730m, bill.Bill.Amount);
    }

    // ---- Expenses, settings and permissions ----

    [Fact]
    public async Task An_expense_paid_in_euros_is_kept_in_dollars_with_its_rate()
    {
        await using var kit = await KitAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<ChargeAssignment>().Add(new ChargeAssignment { EmployeeId = kit.Fx.AnalystId, WbsElementId = kit.Fx.Wbs1 });
            await db.SaveChangesAsync();
        }

        kit.As(Editor);
        var expenses = kit.Get<IExpenseReportService>();
        var report = await expenses.CreateReportAsync(new SaveExpenseReportRequest("Berlin trip"));

        var line = await expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(kit.Fx.Wbs1, OdcCostType.Travel, Oct20, 50m, "Taxi", CurrencyCode: "EUR"));
        Assert.Equal(("EUR", 50m, 1.12m, 56m), (line.CurrencyCode, line.TransactionAmount, line.ExchangeRate, line.Amount));

        var receiptRate = await expenses.UpdateLineAsync(report.Id, line.Id, new SaveExpenseLineRequest(kit.Fx.Wbs1, OdcCostType.Travel, Oct20, 50m, "Taxi", CurrencyCode: "EUR", ExchangeRate: 1.1311m));
        Assert.Equal(56.56m, receiptRate.Amount);

        var dollars = await expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(kit.Fx.Wbs1, OdcCostType.Travel, Oct20, 12.5m, "Parking"));
        Assert.Equal((null, null, 12.5m), (dollars.CurrencyCode, dollars.TransactionAmount, dollars.Amount));
    }

    [Fact]
    public async Task The_functional_currency_is_a_tenant_setting_that_locks_once_foreign_documents_exist()
    {
        await using var kit = await KitAsync();
        var fx = kit.Get<ICurrencyService>();
        var settings = await fx.GetSettingsAsync();
        Assert.Equal(("USD", true), (settings.FunctionalCurrency, settings.CanChangeFunctionalCurrency));

        await kit.Bills.CreateAsync(EuroBill(kit, 100m));
        Assert.False((await fx.GetSettingsAsync()).CanChangeFunctionalCurrency);
        await Assert.ThrowsAsync<ArgumentException>(() => fx.SaveSettingsAsync(new SaveCurrencySettingsRequest("EUR")));
        Assert.Equal("USD", (await fx.SaveSettingsAsync(new SaveCurrencySettingsRequest("USD", kit.Account("4910")))).FunctionalCurrency);
    }

    [Fact]
    public async Task Everyone_reads_currencies_but_only_finance_and_admins_change_rates_and_revalue()
    {
        await using var kit = await KitAsync();
        kit.As(Viewer);
        var fx = kit.Get<ICurrencyService>();
        Assert.Contains(await fx.ListCurrenciesAsync(), c => c.Code == "EUR");
        Assert.Equal(1.10m, (await fx.GetRateAsync("EUR", "USD", Oct1)).Rate);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => fx.SaveRateAsync(new SaveExchangeRateRequest("EUR", "USD", RateKind.Spot, Oct1, 2m)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => fx.ImportRatesAsync(new ImportExchangeRatesRequest("From,To,Type,EffectiveDate,Rate\n")));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => fx.GetSettingsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Get<IRevaluationService>().ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => kit.Get<IRevaluationService>().GetReportAsync(Oct31));

        kit.As(Admin);
        Assert.Equal(2m, (await fx.SaveRateAsync(new SaveExchangeRateRequest("EUR", "USD", RateKind.Spot, Oct1, 2m))).Rate);
    }
}

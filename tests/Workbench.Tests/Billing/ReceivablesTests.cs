using Workbench.Application.Billing;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;

namespace Workbench.Tests.Billing;

/// <summary>Payments against one or more invoices, overpayments kept as customer credits, write-offs, aging and reminders.</summary>
public sealed class ReceivablesTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);
    private static readonly DateOnly Oct31 = new(2026, 10, 31);
    private static readonly DateOnly Nov1 = new(2026, 11, 1);
    private static readonly DateOnly Nov30 = new(2026, 11, 30);
    private static readonly DateOnly Today = new(2026, 11, 20);

    /// <summary>A retainer of $4,000 a month with the October and November invoices approved (dated at each month's end).</summary>
    private static async Task<(CommercialKit Kit, Guid Oct, Guid Nov)> TwoInvoicesAsync()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.RetainerAsync();
        var oct = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Oct1, Oct31, new DateOnly(2026, 10, 31)));
        var nov = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Nov1, Nov30, new DateOnly(2026, 11, 20)));
        await kit.Invoices.ApproveAsync(oct.Summary.Id, new ApproveInvoiceRequest());
        await kit.Invoices.ApproveAsync(nov.Summary.Id, new ApproveInvoiceRequest());
        return (kit, oct.Summary.Id, nov.Summary.Id);
    }

    private static RecordPaymentRequest Pay(CommercialKit kit, decimal amount, bool keep = false, params (Guid Invoice, decimal Amount)[] applications) =>
        new(kit.CustomerId, Today, amount, CashReceiptMethod.Check, applications.Select(a => new PaymentApplicationInput(a.Invoice, a.Amount)).ToList(), "CHK-1", KeepRemainderAsCredit: keep);

    [Fact]
    public async Task A_partial_payment_leaves_the_invoice_partially_paid_and_the_rest_settles_it()
    {
        var (kit, oct, _) = await TwoInvoicesAsync();

        await kit.Payments.RecordAsync(Pay(kit, 1_500m, false, (oct, 1_500m)));
        var partial = await kit.Invoices.GetAsync(oct);
        Assert.Equal(InvoiceState.PartiallyPaid, partial.Summary.Status);
        Assert.Equal(2_500m, partial.Summary.Balance);

        await kit.Payments.RecordAsync(Pay(kit, 2_500m, false, (oct, 2_500m)));
        Assert.Equal(InvoiceState.Paid, (await kit.Invoices.GetAsync(oct)).Summary.Status);
        Assert.Equal(4_000m, await kit.Gl.BalanceAsync("1010"));
        Assert.Equal(4_000m, await kit.Gl.BalanceAsync("1110")); // November is still owed
    }

    [Fact]
    public async Task One_payment_can_pay_several_invoices()
    {
        var (kit, oct, nov) = await TwoInvoicesAsync();

        var payment = await kit.Payments.RecordAsync(Pay(kit, 5_000m, false, (oct, 4_000m), (nov, 1_000m)));

        Assert.Equal(2, payment.Applications.Count);
        Assert.Equal(0m, payment.UnappliedAmount);
        Assert.Equal(InvoiceState.Paid, (await kit.Invoices.GetAsync(oct)).Summary.Status);
        Assert.Equal(3_000m, (await kit.Invoices.GetAsync(nov)).Summary.Balance);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordAsync(Pay(kit, 5_000m, false, (oct, 100m)))); // the whole receipt must be applied
    }

    [Fact]
    public async Task An_overpayment_is_kept_as_a_customer_credit_and_applied_to_a_later_invoice_without_a_new_entry()
    {
        var (kit, oct, nov) = await TwoInvoicesAsync();

        var payment = await kit.Payments.RecordAsync(Pay(kit, 5_000m, true, (oct, 4_000m)));
        Assert.Equal(1_000m, payment.UnappliedAmount);
        Assert.Equal(5_000m, await kit.Gl.BalanceAsync("1010"));
        Assert.Equal(3_000m, await kit.Gl.BalanceAsync("1110")); // 8,000 billed less the 5,000 received: the credit sits in AR

        var credit = Assert.Single(await kit.Receivables.ListCreditsAsync());
        Assert.Equal(1_000m, credit.Unapplied);
        var aging = await kit.Receivables.GetAgingAsync();
        Assert.Equal(1_000m, aging.TotalCredits);
        Assert.Equal(aging.Aging.Totals.Total - 1_000m, aging.NetReceivable);
        Assert.Equal(await kit.Gl.BalanceAsync("1110"), aging.NetReceivable); // the aging, net of credits, is the GL's AR

        var journalCount = (await kit.Gl.EntriesAsync()).Count;
        var applied = await kit.Receivables.ApplyCreditAsync(payment.Id, new ApplyCreditRequest([new PaymentApplicationInput(nov, 800m)]));
        Assert.Equal(200m, applied.UnappliedAmount);
        Assert.Equal(3_200m, (await kit.Invoices.GetAsync(nov)).Summary.Balance);
        Assert.Equal(journalCount, (await kit.Gl.EntriesAsync()).Count);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Receivables.ApplyCreditAsync(payment.Id, new ApplyCreditRequest([new PaymentApplicationInput(nov, 500m)])));

        // An overpayment without asking for the credit is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordAsync(Pay(kit, 5_000m, false, (nov, 4_000m))));
        // A prepayment with nothing to apply yet is all credit.
        var prepaid = await kit.Payments.RecordAsync(Pay(kit, 750m, true));
        Assert.Equal(750m, prepaid.UnappliedAmount);
    }

    [Fact]
    public async Task Voiding_a_payment_with_a_credit_restores_the_invoices_and_removes_the_credit()
    {
        var (kit, oct, _) = await TwoInvoicesAsync();
        var payment = await kit.Payments.RecordAsync(Pay(kit, 5_000m, true, (oct, 4_000m)));

        await kit.Payments.VoidAsync(payment.Id, new VoidInvoiceRequest("Bounced"));

        Assert.Equal(4_000m, (await kit.Invoices.GetAsync(oct)).Summary.Balance);
        Assert.Empty(await kit.Receivables.ListCreditsAsync());
        Assert.Equal(8_000m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(0m, await kit.Gl.BalanceAsync("1010"));
    }

    [Fact]
    public async Task A_write_off_needs_a_reason_posts_bad_debt_and_can_be_reversed()
    {
        var (kit, oct, _) = await TwoInvoicesAsync();
        await kit.Payments.RecordAsync(Pay(kit, 1_500m, false, (oct, 1_500m)));

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Receivables.WriteOffAsync(oct, new WriteOffInvoiceRequest(500m, " ")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Receivables.WriteOffAsync(oct, new WriteOffInvoiceRequest(3_000m, "Too much")));

        var writeOff = await kit.Receivables.WriteOffAsync(oct, new WriteOffInvoiceRequest(500m, "Customer dissolved"));
        Assert.Equal(500m, writeOff.Amount);
        Assert.NotNull(writeOff.JournalEntryNumber);
        var invoice = (await kit.Invoices.GetAsync(oct)).Summary;
        Assert.Equal(2_000m, invoice.Balance);
        Assert.Equal(500m, invoice.AmountWrittenOff);
        Assert.Equal(500m, await kit.Gl.BalanceAsync("7040"));
        Assert.Equal(8_000m - 1_500m - 500m, await kit.Gl.BalanceAsync("1110"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.VoidAsync(oct, new VoidInvoiceRequest("Oops"))); // reverse the write-off first
        var statement = await kit.Ar.GetStatementAsync(kit.CustomerId, Oct1, Nov30);
        Assert.Contains(statement.Lines, l => l.Kind == "Write-off" && l.Credits == 500m);
        Assert.Equal(statement.ClosingBalance, await kit.Gl.BalanceAsync("1110"));

        var reversed = await kit.Receivables.ReverseWriteOffAsync(writeOff.Id, new ReverseWriteOffRequest("Customer paid after all"));
        Assert.True(reversed.IsReversed);
        Assert.Equal(2_500m, (await kit.Invoices.GetAsync(oct)).Summary.Balance);
        Assert.Equal(0m, await kit.Gl.BalanceAsync("7040"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Receivables.ReverseWriteOffAsync(writeOff.Id, new ReverseWriteOffRequest("Again")));
        Assert.Single(await kit.Receivables.ListWriteOffsAsync(oct));
    }

    [Fact]
    public async Task Writing_off_the_whole_balance_closes_the_invoice()
    {
        var (kit, oct, _) = await TwoInvoicesAsync();

        await kit.Receivables.WriteOffAsync(oct, new WriteOffInvoiceRequest(4_000m, "Uncollectible"));

        var invoice = (await kit.Invoices.GetAsync(oct)).Summary;
        Assert.Equal(0m, invoice.Balance);
        Assert.Equal(InvoiceState.Paid, invoice.Status);
        Assert.DoesNotContain((await kit.Ar.GetAgingAsync(Today)).Invoices, i => i.Id == oct);
    }

    [Fact]
    public async Task The_aging_puts_each_customers_open_balance_in_current_30_60_90_and_over()
    {
        var (kit, _, _) = await TwoInvoicesAsync(); // October is due Nov 30, November Dec 20
        var customer = (await kit.Ar.GetAgingAsync(new DateOnly(2026, 12, 15))).Customers.Single();
        Assert.Equal("Acme Co", customer.CustomerName);
        Assert.Equal(4_000m, customer.Buckets.Days1To30);
        Assert.Equal(4_000m, customer.Buckets.Current);

        var later = (await kit.Ar.GetAgingAsync(new DateOnly(2027, 3, 15))).Customers.Single();
        Assert.Equal(4_000m, later.Buckets.Over90);        // 105 days past due
        Assert.Equal(4_000m, later.Buckets.Days61To90);    // 85 days past due
        Assert.Equal(8_000m, later.Buckets.Total);
        Assert.Equal(8_000m, (await kit.Ar.GetAgingAsync(new DateOnly(2027, 3, 15))).Totals.Total);
    }

    [Fact]
    public async Task Finance_is_told_before_an_invoice_is_due_and_again_when_it_is_overdue_once_each()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.AddFinanceAsync();
        await kit.RetainerAsync();
        var draft = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Oct1, Oct31, new DateOnly(2026, 10, 23)));
        await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest()); // due Nov 22, two days from today

        Assert.Equal(1, await kit.Receivables.SendPaymentRemindersAsync());
        var sent = Assert.Single(kit.Publisher.Sent);
        Assert.Equal(DueSoonInvoiceCheck.Kind, sent.Kind);
        Assert.Contains("due in 2 days", sent.Title, StringComparison.Ordinal);

        // The recording publisher saves nothing, so record what the real one would: the reminder with that link is already sent.
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Domain.Notifications.Notification>().Add(new Domain.Notifications.Notification
            {
                RecipientEmail = "finance@example.com", Kind = DueSoonInvoiceCheck.Kind, Title = "Sent", Link = sent.Link, CreatedAtUtc = CommercialKit.Now,
            });
            await db.SaveChangesAsync();
        }

        Assert.Equal(0, await kit.Receivables.SendPaymentRemindersAsync());

        kit.Clock.UtcNow = new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(1, await kit.Receivables.SendPaymentRemindersAsync());
        Assert.Equal(OverdueInvoiceCheck.Kind, kit.Publisher.Sent[^1].Kind);
    }

    [Fact]
    public async Task The_reminder_window_is_an_organization_setting_and_zero_turns_it_off()
    {
        var kit = await CommercialKit.CreateAsync();
        await kit.AddFinanceAsync();
        await kit.RetainerAsync();
        var draft = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, InvoiceKind.Retainer, Oct1, Oct31, new DateOnly(2026, 10, 23)));
        await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());

        var settings = await kit.Settings.GetAsync();
        Assert.Equal(3, settings.ReminderDaysBeforeDue);
        await kit.Settings.SaveAsync(new SaveBillingSettingsRequest(null, null, null, null, 30, null, null, 0, InvoiceLayoutKind.Government));

        Assert.Equal(0, await kit.Receivables.SendPaymentRemindersAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Settings.SaveAsync(new SaveBillingSettingsRequest(null, null, null, null, 400, null, null, 3, InvoiceLayoutKind.Government)));
    }
}

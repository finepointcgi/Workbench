using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Identity;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Identity;

namespace Workbench.Tests.Billing;

public sealed class InvoiceLifecycleTests
{
    private static void SetCostPlus(BillingKit kit) => kit.Burdened!.Set(RateBasis.Provisional,
        new BurdenedCostLine(kit.ProjectId, kit.CostWbs.Id, kit.CostWbs.Code, kit.CostClin.Id, 10, 1_000m, 300m, 200m, 150m, 0, 0),
        new BurdenedCostLine(kit.ProjectId, kit.OdcWbs.Id, kit.OdcWbs.Code, kit.OdcClin.Id, 0, 0, 0, 0, 0, 400m, 0));

    [Fact]
    public async Task Past_funded_value_approval_needs_a_finance_override_reason()
    {
        var kit = await BillingKit.CreateAsync();
        await kit.SetFundingAsync(kit.TmClin, funded: 3_000m);
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));

        var check = Assert.Single(draft.Funding);
        Assert.Equal((FundingCheckLevel.OverFunded, 3_630m, -630m), (check.Level, check.CumulativeBilled, check.FundedRemaining));
        Assert.True(draft.NeedsFundingOverride);
        Assert.Contains(draft.Warnings, w => w.Contains("past the funded value", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest()));
        var approved = await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest("Mod P00002 funding in signature"));

        Assert.Equal(InvoiceState.Approved, approved.Summary.Status);
        Assert.Equal("Mod P00002 funding in signature", approved.FundingOverrideReason);
        await using var db = await kit.Factory.CreateAsync();
        Assert.Contains(await db.Set<Domain.ProjectAccounting.AuditEvent>().ToListAsync(), a => a.Action == "Approved" && a.Reason!.Contains("Funding override", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Past_the_ceiling_an_invoice_cant_be_approved_even_with_a_reason()
    {
        var kit = await BillingKit.CreateAsync();
        await kit.SetFundingAsync(kit.TmClin, funded: 3_000m, ceiling: 3_500m);
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));

        Assert.True(draft.IsOverCeiling);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest("Please")));
        Assert.Contains("ceiling", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Funding_counts_what_earlier_invoices_billed()
    {
        var kit = await BillingKit.CreateAsync();
        await kit.SetFundingAsync(kit.TmClin, funded: 4_000m);
        await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        await using (var db = await kit.Factory.CreateAsync())
        {
            var timesheet = await db.Set<Domain.ProjectAccounting.Timesheet>().FirstAsync();
            db.Set<Domain.ProjectAccounting.TimeEntry>().Add(new Domain.ProjectAccounting.TimeEntry { TimesheetId = timesheet.Id, WbsElementId = kit.TmWbs.Id, LaborCategoryId = kit.CategoryId, WorkDate = new DateOnly(2026, 11, 2), Hours = 5 });
            await db.SaveChangesAsync();
        }

        var november = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1)));

        var check = Assert.Single(november.Funding);
        Assert.Equal((3_630m, 600m, FundingCheckLevel.OverFunded), (check.PreviouslyBilled, check.ThisInvoice, check.Level));
    }

    [Fact]
    public async Task Approving_posts_balanced_entries_to_ar_revenue_and_unbilled_for_the_withhold()
    {
        var kit = await BillingKit.CreateAsync();
        SetCostPlus(kit);

        var invoice = await kit.ApprovedAsync(InvoiceKind.CostPlus);

        var entry = Assert.Single(await kit.Gl.EntriesAsync(JournalSource.Billing));
        Assert.Equal(invoice.JournalEntryId, entry.Id);
        Assert.Equal(invoice.Summary.Id, entry.SourceId);
        Assert.Equal(JournalStatus.Posted, entry.Status);
        Assert.Equal(new DateOnly(2026, 11, 5), entry.Date);
        Assert.True(entry.IsBalanced);
        Assert.Equal(2_148.17m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(17.33m, await kit.Gl.BalanceAsync("1120"));
        Assert.Equal(-2_165.50m, await kit.Gl.BalanceAsync("4010")); // gross: cost 2,050 + fee 115.50
        Assert.Contains(entry.Lines, l => l.AccountId == kit.Gl["4010"] && l.WbsElementId == kit.CostWbs.Id && l.ProjectId == kit.ProjectId);
        Assert.All(entry.Lines, l => Assert.True(l.Debit == 0 ^ l.Credit == 0));

        // Approving again is refused and posts nothing more.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.ApproveAsync(invoice.Summary.Id, new ApproveInvoiceRequest()));
        Assert.Single(await kit.Gl.EntriesAsync(JournalSource.Billing));
    }

    [Fact]
    public async Task Time_and_materials_revenue_posts_to_the_t_and_m_account()
    {
        var kit = await BillingKit.CreateAsync();
        await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);

        Assert.Equal(3_630m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(-3_630m, await kit.Gl.BalanceAsync("4020"));
        Assert.Equal(0m, await kit.Gl.BalanceAsync("4010"));
    }

    [Fact]
    public async Task Voiding_an_approved_invoice_reverses_its_entry_and_releases_its_sources()
    {
        var kit = await BillingKit.CreateAsync();
        var milestone = await kit.Milestones.CreateAsync(kit.ContractId, new SaveMilestoneRequest(kit.FfpClin.Id, "Kickoff", 2_000m, BillingKit.October));
        await kit.Milestones.CompleteAsync(milestone.Id, new CompleteMilestoneRequest(BillingKit.October));
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        var ffp = await kit.ApprovedAsync(InvoiceKind.FixedPriceMilestone);
        await kit.Invoices.MarkSentAsync(tm.Summary.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.VoidAsync(tm.Summary.Id, new VoidInvoiceRequest(" ")));
        var voided = await kit.Invoices.VoidAsync(tm.Summary.Id, new VoidInvoiceRequest("Wrong labor category"));
        await kit.Invoices.VoidAsync(ffp.Summary.Id, new VoidInvoiceRequest("Milestone not accepted"));

        Assert.Equal(InvoiceState.Void, voided.Summary.Status);
        Assert.Equal(0m, voided.Summary.Balance);
        var entries = await kit.Gl.EntriesAsync();
        Assert.Equal(JournalStatus.Reversed, entries.Single(e => e.Id == tm.JournalEntryId).Status);
        Assert.Equal(2, entries.Count(e => e.Source == JournalSource.Reversal));
        Assert.Equal(0m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(0m, await kit.Gl.BalanceAsync("4020"));
        Assert.All((await kit.LoadAsync(tm.Summary.Id)).Lines.SelectMany(l => l.Sources), s => Assert.NotNull(s.ReleasedAtUtc));
        Assert.Null((await kit.Milestones.ListAsync(kit.ContractId)).Single().InvoiceId);

        // Everything is billable again.
        var rebill = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        Assert.Equal(3_630m, rebill.Summary.Total);
        Assert.NotEqual(tm.Summary.Number, rebill.Summary.Number);
        Assert.Equal(2_000m, (await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.FixedPriceMilestone))).Total);
    }

    [Fact]
    public async Task A_paid_invoice_cant_be_voided_and_a_draft_can_be_deleted()
    {
        var kit = await BillingKit.CreateAsync();
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        await kit.Payments.RecordAsync(new RecordPaymentRequest(kit.CustomerId, new DateOnly(2026, 11, 15), 100m, CashReceiptMethod.Check, [new PaymentApplicationInput(tm.Summary.Id, 100m)]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Invoices.VoidAsync(tm.Summary.Id, new VoidInvoiceRequest("Oops")));

        SetCostPlus(kit);
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.CostPlus));
        await kit.Invoices.DeleteDraftAsync(draft.Summary.Id);
        await using var db = await kit.Factory.CreateAsync();
        Assert.False(await db.Set<Invoice>().AnyAsync(i => i.Id == draft.Summary.Id));
    }

    [Fact]
    public async Task A_credit_memo_reduces_the_invoice_balance_and_reverses_revenue()
    {
        var kit = await BillingKit.CreateAsync();
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.CreateCreditMemoAsync(tm.Summary.Id, new CreditMemoRequest("Too much", [new CreditMemoLine(kit.TmClin.Id, "Credit", 5_000m)])));
        var memo = await kit.Invoices.CreateCreditMemoAsync(tm.Summary.Id, new CreditMemoRequest("Rate correction", [new CreditMemoLine(kit.TmClin.Id, "Rate correction, 4 h", 80m)]));
        Assert.Equal((InvoiceKind.CreditMemo, InvoiceState.Draft, -80m), (memo.Summary.Type, memo.Summary.Status, memo.Summary.Total));
        await kit.Invoices.ApproveAsync(memo.Summary.Id, new ApproveInvoiceRequest());

        var original = await kit.Invoices.GetAsync(tm.Summary.Id);
        Assert.Equal((80m, 3_550m), (original.Summary.AmountCredited, original.Summary.Balance));
        Assert.Equal(3_550m, await kit.Gl.BalanceAsync("1110"));
        Assert.Equal(-3_550m, await kit.Gl.BalanceAsync("4020"));
        Assert.Equal(3_550m, (await kit.Invoices.GetContractSummaryAsync(kit.ContractId)).BilledToDate);
    }

    [Fact]
    public async Task Payments_apply_across_invoices_and_post_cash_against_ar()
    {
        var kit = await BillingKit.CreateAsync();
        SetCostPlus(kit);
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        var cp = await kit.ApprovedAsync(InvoiceKind.CostPlus);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordAsync(new RecordPaymentRequest(kit.CustomerId, new DateOnly(2026, 11, 18), 5_000m, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(tm.Summary.Id, 4_000m), new PaymentApplicationInput(cp.Summary.Id, 1_000m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Payments.RecordAsync(new RecordPaymentRequest(kit.CustomerId, new DateOnly(2026, 11, 18), 1_000m, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(tm.Summary.Id, 900m)])));

        var payment = await kit.Payments.RecordAsync(new RecordPaymentRequest(kit.CustomerId, new DateOnly(2026, 11, 18), 4_630m, CashReceiptMethod.Eft,
            [new PaymentApplicationInput(tm.Summary.Id, 3_630m), new PaymentApplicationInput(cp.Summary.Id, 1_000m)], Reference: "EFT-1"));

        Assert.Equal(2, payment.Applications.Count);
        Assert.Equal(InvoiceState.Paid, (await kit.Invoices.GetAsync(tm.Summary.Id)).Summary.Status);
        var partial = await kit.Invoices.GetAsync(cp.Summary.Id);
        Assert.Equal((InvoiceState.PartiallyPaid, 1_148.17m), (partial.Summary.Status, partial.Summary.Balance));
        Assert.Equal(4_630m, await kit.Gl.BalanceAsync("1010"));
        Assert.Equal(1_148.17m, await kit.Gl.BalanceAsync("1110"));
        Assert.Single(await kit.Payments.ListOpenInvoicesAsync(kit.CustomerId));

        await kit.Payments.VoidAsync(payment.Id, new VoidInvoiceRequest("Bounced"));
        Assert.Equal(InvoiceState.Approved, (await kit.Invoices.GetAsync(tm.Summary.Id)).Summary.Status);
        Assert.Equal(0m, await kit.Gl.BalanceAsync("1010"));
        Assert.Equal(5_778.17m, await kit.Gl.BalanceAsync("1110"));
    }

    [Theory]
    [InlineData(-5, "Current")]
    [InlineData(0, "Current")]
    [InlineData(1, "1-30")]
    [InlineData(30, "1-30")]
    [InlineData(31, "31-60")]
    [InlineData(60, "31-60")]
    [InlineData(61, "61-90")]
    [InlineData(90, "61-90")]
    [InlineData(91, "90+")]
    public void Aging_buckets_by_days_past_due(int days, string bucket)
    {
        var b = ArReportService.Bucket(100m, days);
        var actual = b.Current != 0 ? "Current" : b.Days1To30 != 0 ? "1-30" : b.Days31To60 != 0 ? "31-60" : b.Days61To90 != 0 ? "61-90" : "90+";
        Assert.Equal(bucket, actual);
        Assert.Equal(100m, b.Total);
    }

    [Fact]
    public async Task Aging_and_the_statement_show_open_balances_by_customer_and_contract()
    {
        var kit = await BillingKit.CreateAsync();
        SetCostPlus(kit);
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials); // due 2026-12-05
        await kit.ApprovedAsync(InvoiceKind.CostPlus);
        await kit.Payments.RecordAsync(new RecordPaymentRequest(kit.CustomerId, new DateOnly(2026, 11, 18), 630m, CashReceiptMethod.Check, [new PaymentApplicationInput(tm.Summary.Id, 630m)]));

        var now = await kit.Ar.GetAgingAsync(new DateOnly(2026, 11, 20));
        Assert.Equal(5_148.17m, now.Totals.Current);
        var later = await kit.Ar.GetAgingAsync(new DateOnly(2027, 1, 20)); // 46 days past due
        Assert.Equal(5_148.17m, later.Totals.Days31To60);
        Assert.Equal(0m, later.Totals.Current);
        var row = Assert.Single(later.Contracts);
        Assert.Equal(("W1", 2), (row.ContractNumber, row.InvoiceCount));
        Assert.Equal("Army", Assert.Single(later.Customers).CustomerName);

        var statement = await kit.Ar.GetStatementAsync(kit.CustomerId, new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30));
        Assert.Equal(0m, statement.OpeningBalance);
        Assert.Equal(3, statement.Lines.Count);
        Assert.Equal(5_148.17m, statement.ClosingBalance);
        Assert.Equal(630m, statement.Lines.Single(l => l.Kind == "Payment").Credits);
    }

    [Fact]
    public async Task Overdue_invoices_notify_finance_once_per_aging_bucket()
    {
        var kit = await BillingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = "finance@example.com", DisplayName = "Fin" }, OrgRoles.Finance);
            await RoleStore.AddUserAsync(db, new AppUser { Email = "admin@example.com", DisplayName = "Admin" }, OrgRoles.Admin);
            await RoleStore.AddUserAsync(db, new AppUser { Email = "viewer@example.com", DisplayName = "Val" }, OrgRoles.Employee);
            await db.SaveChangesAsync();
        }

        await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        Assert.Equal(0, await kit.Ar.SendOverdueRemindersAsync());

        kit.Clock.UtcNow = new DateTime(2026, 12, 10, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(2, await kit.Ar.SendOverdueRemindersAsync());
        Assert.All(kit.Publisher.Sent, s => Assert.Equal(OverdueInvoiceCheck.Kind, s.Kind));
        Assert.DoesNotContain(kit.Publisher.Sent, s => s.Email == "viewer@example.com");
        Assert.Contains("5 days past due", kit.Publisher.Sent[0].Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_unbilled_accrual_posts_once_with_a_reversal_next_period_and_shows_on_the_close_checklist()
    {
        var kit = await BillingKit.CreateAsync();
        SetCostPlus(kit);
        Guid october;
        await using (var db = await kit.Factory.CreateAsync())
        {
            october = (await db.Set<Domain.ProjectAccounting.FiscalPeriod>().FirstAsync(p => p.Start == BillingKit.October)).Id;
        }

        var preview = await kit.Invoices.GetUnbilledAccrualAsync(october);
        Assert.False(preview.IsPosted);
        Assert.Equal(3_630m + 2_165.50m, preview.Amount); // T&M plus cost-plus with full fee (withhold excluded)

        var posted = await kit.Invoices.PostUnbilledAccrualAsync(october);
        var again = await kit.Invoices.PostUnbilledAccrualAsync(october);

        Assert.True(posted.IsPosted);
        Assert.Equal(posted.JournalEntryId, again.JournalEntryId);
        var entries = await kit.Gl.EntriesAsync();
        var accrual = entries.Single(e => e.Id == posted.JournalEntryId);
        var reversal = entries.Single(e => e.Id == posted.ReversalEntryId);
        Assert.Equal((BillingKit.OctoberEnd, new DateOnly(2026, 11, 1)), (accrual.Date, reversal.Date));
        Assert.True(accrual.IsBalanced && reversal.IsBalanced);
        Assert.Equal(0m, await kit.Gl.BalanceAsync("1120"));

        var close = new PeriodCloseService(kit.Factory, kit.Gl.Engine, new AuditRecorder(kit.User, kit.Clock), [kit.CloseCheck]);
        var checklist = await close.GetChecklistAsync(october);
        var item = Assert.Single(checklist.Checks, c => c.Kind == CloseCheckKind.UnbilledAccrual);
        Assert.True(item.Passed);
        Assert.Contains("posted", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Print_data_and_the_wawf_export_list_the_hours_and_odcs()
    {
        var kit = await BillingKit.CreateAsync();
        var tm = await kit.ApprovedAsync(InvoiceKind.TimeAndMaterials);

        var print = await kit.Invoices.GetPrintAsync(tm.Summary.Id);
        var labor = print.Labor.OrderBy(l => l.Rate).ToList();
        Assert.Equal(("Dana Lee", 16m, 1_600m), (labor[0].EmployeeName, labor[0].Hours, labor[0].Amount));
        Assert.Equal(2, print.Odcs.Count);
        Assert.Equal(3_630m, print.BilledToDate);
        Assert.Equal("Test Co", print.Payee.Name);

        var csv = await kit.Invoices.ExportWawfCsvAsync(tm.Summary.Id);
        var rows = csv.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("RecordType,InvoiceNumber", rows[0], StringComparison.Ordinal);
        Assert.StartsWith("H,W1-0001,2026-11-05", rows[1], StringComparison.Ordinal);
        Assert.Equal(tm.Lines.Count, rows.Count(r => r.StartsWith("L,", StringComparison.Ordinal)));
        Assert.Contains(rows, r => r.Contains(",16.00,HR,100.00,1600.00,", StringComparison.Ordinal));
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Billing;

/// <summary>The Ready to bill list over the billing kit: Army contract W1 with approved hours and ODCs on its T&amp;M, cost and travel CLINs.</summary>
public sealed class ReadyToBillTests
{
    private static async Task SetAddressAsync(BillingKit kit)
    {
        await using var db = await kit.Factory.CreateAsync();
        var profile = await db.Set<CustomerBillingProfile>().FirstAsync(p => p.CustomerId == kit.CustomerId);
        profile.BillingAddress = "Attn: Contracting Officer\nFort Belvoir, VA";
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_amount_is_what_the_draft_holds_and_nothing_blocks_it()
    {
        var kit = await BillingKit.CreateAsync();
        await SetAddressAsync(kit);

        var ready = await kit.Invoices.GetReadyToBillAsync();

        var row = Assert.Single(ready.Rows);
        Assert.Equal("W1", row.ContractNumber);
        Assert.Empty(row.Blockers);
        Assert.Equal(new DateOnly(2026, 10, 12), row.OldestWorkDate);
        Assert.Equal(row.Amount, ready.Total);

        decimal drafted = 0;
        foreach (var d in row.Drafts)
        {
            drafted += (await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(row.ContractId, d.Type, d.PeriodFrom, d.PeriodTo))).Summary.Total;
        }

        Assert.Equal(drafted, row.Amount);
    }

    [Fact]
    public async Task A_missing_address_a_draft_and_missing_rates_are_said_in_plain_words()
    {
        var kit = await BillingKit.CreateAsync();

        var noAddress = Assert.Single((await kit.Invoices.GetReadyToBillAsync()).Rows);
        Assert.Contains(noAddress.Blockers, b => b.Text.Contains("no billing address", StringComparison.Ordinal) && b.Link == $"billing/contracts/{kit.ContractId}");

        await SetAddressAsync(kit);
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        Assert.Empty((await kit.Invoices.GetReadyToBillAsync()).Rows); // everything is already on the draft
        await using (var db = await kit.Factory.CreateAsync())
        {
            var timesheet = await db.Set<Timesheet>().FirstAsync();
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = timesheet.Id, WbsElementId = kit.TmWbs.Id, LaborCategoryId = kit.CategoryId, WorkDate = new DateOnly(2026, 11, 2), Hours = 5 });
            await db.SaveChangesAsync();
        }

        var withDraft = Assert.Single((await kit.Invoices.GetReadyToBillAsync()).Rows);
        var blocker = Assert.Single(withDraft.Blockers);
        Assert.Contains(draft.Summary.Number, blocker.Text, StringComparison.Ordinal);
        Assert.Equal($"billing/invoices/{draft.Summary.Id}", blocker.Link);

        await kit.Invoices.DeleteDraftAsync(draft.Summary.Id);
        await using (var db = await kit.Factory.CreateAsync())
        {
            ((DbContext)db).RemoveRange(await db.Set<ContractLaborRate>().ToListAsync());
            await db.SaveChangesAsync();
        }

        var noRates = Assert.Single((await kit.Invoices.GetReadyToBillAsync()).Rows);
        Assert.Contains(noRates.Blockers, b => b.Text.StartsWith("No bill rate for SE2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Billing_past_funding_blocks_the_draft_and_billed_work_leaves_the_list()
    {
        var kit = await BillingKit.CreateAsync();
        await SetAddressAsync(kit);
        await kit.SetFundingAsync(kit.TmClin, funded: 1_000m);

        var row = Assert.Single((await kit.Invoices.GetReadyToBillAsync()).Rows);
        Assert.Contains(row.Blockers, b => b.Text.Contains("past the funded value", StringComparison.Ordinal));

        await kit.SetFundingAsync(kit.TmClin, funded: 50_000m);
        foreach (var d in Assert.Single((await kit.Invoices.GetReadyToBillAsync()).Rows).Drafts)
        {
            var draft = await kit.Invoices.GenerateDraftAsync(new GenerateInvoiceRequest(kit.ContractId, d.Type, d.PeriodFrom, d.PeriodTo));
            await kit.Invoices.ApproveAsync(draft.Summary.Id, new ApproveInvoiceRequest());
        }

        Assert.DoesNotContain((await kit.Invoices.GetReadyToBillAsync()).Rows, r => r.Drafts.Any(d => d.Type == InvoiceKind.TimeAndMaterials && d.Amount != 0));
    }
}

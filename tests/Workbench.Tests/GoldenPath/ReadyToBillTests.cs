using Workbench.ApiClient;
using Workbench.Tests.Identity;
using Workbench.Contracts.Billing;

namespace Workbench.Tests.GoldenPath;

/// <summary>"Where do I start invoicing?" on the real host and demo seed: the list's number is what the draft invoice then holds.</summary>
public sealed class ReadyToBillTests
{
    [Fact]
    public async Task The_amount_on_the_list_is_exactly_what_the_draft_invoice_contains()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        var invoices = new HttpInvoiceService(finance);

        var ready = await invoices.GetReadyToBillAsync();

        Assert.NotEmpty(ready.Rows);
        Assert.Equal(ready.Rows.Sum(r => r.Amount), ready.Total);
        Assert.Equal(ready.Rows.OrderBy(r => r.OldestWorkDate ?? DateOnly.MaxValue).Select(r => r.ContractNumber), ready.Rows.Select(r => r.ContractNumber));
        var row = ready.Rows.FirstOrDefault(r => r.Blockers.Count == 0 && r.Amount > 0);
        Assert.NotNull(row);

        decimal drafted = 0;
        foreach (var d in row.Drafts)
        {
            var draft = await invoices.GenerateDraftAsync(new GenerateInvoiceRequest(row.ContractId, d.Type, d.PeriodFrom, d.PeriodTo));
            Assert.Equal(d.Amount, draft.Summary.Total);
            drafted += draft.Summary.Total;
        }

        Assert.Equal(row.Amount, drafted);

        // With the draft made, the contract says so, and what's on the draft is no longer counted as waiting.
        var after = (await invoices.GetReadyToBillAsync()).Rows.SingleOrDefault(r => r.ContractId == row.ContractId);
        Assert.True(after is null || after.Blockers.Any(b => b.Text.Contains("still a draft", StringComparison.Ordinal)));
        Assert.True(after is null || after.Amount < row.Amount);
    }

    [Fact]
    public async Task Only_finance_sees_the_list()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var employee = web.CreateClientAs("analyst@example.com");

        await Assert.ThrowsAnyAsync<Exception>(() => new HttpInvoiceService(employee).GetReadyToBillAsync());
    }
}

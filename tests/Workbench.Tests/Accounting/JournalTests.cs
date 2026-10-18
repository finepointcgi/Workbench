using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Odcs;

namespace Workbench.Tests.Accounting;

public sealed class JournalTests
{
    private static readonly DateOnly Day = OdcTestHost.Day;

    private static async Task<(OdcTestHost Host, LedgerKit Gl)> CreateAsync()
    {
        var host = await OdcTestHost.CreateAsync();
        host.As("finance@example.com");
        return (host, await LedgerKit.CreateAsync(host.Factory, host.User, TimeProvider.System));
    }

    [Fact]
    public async Task Default_chart_has_govcon_accounts_and_mapped_posting_rules()
    {
        var (_, gl) = await CreateAsync();
        var accounts = await gl.Chart.ListAccountsAsync();
        Assert.Contains(accounts, a => a.Number == "1120" && a.Type == AccountKind.Asset);
        Assert.Contains(accounts, a => a.Number == "2100" && a.NormalBalance == BalanceSide.Credit);
        Assert.Contains(accounts, a => a.Number == "7010" && a.CostClass == CostClass.Unallowable);
        Assert.True(accounts.Single(a => a.Number == "5000").HasChildren);

        var setup = await gl.Posting.GetSetupAsync();
        Assert.All(setup.Rules.Where(r => !r.Label.Contains("optional")), r => Assert.NotNull(r.AccountId));
        Assert.Equal(0, await gl.Chart.InstallDefaultChartAsync()); // only into an empty chart
    }

    [Fact]
    public async Task Unbalanced_entry_cannot_be_posted()
    {
        var (_, gl) = await CreateAsync();
        var draft = await gl.Journal.CreateDraftAsync(gl.Entry(Day, ("1010", 100m, 0), ("3100", 0, 99.99m)));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.PostAsync(draft.Entry.Id));
        Assert.Contains("doesn't balance", ex.Message);

        await gl.Journal.UpdateDraftAsync(draft.Entry.Id, gl.Entry(Day, ("1010", 100m, 0), ("3100", 0, 100m)));
        var posted = await gl.Journal.PostAsync(draft.Entry.Id);
        Assert.Equal(JournalEntryState.Posted, posted.Entry.Status);
        Assert.Equal("JE-000001", posted.Entry.Number);
        Assert.Equal(100m, await gl.BalanceAsync("1010"));
    }

    [Theory]
    [InlineData(10.005, 10.005)] // fractions of a cent
    [InlineData(-5, -5)]
    public async Task Lines_must_be_whole_positive_cents(decimal debit, decimal credit)
    {
        var (_, gl) = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1010", debit, 0), ("3100", 0, credit))));
    }

    [Fact]
    public async Task Posting_into_a_closed_period_is_refused()
    {
        var (_, gl) = await CreateAsync();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(gl.Entry(OdcTestHost.ClosedDay, ("1010", 50m, 0), ("3100", 0, 50m))));
        Assert.Contains("closed", ex.Message);
    }

    [Fact]
    public async Task Summary_and_inactive_accounts_take_no_postings()
    {
        var (_, gl) = await CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1000", 5m, 0), ("3100", 0, 5m))));

        await gl.Chart.SetActiveAsync(gl["1300"], false);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1300", 5m, 0), ("3100", 0, 5m))));
        Assert.Contains("inactive", ex.Message);

        // A mapped account can't be deactivated out from under the posting rules.
        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Chart.SetActiveAsync(gl["2100"], false));
    }

    [Fact]
    public async Task Wbs_segment_fills_in_its_project_and_must_match_it()
    {
        var (host, gl) = await CreateAsync();
        var lines = new List<JournalLineInput> { new(gl["5190"], 75m, 0, WbsElementId: host.Leaf.Id), new(gl["2200"], 0, 75m) };
        var posted = await gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(Day, "Accrual", lines));
        Assert.Equal(host.Project.Id, posted.Lines[0].ProjectId);
        Assert.Equal("1001.01.01", posted.Lines[0].WbsCode);

        var wrong = new List<JournalLineInput> { new(gl["5190"], 75m, 0, ProjectId: host.OnHoldProject.Id, WbsElementId: host.Leaf.Id), new(gl["2200"], 0, 75m) };
        await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(Day, "Accrual", wrong)));
    }

    [Fact]
    public async Task Posted_entries_are_immutable()
    {
        var (_, gl) = await CreateAsync();
        var posted = await gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1010", 100m, 0), ("3100", 0, 100m)));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Journal.UpdateDraftAsync(posted.Entry.Id, gl.Entry(Day, ("1010", 1m, 0), ("3100", 0, 1m))));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Journal.DeleteDraftAsync(posted.Entry.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Journal.PostAsync(posted.Entry.Id));
        Assert.Equal(100m, (await gl.Journal.GetAsync(posted.Entry.Id)).Entry.TotalDebit);
    }

    [Fact]
    public async Task Reversal_swaps_the_lines_links_both_entries_and_is_audited()
    {
        var (host, gl) = await CreateAsync();
        var original = await gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1010", 250m, 0), ("3100", 0, 250m)));

        var reversal = await gl.Journal.ReverseAsync(original.Entry.Id, new ReverseJournalEntryRequest("Posted to the wrong account"));
        Assert.Equal(JournalEntrySource.Reversal, reversal.Entry.Source);
        Assert.Equal(original.Entry.Id, reversal.Entry.ReversalOfId);
        Assert.Equal(250m, reversal.Lines.Single(l => l.AccountNumber == "3100").Debit);
        Assert.Equal(250m, reversal.Lines.Single(l => l.AccountNumber == "1010").Credit);

        var after = await gl.Journal.GetAsync(original.Entry.Id);
        Assert.Equal(JournalEntryState.Reversed, after.Entry.Status);
        Assert.Equal(reversal.Entry.Id, after.Entry.ReversedById);
        Assert.Equal(0m, await gl.BalanceAsync("1010"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Journal.ReverseAsync(original.Entry.Id, new ReverseJournalEntryRequest("again")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => gl.Journal.ReverseAsync(reversal.Entry.Id, new ReverseJournalEntryRequest("undo")));

        var audit = await host.Audit.ListAsync("JournalEntry");
        Assert.Contains(audit, a => a.EntityId == original.Entry.Id && a.Action == "Posted");
        Assert.Contains(audit, a => a.EntityId == original.Entry.Id && a.Action == "Reversed" && a.Reason == "Posted to the wrong account");
        Assert.Contains(audit, a => a.EntityId == reversal.Entry.Id && a.Action == "Posted");
    }

    [Fact]
    public async Task Reversal_of_a_closed_period_entry_goes_into_an_open_period()
    {
        var (host, gl) = await CreateAsync();
        var posted = await gl.Journal.CreateAndPostAsync(gl.Entry(Day, ("1010", 40m, 0), ("3100", 0, 40m)));
        await using (var db = await host.Factory.CreateAsync())
        {
            (await db.Set<FiscalPeriod>().SingleAsync(p => p.Name == "2026-10")).Status = PeriodStatus.Closed;
            await db.SaveChangesAsync();
        }

        await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.ReverseAsync(posted.Entry.Id, new ReverseJournalEntryRequest("late")));
    }

    [Fact]
    public async Task Other_modules_post_idempotently_by_source_id()
    {
        var (_, gl) = await CreateAsync();
        var invoiceId = Guid.NewGuid();
        var request = gl.Entry(Day, ("1110", 900m, 0), ("4010", 0, 900m)) with { Source = JournalEntrySource.Billing, SourceId = invoiceId };

        var first = await gl.Journal.CreateAndPostAsync(request);
        var retry = await gl.Journal.CreateAndPostAsync(request);
        Assert.Equal(first.Entry.Id, retry.Entry.Id);
        Assert.Single(await gl.EntriesAsync(Workbench.Domain.Accounting.JournalSource.Billing));

        await Assert.ThrowsAsync<ArgumentException>(() => gl.Journal.CreateAndPostAsync(request with { Source = JournalEntrySource.Timesheet }));
    }
}

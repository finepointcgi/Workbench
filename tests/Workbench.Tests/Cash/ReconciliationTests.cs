using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;
using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Cash;

public sealed class ReconciliationTests
{
    private static readonly DateOnly End = new(2026, 10, 31);

    /// <summary>
    /// GL: receipt 12,500 and check 1001 (both on the statement), check 1002 for 700 (outstanding) and a 1,000 deposit
    /// made on the 31st (in transit). Bank: the receipt, check 1001 and a 25.00 fee; ending balance 10,075.00.
    /// </summary>
    private static async Task<(CashKit Kit, JournalEntryDto Outstanding, JournalEntryDto InTransit)> ScenarioAsync()
    {
        var kit = await CashKit.CreateAsync();
        await kit.ReceiptAsync(new DateOnly(2026, 10, 2), 12_500m, "EFT 20261002-ARMY");
        await kit.VendorPaymentAsync(new DateOnly(2026, 10, 4), 2_400m, "1001");
        var outstanding = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 28), 700m, "1002");
        var inTransit = await kit.ReceiptAsync(End, 1_000m, "CHK 8812");
        await kit.ImportAsync(10_075m,
            ("2026-10-02", "DFAS EFT PMT 20261002-ARMY", 12_500m, null, "B1"),
            ("2026-10-06", "CHECK 1001", -2_400m, "1001", "B2"),
            ("2026-10-31", "MONTHLY SERVICE FEE", -25m, null, "B3"));
        return (kit, outstanding, inTransit);
    }

    [Fact]
    public async Task The_math_adjusts_for_outstanding_items_and_unrecorded_bank_items()
    {
        var (kit, outstanding, inTransit) = await ScenarioAsync();
        await using var _ = kit;

        // The defaults come from the imported statement.
        var defaults = await kit.Reconciliations.GetDefaultsAsync(kit.Bank.Id);
        Assert.Equal((End, 10_075m, (DateOnly?)null), (defaults.StatementEndDate!.Value, defaults.StatementEndingBalance!.Value, defaults.ReconciledThrough));

        var rec = await kit.Reconciliations.StartAsync(new StartReconciliationRequest(kit.Bank.Id, End));
        Assert.Equal(10_075m, rec.StatementEndingBalance);
        await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id, End));

        var m = (await kit.Reconciliations.GetWorkspaceAsync(rec.Id)).Reconciliation.Math;
        Assert.Equal(10_400m, m.GlBalance);                  // 12,500 − 2,400 − 700 + 1,000
        Assert.Equal(1_000m, m.DepositsInTransit);
        Assert.Equal(700m, m.OutstandingPayments);
        Assert.Equal(10_375m, m.AdjustedBankBalance);        // 10,075 + 1,000 − 700
        Assert.Equal(-25m, m.UnrecordedBankItems);
        Assert.Equal(10_375m, m.AdjustedBookBalance);        // 10,400 − 25
        Assert.True(m.IsBalanced);
        Assert.Equal(new[] { kit.CashLine(outstanding), kit.CashLine(inTransit) }.Order(), m.OutstandingItems.Select(o => o.JournalLineId).Order());
        Assert.Equal("MONTHLY SERVICE FEE", Assert.Single(m.UnrecordedItems).Description);

        // Balanced, but the fee isn't in the GL: completing is refused until it is booked.
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Reconciliations.CompleteAsync(rec.Id, new CompleteReconciliationRequest()));
        Assert.Contains("aren't in the GL", refused.Message);

        // A wrong statement balance shows as a difference and also blocks.
        var wrong = await kit.Reconciliations.UpdateAsync(rec.Id, new UpdateReconciliationRequest(10_000m));
        Assert.Equal(-75m, wrong.Math.Difference);
        await kit.Reconciliations.UpdateAsync(rec.Id, new UpdateReconciliationRequest(10_075m));
    }

    [Fact]
    public async Task Booking_the_bank_fee_posts_a_bank_journal_entry_and_matches_it()
    {
        var (kit, _, _) = await ScenarioAsync();
        await using var __ = kit;
        var fee = (await kit.ItemsAsync()).Single(i => i.Amount == -25m);
        var accounts = await kit.Reconciliations.GetBookingAccountsAsync();
        Assert.Equal(kit.Account("6240"), accounts.BankFeeAccountId);
        Assert.Equal(kit.Account("4910"), accounts.InterestIncomeAccountId);

        var match = await kit.Reconciliations.BookBankItemAsync(new BookBankItemRequest(fee.Id, kit.Account("6240")));
        Assert.Equal((MatchRule.BookedFromBank, 100, -25m, -25m), (match.Rule, match.Confidence, match.BankTotal, match.GlTotal));

        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Bank));
        Assert.Equal((fee.Id, JournalStatus.Posted, End), (entry.SourceId!.Value, entry.Status, entry.Date));
        Assert.Equal("Bank fee: MONTHLY SERVICE FEE", entry.Description);
        Assert.Equal(25m, entry.Lines.Single(l => l.AccountId == kit.Account("6240")).Debit);
        Assert.Equal(25m, entry.Lines.Single(l => l.AccountId == kit.Account("1010")).Credit);
        var item = (await kit.ItemsAsync()).Single(i => i.Id == fee.Id);
        Assert.Equal((match.Id, entry.Id), (item.MatchId!.Value, item.JournalEntryId!.Value));

        // Booking again is refused (already matched); an interest item books the other way.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Reconciliations.BookBankItemAsync(new BookBankItemRequest(fee.Id, kit.Account("6240"))));
        await kit.ImportAsync(("2026-10-31", "INTEREST PAYMENT", 3.12m, null, "B9"));
        var interest = (await kit.ItemsAsync()).Single(i => i.Amount == 3.12m);
        await kit.Reconciliations.BookBankItemAsync(new BookBankItemRequest(interest.Id, kit.Account("4910")));
        var interestEntry = (await kit.EntriesAsync(JournalSource.Bank)).Single(e => e.SourceId == interest.Id);
        Assert.Equal(3.12m, interestEntry.Lines.Single(l => l.AccountId == kit.Account("1010")).Debit);
        Assert.Equal(3.12m, interestEntry.Lines.Single(l => l.AccountId == kit.Account("4910")).Credit);
    }

    [Fact]
    public async Task Completing_locks_the_matched_items_until_the_reconciliation_is_reopened()
    {
        var (kit, outstanding, _) = await ScenarioAsync();
        await using var _ = kit;
        var rec = await kit.Reconciliations.StartAsync(new StartReconciliationRequest(kit.Bank.Id, End));
        await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id, End));
        var fee = (await kit.ItemsAsync()).Single(i => i.Amount == -25m);
        await kit.Reconciliations.BookBankItemAsync(new BookBankItemRequest(fee.Id, kit.Account("6240")));

        var done = await kit.Reconciliations.CompleteAsync(rec.Id, new CompleteReconciliationRequest("October close"));
        Assert.Equal(ReconciliationState.Completed, done.Status);
        Assert.Equal((10_375m, 1_000m, 700m, 0m), (done.Math.GlBalance, done.Math.DepositsInTransit, done.Math.OutstandingPayments, done.Math.Difference));

        // Every cleared match is locked; the outstanding check isn't matched, so nothing locks it.
        var items = await kit.ItemsAsync();
        Assert.All(items, i => Assert.True(i.IsLocked));
        var ws = await kit.Reconciliations.GetWorkspaceAsync(rec.Id);
        Assert.Empty(ws.Matches);
        Assert.Contains(ws.GlItems, g => g.JournalLineId == kit.CashLine(outstanding) && !g.IsLocked);
        var anyMatch = items[0].MatchId!.Value;
        var locked = await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Reconciliations.UnmatchAsync(anyMatch));
        Assert.Contains("locked", locked.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Reconciliations.UpdateAsync(rec.Id, new UpdateReconciliationRequest(1m)));

        // The next reconciliation must be later; the account shows reconciled through the date.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Reconciliations.StartAsync(new StartReconciliationRequest(kit.Bank.Id, End.AddDays(-1))));
        Assert.Equal(End, (await kit.BankAccounts.GetAsync(kit.Bank.Id)).ReconciledThrough);

        // Reopening needs a reason, unlocks the matches and is audited.
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Reconciliations.ReopenAsync(rec.Id, new ReconciliationReasonRequest(" ")));
        var reopened = await kit.Reconciliations.ReopenAsync(rec.Id, new ReconciliationReasonRequest("Bank corrected the fee"));
        Assert.Equal((ReconciliationState.InProgress, "Bank corrected the fee"), (reopened.Status, reopened.ReopenReason));
        Assert.All(await kit.ItemsAsync(), i => Assert.False(i.IsLocked));
        await kit.Reconciliations.UnmatchAsync(anyMatch);

        await using var db = await kit.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().Where(a => a.EntityType == nameof(BankReconciliation) && a.EntityId == rec.Id).Select(a => a.Action).ToListAsync();
        Assert.Contains("Completed", audit);
        Assert.Contains("Reopened", audit);
    }

    [Fact]
    public async Task The_period_close_check_blocks_until_reconciled_or_waived()
    {
        var (kit, _, _) = await ScenarioAsync();
        await using var _ = kit;
        await using var db = await kit.Factory.CreateAsync();
        var period = await db.Set<FiscalPeriod>().AsNoTracking().SingleAsync();
        var check = new Workbench.Application.Cash.BankReconciliationCloseCheck();

        var before = await check.CheckAsync(db, period, CancellationToken.None);
        Assert.Equal((CloseCheckKind.BankReconciled, false, 1), (before.Kind, before.Passed, before.Count));
        Assert.Contains("never reconciled", Assert.Single(before.Details));

        // It's on the period-close checklist (registered by AddWorkbenchCash) and blocks the close.
        var checklist = await kit.Get<IPeriodCloseService>().GetChecklistAsync(period.Id);
        Assert.False(checklist.Checks.Single(c => c.Kind == CloseCheckKind.BankReconciled).Passed);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Reconciliations.WaiveCloseCheckAsync(new WaiveBankCloseCheckRequest(period.Id, "")));
        await kit.Reconciliations.WaiveCloseCheckAsync(new WaiveBankCloseCheckRequest(period.Id, "Statement arrives on the 5th"));
        var waived = await check.CheckAsync(db, period, CancellationToken.None);
        Assert.True(waived.Passed);
        Assert.Contains("Waived by finance@example.com: Statement arrives on the 5th", waived.Summary);
    }
}

using Workbench.Contracts.Accounting;
using Workbench.Contracts.Cash;

namespace Workbench.Tests.Cash;

public sealed class MatchingTests
{
    private static readonly DateOnly D2 = new(2026, 10, 2);

    [Fact]
    public async Task Auto_match_uses_references_first_then_amount_and_date_with_confidence()
    {
        await using var kit = await CashKit.CreateAsync();
        var receipt = await kit.ReceiptAsync(D2, 12_500m, "EFT 20261002-ARMY");
        var check = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 4), 2_400m, "1001");
        var ach = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 10), 5_000m, "ACH-000123", check: false);
        var wire = await kit.ManualCashAsync(new DateOnly(2026, 10, 15), 8_000m, "Wire from NAVSEA");

        // A check for the same amount as check 1001 but a different number must not take its place.
        var otherCheck = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 5), 2_400m, "1002");

        await kit.ImportAsync(
            ("2026-10-02", "DFAS COLUMBUS EFT PMT 20261002-ARMY", 12_500m, null, "B1"),
            ("2026-10-06", "CHECK 1001", -2_400m, "0001001", "B2"),
            ("2026-10-10", "ACME FEDERAL ACH PMT ACH000123", -5_000m, null, "B3"),
            ("2026-10-16", "INCOMING WIRE", 8_000m, null, "B4"),
            ("2026-10-31", "MONTHLY SERVICE FEE", -25m, null, "B5"));

        var result = await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id));
        Assert.Equal(4, result.MatchesCreated);
        var items = await kit.ItemsAsync();
        MatchRule Rule(Guid glLine) => result.Matches.Single(m => m.JournalLineIds.Contains(glLine)).Rule;
        int Confidence(Guid glLine) => result.Matches.Single(m => m.JournalLineIds.Contains(glLine)).Confidence;

        Assert.Equal((MatchRule.ArPaymentReference, 100), (Rule(kit.CashLine(receipt)), Confidence(kit.CashLine(receipt))));
        Assert.Equal((MatchRule.ApCheckNumber, 100), (Rule(kit.CashLine(check)), Confidence(kit.CashLine(check))));
        Assert.Equal((MatchRule.ApAchReference, 100), (Rule(kit.CashLine(ach)), Confidence(kit.CashLine(ach))));
        Assert.Equal((MatchRule.AmountAndDate, 90), (Rule(kit.CashLine(wire)), Confidence(kit.CashLine(wire)))); // one day apart
        Assert.DoesNotContain(result.Matches, m => m.JournalLineIds.Contains(kit.CashLine(otherCheck)));
        Assert.All(result.Matches, m => Assert.Equal(m.BankTotal, m.GlTotal));

        // The fee stays for a person: it has no GL entry yet.
        Assert.Null(items.Single(i => i.Description == "MONTHLY SERVICE FEE").MatchId);

        // Running it again finds nothing new.
        Assert.Equal(0, (await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id))).MatchesCreated);
    }

    [Fact]
    public async Task Amount_matches_lose_confidence_with_distance_and_ties_are_left_alone()
    {
        await using var kit = await CashKit.CreateAsync();
        var far = await kit.ManualCashAsync(new DateOnly(2026, 10, 1), 300m, "Deposit");
        await kit.ManualCashAsync(new DateOnly(2026, 10, 20), 450m, "Deposit A");
        await kit.ManualCashAsync(new DateOnly(2026, 10, 20), 450m, "Deposit B");
        await kit.ImportAsync(("2026-10-05", "DEPOSIT", 300m, null, "B1"), ("2026-10-20", "DEPOSIT", 450m, null, "B2"), ("2026-10-25", "DEPOSIT", 999m, null, "B3"));

        var result = await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id));
        var match = Assert.Single(result.Matches);
        Assert.Equal((MatchRule.AmountAndDate, 75), (match.Rule, match.Confidence)); // four days apart: 95 - 4 x 5
        Assert.Equal(kit.CashLine(far), Assert.Single(match.JournalLineIds));
    }

    [Fact]
    public async Task A_deposit_of_several_receipts_matches_one_to_many_and_a_batch_payment_many_to_one()
    {
        await using var kit = await CashKit.CreateAsync();
        var r1 = await kit.ReceiptAsync(new DateOnly(2026, 10, 20), 1_000m, "CHK 5521");
        var r2 = await kit.ReceiptAsync(new DateOnly(2026, 10, 20), 2_500m, "CHK 7710");
        var batch = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 22), 3_000m, "ACH-BATCH-7", check: false);
        await kit.ImportAsync(
            ("2026-10-21", "DEPOSIT", 3_500m, null, "B1"),
            ("2026-10-23", "ACH DEBIT VENDOR ONE", -1_000m, null, "B2"),
            ("2026-10-23", "ACH DEBIT VENDOR TWO", -2_000m, null, "B3"));
        var items = await kit.ItemsAsync();
        var deposit = items.Single(i => i.Amount == 3_500m);

        // Manual: the two sides must agree.
        var mismatch = await Assert.ThrowsAsync<ArgumentException>(() =>
            kit.Reconciliations.MatchAsync(new MatchItemsRequest(kit.Bank.Id, [deposit.Id], [kit.CashLine(r1)])));
        Assert.Contains("don't agree", mismatch.Message);

        var oneToMany = await kit.Reconciliations.MatchAsync(new MatchItemsRequest(kit.Bank.Id, [deposit.Id], [kit.CashLine(r1), kit.CashLine(r2)], "Deposit slip 44"));
        Assert.Equal((MatchRule.Manual, 100, 3_500m, 2), (oneToMany.Rule, oneToMany.Confidence, oneToMany.GlTotal, oneToMany.JournalLineIds.Count));

        // Already matched items can't be matched again.
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            kit.Reconciliations.MatchAsync(new MatchItemsRequest(kit.Bank.Id, [deposit.Id], [kit.CashLine(r1), kit.CashLine(r2)])));

        // The batch ACH: the auto-matcher finds the two bank debits that make up the one GL payment.
        var auto = await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id));
        var group = Assert.Single(auto.Matches);
        Assert.Equal((MatchRule.AmountAndDateGroup, 60, 2), (group.Rule, group.Confidence, group.BankItemIds.Count));
        Assert.Equal(kit.CashLine(batch), Assert.Single(group.JournalLineIds));

        // Unmatching frees both sides.
        await kit.Reconciliations.UnmatchAsync(oneToMany.Id);
        Assert.Null((await kit.ItemsAsync()).Single(i => i.Id == deposit.Id).MatchId);
    }

    [Fact]
    public async Task A_voided_payment_and_its_reversal_clear_each_other()
    {
        await using var kit = await CashKit.CreateAsync();
        var check = await kit.VendorPaymentAsync(new DateOnly(2026, 10, 6), 900m, "1005");
        var reversal = await kit.Journal.ReverseAsync(check.Entry.Id, new ReverseJournalEntryRequest("Check voided"));

        var result = await kit.Reconciliations.AutoMatchAsync(new AutoMatchRequest(kit.Bank.Id));
        var pair = Assert.Single(result.Matches);
        Assert.Equal((MatchRule.ReversalPair, 0, 0m), (pair.Rule, pair.BankItemIds.Count, pair.GlTotal));
        Assert.Equal(new[] { kit.CashLine(check), kit.CashLine(reversal) }.Order(), pair.JournalLineIds.Order());
    }
}

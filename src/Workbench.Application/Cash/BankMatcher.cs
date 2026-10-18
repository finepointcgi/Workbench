using Workbench.Domain.Accounting;
using Workbench.Domain.Cash;

namespace Workbench.Application.Cash;

/// <summary>An open bank item the auto-matcher may pair.</summary>
internal sealed record OpenBankItem(Guid Id, DateOnly Date, decimal Amount, BankTransactionType Type, string Description, string? CheckNumber, string? BankReference);

/// <summary>A match the auto-matcher proposes (no bank items for a GL entry paired with its reversal).</summary>
internal sealed record ProposedMatch(IReadOnlyList<Guid> BankItemIds, IReadOnlyList<Guid> JournalLineIds, BankMatchRule Rule, int Confidence, string Note);

/// <summary>
/// The auto-match rules, in order, each consuming what it matches:
/// <list type="number">
/// <item>a GL entry and its reversal on the same account cancel out (a voided check never reaches the bank): 100;</item>
/// <item>an AR receipt whose payment reference appears in the bank item's description or reference, same amount: 100;</item>
/// <item>an AP check whose number is the bank item's check number, same amount: 100;</item>
/// <item>an AP ACH payment whose reference appears in the bank item, same amount: 100;</item>
/// <item>the same amount within the date window: 95 on the same day, 5 less per day apart (at least 60), 20 less when
/// another GL line of that amount is also in the window (only the closest is taken, never a tie);</item>
/// <item>one bank item equal to a group of GL lines of the same sign within the window (a deposit of several receipts,
/// a batch ACH), or several bank items equal to one GL line: 60.</item>
/// </list>
/// </summary>
internal static class BankMatcher
{
    /// <summary>Most GL lines tried in one group match (2^n subsets).</summary>
    private const int MaxGroupCandidates = 12;

    public static List<ProposedMatch> Propose(IReadOnlyList<OpenBankItem> bankItems, IReadOnlyList<GlCashLine> glLines, int windowDays)
    {
        var proposals = new List<ProposedMatch>();
        var bank = bankItems.OrderBy(b => b.Date).ThenBy(b => b.Id).ToList();
        var gl = glLines.ToList();

        // 1. Entry and reversal on this account cancel out.
        foreach (var reversal in gl.Where(l => l.ReversalOfId is not null).ToList())
        {
            var original = gl.FirstOrDefault(l => l.EntryId == reversal.ReversalOfId && l.Amount == -reversal.Amount);
            if (original is not null && gl.Contains(reversal))
            {
                proposals.Add(new([], [original.LineId, reversal.LineId], BankMatchRule.ReversalPair, 100,
                    $"{original.EntryNumber} reversed by {reversal.EntryNumber}"));
                gl.Remove(original);
                gl.Remove(reversal);
            }
        }

        void Take(OpenBankItem b, GlCashLine l, BankMatchRule rule, int confidence, string note)
        {
            proposals.Add(new([b.Id], [l.LineId], rule, confidence, note));
            bank.Remove(b);
            gl.Remove(l);
        }

        // 2–4. References.
        foreach (var b in bank.ToList())
        {
            var text = BankStatementService.Normalize($"{b.Description} {b.BankReference}");
            var check = b.CheckNumber;
            var byReference = gl.Where(l => l.Amount == b.Amount && l.Reference is { Length: > 0 }).ToList();

            var ar = byReference.FirstOrDefault(l => l.Source == JournalSource.Billing && b.Amount > 0 && Contains(text, l.Reference!));
            if (ar is not null)
            {
                Take(b, ar, BankMatchRule.ArPaymentReference, 100, $"AR payment reference {ar.Reference}");
                continue;
            }

            if (check is not null)
            {
                var ap = byReference.FirstOrDefault(l => l.Source == JournalSource.AP && l.IsCheck && SameCheck(check, l.Reference!));
                if (ap is not null)
                {
                    Take(b, ap, BankMatchRule.ApCheckNumber, 100, $"Check {check}");
                    continue;
                }
            }

            var ach = byReference.FirstOrDefault(l => l.Source == JournalSource.AP && !l.IsCheck && Contains(text, l.Reference!));
            if (ach is not null)
            {
                Take(b, ach, BankMatchRule.ApAchReference, 100, $"ACH reference {ach.Reference}");
            }
        }

        // 5. Amount within the date window, closest first. A check item with a number only matches GL checks without one.
        foreach (var b in bank.ToList())
        {
            var candidates = gl.Where(l => l.Amount == b.Amount && Math.Abs(l.Date.DayNumber - b.Date.DayNumber) <= windowDays)
                .Where(l => b.CheckNumber is null || !l.IsCheck || l.Reference is null || SameCheck(b.CheckNumber, l.Reference))
                .OrderBy(l => Math.Abs(l.Date.DayNumber - b.Date.DayNumber)).ToList();
            if (candidates.Count == 0)
            {
                continue;
            }

            var best = candidates[0];
            var days = Math.Abs(best.Date.DayNumber - b.Date.DayNumber);
            if (candidates.Count > 1 && Math.Abs(candidates[1].Date.DayNumber - b.Date.DayNumber) == days)
            {
                continue; // A tie: leave it for a person.
            }

            // Another bank item that fits this GL line at least as well: also a person's call.
            if (bank.Any(o => o != b && o.Amount == b.Amount && Math.Abs(best.Date.DayNumber - o.Date.DayNumber) <= days))
            {
                continue;
            }

            var confidence = Math.Max(60, 95 - 5 * days) - (candidates.Count > 1 ? 20 : 0);
            Take(b, best, BankMatchRule.AmountAndDate, confidence,
                days == 0 ? "Same amount, same day" : $"Same amount, {days} day{(days == 1 ? "" : "s")} apart");
        }

        // 6. Groups: one bank item = several GL lines, or several bank items = one GL line.
        foreach (var b in bank.ToList())
        {
            var candidates = gl.Where(l => Math.Sign(l.Amount) == Math.Sign(b.Amount) && Math.Abs(l.Amount) < Math.Abs(b.Amount)
                    && Math.Abs(l.Date.DayNumber - b.Date.DayNumber) <= windowDays)
                .OrderBy(l => Math.Abs(l.Date.DayNumber - b.Date.DayNumber)).Take(MaxGroupCandidates).ToList();
            var subset = SmallestSubsetSumming(candidates, l => l.Amount, b.Amount);
            if (subset is { Count: >= 2 })
            {
                proposals.Add(new([b.Id], subset.Select(l => l.LineId).ToList(), BankMatchRule.AmountAndDateGroup, 60,
                    $"One bank item for {subset.Count} GL items"));
                bank.Remove(b);
                gl.RemoveAll(subset.Contains);
            }
        }

        foreach (var l in gl.ToList())
        {
            var candidates = bank.Where(b => Math.Sign(b.Amount) == Math.Sign(l.Amount) && Math.Abs(b.Amount) < Math.Abs(l.Amount)
                    && Math.Abs(l.Date.DayNumber - b.Date.DayNumber) <= windowDays)
                .OrderBy(b => Math.Abs(l.Date.DayNumber - b.Date.DayNumber)).Take(MaxGroupCandidates).ToList();
            var subset = SmallestSubsetSumming(candidates, b => b.Amount, l.Amount);
            if (subset is { Count: >= 2 })
            {
                proposals.Add(new(subset.Select(b => b.Id).ToList(), [l.LineId], BankMatchRule.AmountAndDateGroup, 60,
                    $"{subset.Count} bank items for one GL item"));
                gl.Remove(l);
                bank.RemoveAll(subset.Contains);
            }
        }

        return proposals;
    }

    /// <summary>
    /// Whether the bank text carries the reference: the whole reference (letters and digits only), or every word of it of
    /// three or more characters when one has a digit (so "EFT 20261002-ARMY" is found in "EFT PMT 20261002-ARMY").
    /// </summary>
    private static bool Contains(string normalizedText, string reference)
    {
        var r = BankStatementService.Normalize(reference);
        if (r.Length >= 3 && normalizedText.Contains(r, StringComparison.Ordinal))
        {
            return true;
        }

        var words = reference.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(w => w.Split(['-', '/', '_', '.', '#', ':'], StringSplitOptions.RemoveEmptyEntries))
            .Select(BankStatementService.Normalize).Where(w => w.Length >= 3).ToList();
        return words.Any(w => w.Any(char.IsAsciiDigit)) && words.All(w => normalizedText.Contains(w, StringComparison.Ordinal));
    }

    private static bool SameCheck(string bankCheck, string glReference) =>
        string.Equals(bankCheck.TrimStart('0'), glReference.Trim().TrimStart('0'), StringComparison.OrdinalIgnoreCase);

    /// <summary>The smallest subset (by count, then earliest) of up to <see cref="MaxGroupCandidates"/> items summing exactly to the target.</summary>
    private static List<T>? SmallestSubsetSumming<T>(IReadOnlyList<T> items, Func<T, decimal> amount, decimal target)
    {
        if (items.Count < 2)
        {
            return null;
        }

        List<T>? best = null;
        var n = items.Count;
        for (var mask = 1; mask < 1 << n; mask++)
        {
            var bits = System.Numerics.BitOperations.PopCount((uint)mask);
            if (bits < 2 || (best is not null && bits >= best.Count))
            {
                continue;
            }

            decimal sum = 0;
            for (var i = 0; i < n; i++)
            {
                if ((mask & (1 << i)) != 0)
                {
                    sum += amount(items[i]);
                }
            }

            if (sum == target)
            {
                best = Enumerable.Range(0, n).Where(i => (mask & (1 << i)) != 0).Select(i => items[i]).ToList();
            }
        }

        return best;
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Cash;

/// <summary>
/// A posted journal line on a GL cash account, with the reference the bank will show: the AR payment reference for a
/// cash receipt, the check number or ACH reference for an AP payment (a reversal carries its original's).
/// <see cref="Amount"/> is debit-positive.
/// </summary>
internal sealed record GlCashLine(
    Guid LineId,
    Guid EntryId,
    string EntryNumber,
    DateOnly Date,
    JournalSource Source,
    Guid? SourceId,
    string Description,
    decimal Amount,
    Guid? ReversalOfId,
    Guid? ReversedById,
    string? Reference,
    bool IsCheck,
    bool IsAch);

internal static class CashLedger
{
    /// <summary>Posted (including later reversed) lines on <paramref name="glAccountId"/>, oldest first.</summary>
    public static async Task<List<GlCashLine>> LoadAsync(IWorkbenchDbContext db, Guid glAccountId, CancellationToken ct)
    {
        var rows = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where l.AccountId == glAccountId && e.Status != JournalStatus.Draft
                select new { l.Id, EntryId = e.Id, e.Number, e.Date, e.Source, e.SourceId, e.Description, l.Memo, l.Debit, l.Credit, e.ReversalOfId, e.ReversedById, e.Sequence })
            .ToListAsync(ct);

        // References from the subledger records behind the entries (a reversal points at the entry it reverses).
        var reversed = rows.Where(r => r.ReversalOfId is not null).Select(r => r.ReversalOfId!.Value).Distinct().ToList();
        var originals = await db.Set<JournalEntry>().AsNoTracking().Where(e => reversed.Contains(e.Id))
            .Select(e => new { e.Id, e.Source, e.SourceId }).ToDictionaryAsync(e => e.Id, ct);
        (JournalSource Source, Guid? SourceId) Origin(JournalSource source, Guid? sourceId, Guid? reversalOf) =>
            reversalOf is { } o && originals.TryGetValue(o, out var original) ? (original.Source, original.SourceId) : (source, sourceId);

        var origins = rows.Select(r => Origin(r.Source, r.SourceId, r.ReversalOfId)).ToList();
        var arIds = origins.Where(o => o.Source == JournalSource.Billing && o.SourceId is not null).Select(o => o.SourceId!.Value).Distinct().ToList();
        var apIds = origins.Where(o => o.Source == JournalSource.AP && o.SourceId is not null).Select(o => o.SourceId!.Value).Distinct().ToList();
        var ar = await db.Set<Payment>().AsNoTracking().Where(p => arIds.Contains(p.Id)).Select(p => new { p.Id, p.Reference, p.Method })
            .ToDictionaryAsync(p => p.Id, ct);
        var ap = await db.Set<ApPayment>().AsNoTracking().Where(p => apIds.Contains(p.Id)).Select(p => new { p.Id, p.Reference, p.Method })
            .ToDictionaryAsync(p => p.Id, ct);

        return rows.Select((r, i) =>
            {
                var (source, sourceId) = origins[i];
                string? reference = null;
                bool isCheck = false, isAch = false;
                if (source == JournalSource.Billing && sourceId is { } pid && ar.TryGetValue(pid, out var receipt))
                {
                    reference = receipt.Reference;
                    isCheck = receipt.Method == Domain.Billing.PaymentMethod.Check;
                    isAch = receipt.Method == Domain.Billing.PaymentMethod.Eft;
                }
                else if (source == JournalSource.AP && sourceId is { } apid && ap.TryGetValue(apid, out var payment))
                {
                    reference = payment.Reference;
                    isCheck = payment.Method == Domain.Purchasing.PaymentMethod.Check;
                    isAch = payment.Method == Domain.Purchasing.PaymentMethod.Ach;
                }

                var description = string.IsNullOrWhiteSpace(r.Memo) || r.Memo == r.Description ? r.Description : $"{r.Description} · {r.Memo}";
                return (Line: new GlCashLine(r.Id, r.EntryId, r.Number, r.Date, r.Source, r.SourceId, description, r.Debit - r.Credit,
                    r.ReversalOfId, r.ReversedById, reference, isCheck, isAch), r.Sequence);
            })
            .OrderBy(x => x.Line.Date).ThenBy(x => x.Sequence).Select(x => x.Line).ToList();
    }

    /// <summary>Debit-positive posted balances of GL accounts, through a date (or all).</summary>
    public static async Task<Dictionary<Guid, decimal>> BalancesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> accountIds, DateOnly? through, CancellationToken ct)
    {
        var rows = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where accountIds.Contains(l.AccountId) && e.Status != JournalStatus.Draft && (through == null || e.Date <= through)
                select new { l.AccountId, l.Debit, l.Credit })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.AccountId).ToDictionary(g => g.Key, g => g.Sum(r => r.Debit - r.Credit));
    }
}

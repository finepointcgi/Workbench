using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>Credit bookkeeping shared by bills, credits and payment runs: applications, what's reserved, what's credited.</summary>
internal static class ApCredits
{
    public const string ApplicationEntity = nameof(ApCreditApplication);
    public const string RefundEntity = nameof(ApRefund);

    public static BillKind ToContract(this ApDocumentKind k) => (BillKind)(int)k;
    public static ApDocumentKind ToDomain(this BillKind k) => (ApDocumentKind)(int)k;
    public static CreditApplicationState ToContract(this ApCreditApplicationStatus s) => (CreditApplicationState)(int)s;
    public static RefundState ToContract(this ApRefundStatus s) => (RefundState)(int)s;

    /// <summary>"bill", "credit memo", "debit memo" or "retainage release", for messages.</summary>
    public static string Label(ApDocumentKind kind) => kind switch
    {
        ApDocumentKind.CreditMemo => "credit memo",
        ApDocumentKind.DebitMemo => "debit memo",
        ApDocumentKind.RetainageRelease => "retainage release",
        _ => "bill",
    };

    public static string Title(ApBill b) => $"{char.ToUpperInvariant(Label(b.Kind)[0])}{Label(b.Kind)[1..]} {b.Number}";

    /// <summary>Applications that still count (scheduled in an open run, or applied).</summary>
    public static IQueryable<ApCreditApplication> Live(IWorkbenchDbContext db) =>
        db.Set<ApCreditApplication>().Where(a => a.Status != ApCreditApplicationStatus.Reversed);

    /// <summary>Credit netted in open payment runs, per memo.</summary>
    public static async Task<Dictionary<Guid, decimal>> ScheduledByCreditAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? creditIds, CancellationToken ct)
    {
        var q = db.Set<ApCreditApplication>().AsNoTracking().Where(a => a.Status == ApCreditApplicationStatus.Scheduled);
        if (creditIds is not null)
        {
            q = q.Where(a => creditIds.Contains(a.CreditId));
        }

        return (await q.Select(a => new { a.CreditId, a.Amount }).ToListAsync(ct)).GroupBy(a => a.CreditId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));
    }

    /// <summary>Credit netted in open payment runs, per bill.</summary>
    public static async Task<Dictionary<Guid, decimal>> ScheduledByBillAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> billIds, CancellationToken ct) =>
        (await db.Set<ApCreditApplication>().AsNoTracking().Where(a => a.Status == ApCreditApplicationStatus.Scheduled && billIds.Contains(a.BillId))
            .Select(a => new { a.BillId, a.Amount }).ToListAsync(ct))
        .GroupBy(a => a.BillId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount));

    /// <summary>Posted memos with credit left (functional currency), oldest first, optionally for some vendors.</summary>
    public static async Task<List<ApBill>> OpenCreditsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? vendorIds, CancellationToken ct, bool tracking = false)
    {
        var q = db.Set<ApBill>().Where(b => (b.Kind == ApDocumentKind.CreditMemo || b.Kind == ApDocumentKind.DebitMemo) && b.Status == ApBillStatus.Posted);
        if (!tracking)
        {
            q = q.AsNoTracking();
        }

        if (vendorIds is not null)
        {
            q = q.Where(b => vendorIds.Contains(b.VendorId));
        }

        return (await q.ToListAsync(ct)).Where(b => b.Balance > 0 && !b.IsForeignCurrency).OrderBy(b => b.InvoiceDate).ThenBy(b => b.Number, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Takes <paramref name="amount"/> of <paramref name="credit"/> off <paramref name="bill"/> (both tracked). Applied rows
    /// update both running totals now; scheduled ones when their payment run is paid.
    /// </summary>
    public static ApCreditApplication Apply(IWorkbenchDbContext db, ApBill credit, ApBill bill, decimal amount, DateOnly date, string actor, DateTime utcNow,
        Guid? runId = null, bool scheduled = false)
    {
        var application = new ApCreditApplication
        {
            CreditId = credit.Id, BillId = bill.Id, VendorId = bill.VendorId, Amount = amount, AppliedOn = date, RunId = runId,
            Status = scheduled ? ApCreditApplicationStatus.Scheduled : ApCreditApplicationStatus.Applied,
            AppliedBy = actor, AppliedAtUtc = utcNow,
        };
        db.Set<ApCreditApplication>().Add(application);
        if (!scheduled)
        {
            credit.AppliedAmount += amount;
            bill.AppliedAmount += amount;
        }

        return application;
    }

    /// <summary>Checks a credit can go against a bill: same vendor and company, a posted bill (or release) in the functional currency.</summary>
    public static void EnsureApplicable(ApBill credit, ApBill bill)
    {
        if (!credit.IsCredit || credit.Status != ApBillStatus.Posted)
        {
            throw new ArgumentException($"{Title(credit)} isn't a posted credit or debit memo.");
        }

        if (bill.IsCredit || bill.Status != ApBillStatus.Posted)
        {
            throw new ArgumentException($"{Title(bill)} isn't a posted bill, so credit can't be applied to it.");
        }

        if (bill.VendorId != credit.VendorId)
        {
            throw new ArgumentException($"{Title(bill)} is another vendor's.");
        }

        if (bill.CompanyId != credit.CompanyId)
        {
            throw new ArgumentException($"{Title(bill)} belongs to another company.");
        }

        if (bill.IsForeignCurrency || credit.IsForeignCurrency)
        {
            throw new ArgumentException($"{Title(bill)} is in {bill.CurrencyCode ?? credit.CurrencyCode}; credits apply to dollar bills only.");
        }
    }

    /// <summary>Amounts credited so far per original bill line, by memos that aren't void (optionally leaving one memo out).</summary>
    public static async Task<Dictionary<Guid, (decimal Amount, decimal Retainage)>> CreditedByLineAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid> lineIds, Guid? exceptMemoId, CancellationToken ct) =>
        (await db.Set<ApBillLine>().AsNoTracking().Where(l => l.CreditsLineId != null && lineIds.Contains(l.CreditsLineId!.Value) && l.BillId != exceptMemoId)
            .Join(db.Set<ApBill>().Where(b => b.Status != ApBillStatus.Void), l => l.BillId, b => b.Id, (l, b) => new { Id = l.CreditsLineId!.Value, l.Amount, l.RetainageAmount })
            .ToListAsync(ct))
        .GroupBy(x => x.Id).ToDictionary(g => g.Key, g => (g.Sum(x => x.Amount), g.Sum(x => x.RetainageAmount)));

    /// <summary>
    /// Retainage per posted subcontract bill: held on its lines, taken back by memos crediting those lines, released by posted
    /// releases, and in releases not yet posted (or not yet void).
    /// </summary>
    public static async Task<List<RetainageBillDto>> RetainageAsync(IWorkbenchDbContext db, Guid? subcontractId, IReadOnlyCollection<Guid>? billIds, CancellationToken ct)
    {
        var lines = db.Set<ApBillLine>().AsNoTracking().Where(l => l.Kind == ApBillLineKind.Subcontract && l.RetainageAmount != 0);
        if (subcontractId is { } sid)
        {
            lines = lines.Where(l => l.SubcontractId == sid);
        }

        if (billIds is not null)
        {
            lines = lines.Where(l => billIds.Contains(l.BillId));
        }

        var held = await lines.Join(db.Set<ApBill>().Where(b => b.Kind == ApDocumentKind.Bill && b.Status == ApBillStatus.Posted), l => l.BillId, b => b.Id, (l, b) => new { l.Id, l.RetainageAmount, Bill = b })
            .ToListAsync(ct);
        if (held.Count == 0)
        {
            return [];
        }

        var lineIds = held.Select(h => h.Id).ToList();
        var credited = await CreditedByLineAsync(db, lineIds, null, ct);
        var ids = held.Select(h => h.Bill.Id).Distinct().ToList();
        var releases = await db.Set<ApBillLine>().AsNoTracking().Where(l => l.ReleasesBillId != null && ids.Contains(l.ReleasesBillId!.Value))
            .Join(db.Set<ApBill>().Where(b => b.Status != ApBillStatus.Void), l => l.BillId, b => b.Id, (l, b) => new { Bill = l.ReleasesBillId!.Value, l.Amount, b.Status })
            .ToListAsync(ct);
        return held.GroupBy(h => h.Bill.Id).Select(g =>
        {
            var bill = g.First().Bill;
            var mine = releases.Where(r => r.Bill == bill.Id).ToList();
            return new RetainageBillDto(bill.Id, bill.Number, bill.InvoiceNumber, bill.InvoiceDate, g.Sum(h => h.RetainageAmount),
                g.Sum(h => credited.TryGetValue(h.Id, out var c) ? c.Retainage : 0),
                mine.Where(r => r.Status == ApBillStatus.Posted).Sum(r => r.Amount), mine.Where(r => r.Status != ApBillStatus.Posted).Sum(r => r.Amount));
        }).OrderBy(r => r.InvoiceDate).ThenBy(r => r.Number, StringComparer.Ordinal).ToList();
    }
}

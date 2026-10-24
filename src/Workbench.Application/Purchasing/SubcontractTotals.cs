using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>What has been invoiced against subcontracts, from AP bill lines and vendor ODCs entered outside AP.</summary>
/// <summary>Retainage is what's still held (withheld less credited less released); Released is what posted releases made payable.</summary>
internal sealed record SubcontractBilling(Guid SubcontractId, decimal Billed, decimal Retainage, decimal Paid, IReadOnlyList<Contracts.Purchasing.SubcontractInvoiceDto> Invoices,
    decimal Released = 0);

internal static class SubcontractTotals
{
    /// <summary>
    /// Billed to date per subcontract: lines on submitted, approved and posted bills, plus submitted or approved ODCs charged
    /// to the subcontract that no such bill line links to. <paramref name="excludeBillId"/> leaves one bill out (the one
    /// being checked), along with the ODCs it links.
    /// </summary>
    public static async Task<Dictionary<Guid, SubcontractBilling>> LoadAsync(
        IWorkbenchDbContext db, IReadOnlyCollection<Guid>? subcontractIds, CancellationToken ct, Guid? excludeBillId = null)
    {
        var linesQuery = db.Set<ApBillLine>().AsNoTracking().Where(l => l.SubcontractId != null);
        if (subcontractIds is not null)
        {
            linesQuery = linesQuery.Where(l => subcontractIds.Contains(l.SubcontractId!.Value));
        }

        var lines = await linesQuery.ToListAsync(ct);
        var billIds = lines.Select(l => l.BillId).Distinct().ToList();
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, ct);

        // ODCs linked from any live bill count through that bill (or not at all, for the excluded one).
        var linkedOdcs = (await db.Set<ApBillLine>().AsNoTracking().Where(l => l.OdcEntryId != null)
                .Join(db.Set<ApBill>().Where(b => b.Status != ApBillStatus.Void), l => l.BillId, b => b.Id, (l, b) => new { l.OdcEntryId, b.Id, b.Status })
                .ToListAsync(ct))
            .Where(x => x.Id == excludeBillId || PurchasingSupport.BilledStatuses.Contains(x.Status))
            .Select(x => x.OdcEntryId!.Value).ToHashSet();

        // A voided posted bill took its ODCs back off with offsets; neither the ODCs nor the offsets are invoices.
        foreach (var voided in await db.Set<ApBillLine>().AsNoTracking().Where(l => l.ReversalOdcEntryId != null).Select(l => new { l.OdcEntryId, l.ReversalOdcEntryId }).ToListAsync(ct))
        {
            linkedOdcs.Add(voided.ReversalOdcEntryId!.Value);
            if (voided.OdcEntryId is { } original)
            {
                linkedOdcs.Add(original);
            }
        }

        var odcQuery = db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.SubcontractId != null && (o.Status == OdcStatus.Submitted || o.Status == OdcStatus.Approved));
        if (subcontractIds is not null)
        {
            odcQuery = odcQuery.Where(o => subcontractIds.Contains(o.SubcontractId!.Value));
        }

        var odcs = (await odcQuery.ToListAsync(ct)).Where(o => !linkedOdcs.Contains(o.Id)).ToList();

        var result = new Dictionary<Guid, SubcontractBilling>();
        var ids = lines.Select(l => l.SubcontractId!.Value).Concat(odcs.Select(o => o.SubcontractId!.Value)).Distinct();
        foreach (var id in ids)
        {
            var invoices = new List<Contracts.Purchasing.SubcontractInvoiceDto>();
            decimal billed = 0, retainage = 0, paid = 0, released = 0;
            foreach (var group in lines.Where(l => l.SubcontractId == id).GroupBy(l => l.BillId))
            {
                var bill = bills[group.Key];

                // Credit and debit memos take billed (and the retainage on it) back; a release pays out retainage already billed.
                var sign = bill.IsCredit ? -1 : 1;
                var amount = sign * group.Sum(l => l.Amount);
                var held = sign * group.Sum(l => l.RetainageAmount);
                invoices.Add(new(bill.Id, bill.Number, bill.InvoiceNumber, bill.InvoiceDate, amount, held, bill.Status.ToString(), bill.Kind.ToContract()));
                if (bill.Id == excludeBillId || !PurchasingSupport.BilledStatuses.Contains(bill.Status))
                {
                    continue;
                }

                if (bill.Kind == ApDocumentKind.RetainageRelease)
                {
                    if (bill.Status == ApBillStatus.Posted)
                    {
                        released += amount;
                        paid += bill.Payable is > 0 and var due ? Financials.Money.Round(bill.PaidAmount * amount / due) : 0;
                    }

                    continue;
                }

                billed += amount;
                retainage += held;
                if (bill.IsCredit)
                {
                    continue; // A memo's "paid" is a refund to us, not a payment on the subcontract.
                }

                // Payments apply to the bill as a whole; this subcontract's share is in proportion to its payable lines.
                if (bill.Amount - bill.RetainageAmount is > 0 and var payable)
                {
                    paid += Financials.Money.Round(bill.PaidAmount * (amount - held) / payable);
                }
            }

            foreach (var odc in odcs.Where(o => o.SubcontractId == id))
            {
                invoices.Add(new(null, null, odc.InvoiceNumber, odc.IncurredOn, odc.Amount, 0, $"ODC {odc.Status}"));
                billed += odc.Amount;
            }

            result[id] = new SubcontractBilling(id, billed, retainage - released, paid, invoices.OrderBy(i => i.InvoiceDate).ToList(), released);
        }

        return result;
    }
}

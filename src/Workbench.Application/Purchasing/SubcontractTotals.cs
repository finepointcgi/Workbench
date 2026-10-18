using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>What has been invoiced against subcontracts, from AP bill lines and vendor ODCs entered outside AP.</summary>
internal sealed record SubcontractBilling(Guid SubcontractId, decimal Billed, decimal Retainage, decimal Paid, IReadOnlyList<Contracts.Purchasing.SubcontractInvoiceDto> Invoices);

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
            decimal billed = 0, retainage = 0, paid = 0;
            foreach (var group in lines.Where(l => l.SubcontractId == id).GroupBy(l => l.BillId))
            {
                var bill = bills[group.Key];
                var amount = group.Sum(l => l.Amount);
                var held = group.Sum(l => l.RetainageAmount);
                invoices.Add(new(bill.Id, bill.Number, bill.InvoiceNumber, bill.InvoiceDate, amount, held, bill.Status.ToString()));
                if (bill.Id == excludeBillId || !PurchasingSupport.BilledStatuses.Contains(bill.Status))
                {
                    continue;
                }

                billed += amount;
                retainage += held;

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

            result[id] = new SubcontractBilling(id, billed, retainage, paid, invoices.OrderBy(i => i.InvoiceDate).ToList());
        }

        return result;
    }
}

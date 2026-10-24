using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Security;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Security;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

public sealed class ApReportService(IWorkbenchDbContextFactory dbFactory, SecurityEventLog securityLog, IOptions<PurchasingOptions> options) : IApReportService
{
    /// <summary>
    /// Open payable balance as of <paramref name="asOf"/>: posted bills invoiced by then, less payments made and credits
    /// applied by then, bucketed by days past the due date. Open credit and debit memos count negative, aged from their date.
    /// Retainage is held, not due, so it's shown apart (less what credits took back and releases made payable).
    /// </summary>
    public async Task<AgingReportDto> GetAgingAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bills = await OpenAsOfAsync(db, asOf, null, cancellationToken);
        var paid = await PaidByBillAsync(db, asOf, cancellationToken);
        var (appliedToBill, appliedFromCredit, refunded) = await CreditsAsOfAsync(db, asOf, cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, cancellationToken);
        var rows = new List<AgingRowDto>();
        foreach (var group in bills.GroupBy(b => b.VendorId))
        {
            decimal current = 0, d30 = 0, d60 = 0, d90 = 0, over = 0, credits = 0;
            foreach (var bill in group)
            {
                var open = bill.IsCredit
                    ? -(bill.Payable - refunded.GetValueOrDefault(bill.Id) - appliedFromCredit.GetValueOrDefault(bill.Id))
                    : bill.Payable - paid.GetValueOrDefault(bill.Id) - appliedToBill.GetValueOrDefault(bill.Id);
                if (open == 0)
                {
                    continue;
                }

                if (open < 0)
                {
                    credits += open;
                }

                var days = asOf.DayNumber - bill.DueDate.DayNumber;
                switch (days)
                {
                    case <= 0: current += open; break;
                    case <= 30: d30 += open; break;
                    case <= 60: d60 += open; break;
                    case <= 90: d90 += open; break;
                    default: over += open; break;
                }
            }

            var retainage = group.Sum(b => b.Kind == ApDocumentKind.RetainageRelease ? -b.Amount : b.IsCredit ? -b.RetainageAmount : b.RetainageAmount);
            if (current + d30 + d60 + d90 + over == 0 && retainage == 0 && credits == 0)
            {
                continue;
            }

            var vendor = vendors[group.Key];
            rows.Add(new AgingRowDto(vendor.Id, vendor.Code, vendor.Name, current, d30, d60, d90, over, retainage, credits));
        }

        return new AgingReportDto(asOf, rows.OrderBy(r => r.VendorCode).ToList());
    }

    /// <summary>Documents that were posted as of a date: posted ones, and posted ones voided after it.</summary>
    private static async Task<List<ApBill>> OpenAsOfAsync(IWorkbenchDbContext db, DateOnly asOf, Guid? vendorId, CancellationToken ct) =>
        await db.Set<ApBill>().AsNoTracking()
            .Where(b => (b.Status == ApBillStatus.Posted || (b.Status == ApBillStatus.Void && b.VoidReversalDate != null && b.VoidReversalDate > asOf))
                && b.InvoiceDate <= asOf && (vendorId == null || b.VendorId == vendorId))
            .ToListAsync(ct);

    /// <summary>Credits applied by a date (per bill and per memo) and refunds received by then (per memo).</summary>
    private static async Task<(Dictionary<Guid, decimal> ToBill, Dictionary<Guid, decimal> FromCredit, Dictionary<Guid, decimal> Refunded)> CreditsAsOfAsync(
        IWorkbenchDbContext db, DateOnly asOf, CancellationToken ct)
    {
        var applied = await db.Set<ApCreditApplication>().AsNoTracking().Where(a => a.Status == ApCreditApplicationStatus.Applied && a.AppliedOn <= asOf)
            .Select(a => new { a.BillId, a.CreditId, a.Amount }).ToListAsync(ct);
        var refunds = await db.Set<ApRefund>().AsNoTracking().Where(r => r.Status == ApRefundStatus.Received && r.ReceivedOn <= asOf)
            .Select(r => new { r.CreditId, r.Amount }).ToListAsync(ct);
        return (applied.GroupBy(a => a.BillId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount)),
            applied.GroupBy(a => a.CreditId).ToDictionary(g => g.Key, g => g.Sum(a => a.Amount)),
            refunds.GroupBy(r => r.CreditId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount)));
    }

    public async Task<VendorLedgerDto> GetVendorLedgerAsync(Guid vendorId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking()
            .Where(b => b.VendorId == vendorId && (b.Status == ApBillStatus.Posted || (b.Status == ApBillStatus.Void && b.VoidReversalDate != null))).ToListAsync(cancellationToken);
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.VendorId == vendorId && p.PaidOn != null).ToListAsync(cancellationToken);
        var refunds = await db.Set<ApRefund>().AsNoTracking().Where(r => r.VendorId == vendorId).ToListAsync(cancellationToken);
        var billNumbers = bills.ToDictionary(b => b.Id, b => b.Number);

        // Every movement in date order: bills (charges, net of retainage), credits, retainage releases, payments, refunds, and
        // voids (a payment or refund given back, a posted document reversed). Applying a credit to a bill moves nothing here.
        var events = new List<(DateOnly Date, int Order, string Kind, string Reference, string Description, decimal Charges, decimal Payments)>();
        foreach (var b in bills)
        {
            var (kind, description, charges, credits) = b.Kind switch
            {
                ApDocumentKind.CreditMemo => ("Credit memo", $"Vendor credit {b.InvoiceNumber}", 0m, b.Payable),
                ApDocumentKind.DebitMemo => ("Debit memo", $"Billed back {b.InvoiceNumber}", 0m, b.Payable),
                ApDocumentKind.RetainageRelease => ("Retainage release", b.Description ?? "Retainage released", b.Payable, 0m),
                _ => ("Bill", $"Invoice {b.InvoiceNumber}" + (b.RetainageAmount > 0 ? $" (retainage {b.RetainageAmount:N2} held)" : ""), b.Payable, 0m),
            };
            events.Add((b.InvoiceDate, 0, kind, b.Number, description, charges, credits));
            if (b.Status == ApBillStatus.Void)
            {
                events.Add((b.VoidReversalDate!.Value, 2, "Void", b.Number, $"Voided: {b.VoidReason}", -charges, -credits));
            }
        }

        foreach (var p in payments)
        {
            var bill = billNumbers.GetValueOrDefault(p.BillId) ?? "";
            events.Add((p.PaidOn!.Value, 1, "Payment", p.Reference ?? "", $"{p.Method} for {bill}", 0, p.Amount));
            if (p.Status == ApPaymentStatus.Void)
            {
                var voided = p.VoidedAtUtc is { } v ? DateOnly.FromDateTime(v) : p.PaidOn.Value;
                events.Add((voided < p.PaidOn.Value ? p.PaidOn.Value : voided, 2, "Void", p.Reference ?? "", $"Voided: {p.VoidReason}", 0, -p.Amount));
            }
        }

        foreach (var r in refunds)
        {
            var credit = billNumbers.GetValueOrDefault(r.CreditId) ?? "";
            events.Add((r.ReceivedOn, 1, "Refund", r.Reference, $"Refund of {credit}", r.Amount, 0));
            if (r.Status == ApRefundStatus.Void)
            {
                var voided = r.VoidedAtUtc is { } v ? DateOnly.FromDateTime(v) : r.ReceivedOn;
                events.Add((voided < r.ReceivedOn ? r.ReceivedOn : voided, 2, "Void", r.Reference, $"Refund voided: {r.VoidReason}", -r.Amount, 0));
            }
        }

        var ordered = events.OrderBy(e => e.Date).ThenBy(e => e.Order).ThenBy(e => e.Reference, StringComparer.Ordinal).ToList();
        var opening = ordered.Where(e => from is { } f && e.Date < f).Sum(e => e.Charges - e.Payments);
        var balance = opening;
        var lines = new List<VendorLedgerLineDto>();
        foreach (var e in ordered.Where(e => (from is null || e.Date >= from) && (to is null || e.Date <= to)))
        {
            balance += e.Charges - e.Payments;
            lines.Add(new VendorLedgerLineDto(e.Date, e.Kind, e.Reference, e.Description, e.Charges, e.Payments, balance));
        }

        return new VendorLedgerDto(vendor.Id, vendor.Code, vendor.Name, from, to, opening, lines, balance);
    }

    /// <summary>Payments (by payment date, voids excluded) in the calendar year to 1099 vendors, by box; the threshold is $600.</summary>
    public async Task<Form1099ReportDto> Get1099Async(int year, CancellationToken cancellationToken = default)
    {
        if (year is < 2000 or > 2100)
        {
            throw new ArgumentException("Choose a calendar year.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var start = new DateOnly(year, 1, 1);
        var end = new DateOnly(year, 12, 31);
        var payments = await db.Set<ApPayment>().AsNoTracking()
            .Where(p => p.Status == ApPaymentStatus.Paid && p.PaidOn >= start && p.PaidOn <= end)
            .Join(db.Set<Vendor>().Where(v => v.Is1099), p => p.VendorId, v => v.Id, (p, v) => new { p.Amount, Vendor = v })
            .ToListAsync(cancellationToken);
        // A vendor's refund in the year comes off what it was paid.
        var refunds = (await db.Set<ApRefund>().AsNoTracking().Where(r => r.Status == ApRefundStatus.Received && r.ReceivedOn >= start && r.ReceivedOn <= end)
                .Select(r => new { r.VendorId, r.Amount }).ToListAsync(cancellationToken))
            .GroupBy(r => r.VendorId).ToDictionary(g => g.Key, g => g.Sum(r => r.Amount));
        var threshold = options.Value.Form1099Threshold;
        var rows = payments.GroupBy(p => p.Vendor.Id).Select(g =>
        {
            var v = g.First().Vendor;
            return new Form1099RowDto(v.Id, v.Code, v.Name, PurchasingCatalog.MaskTaxId(v.TaxIdLast4), v.Form1099Box ?? PurchasingCatalog.DefaultForm1099Box,
                g.Sum(p => p.Amount) - refunds.GetValueOrDefault(v.Id), g.Count());
        }).OrderBy(r => r.VendorCode).ToList();
        return new Form1099ReportDto(year, threshold, rows.Where(r => r.Amount >= threshold).ToList(), rows.Where(r => r.Amount < threshold).ToList());
    }

    public async Task<IReadOnlyList<SubcontractDto>> GetSubcontractStatusAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var subs = await db.Set<Subcontract>().AsNoTracking().OrderBy(s => s.Number).ToListAsync(cancellationToken);
        return await SubcontractService.ToDtosAsync(db, subs, cancellationToken);
    }

    public async Task<SmallBusinessReportDto> GetSmallBusinessAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        if (to < from)
        {
            throw new ArgumentException("The period ends before it starts.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking()
            .Where(b => b.Status == ApBillStatus.Posted && b.InvoiceDate >= from && b.InvoiceDate <= to && b.Kind != ApDocumentKind.RetainageRelease)
            .Join(db.Set<Vendor>(), b => b.VendorId, v => v.Id, (b, v) => new { Amount = b.Kind == ApDocumentKind.CreditMemo || b.Kind == ApDocumentKind.DebitMemo ? -b.Amount : b.Amount, Vendor = v })
            .ToListAsync(cancellationToken);
        var total = bills.Sum(b => b.Amount);
        decimal Percent(decimal amount) => total == 0 ? 0 : Math.Round(amount * 100m / total, 1);

        var bySize = bills.GroupBy(b => string.IsNullOrWhiteSpace(b.Vendor.BusinessSize) ? "Not specified" : b.Vendor.BusinessSize!)
            .Select(g => new SmallBusinessRowDto(g.Key, g.Select(b => b.Vendor.Id).Distinct().Count(), g.Sum(b => b.Amount), Percent(g.Sum(b => b.Amount))))
            .OrderByDescending(r => r.Amount).ToList();

        // A vendor counts toward every category it holds, so these don't add up to the total.
        var socio = await Customization.ValueLists.ItemsAsync(db, Customization.ValueListCatalog.VendorSocioEconomic, cancellationToken);
        var bySocio = socio.Select(category =>
        {
            var matching = bills.Where(b => (b.Vendor.SocioEconomicCategories ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(category.Code, StringComparer.OrdinalIgnoreCase)).ToList();
            return new SmallBusinessRowDto(category.Label, matching.Select(b => b.Vendor.Id).Distinct().Count(), matching.Sum(b => b.Amount), Percent(matching.Sum(b => b.Amount)));
        }).ToList();
        return new SmallBusinessReportDto(from, to, total, bySize, bySocio);
    }

    public async Task<PurchasingExportFile> ExportAgingCsvAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var report = await GetAgingAsync(asOf, cancellationToken);
        var rows = new List<IEnumerable<object?>> { new object?[] { "VendorCode", "Vendor", "Current", "1-30", "31-60", "61-90", "Over 90", "Total", "Retainage", "OpenCredits" } };
        rows.AddRange(report.Rows.Select(r => new object?[] { r.VendorCode, r.VendorName, r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total, r.Retainage, r.Credits }));
        await securityLog.RecordAsync(SecurityEventTypes.ExportFinancialCsv, "ap aging", SecurityOutcomes.Success, $"As of {asOf:yyyy-MM-dd}", cancellationToken: cancellationToken);
        return new PurchasingExportFile($"ap-aging-{asOf:yyyy-MM-dd}.csv", "text/csv", PurchasingSupport.Csv(rows));
    }

    public async Task<PurchasingExportFile> Export1099CsvAsync(int year, CancellationToken cancellationToken = default)
    {
        var report = await Get1099Async(year, cancellationToken);
        var rows = new List<IEnumerable<object?>> { new object?[] { "Year", "VendorCode", "Vendor", "TIN", "Box", "Amount", "Payments" } };
        rows.AddRange(report.Rows.Select(r => new object?[] { year, r.VendorCode, r.VendorName, r.TaxIdMasked, r.Box, r.Amount, r.Payments }));
        await securityLog.RecordAsync(PurchasingSecurityEvents.Export1099, $"1099 {year}", SecurityOutcomes.Success, $"{report.Rows.Count} vendors", cancellationToken: cancellationToken);
        return new PurchasingExportFile($"1099-{year}.csv", "text/csv", PurchasingSupport.Csv(rows));
    }

    private static async Task<Dictionary<Guid, decimal>> PaidByBillAsync(IWorkbenchDbContext db, DateOnly asOf, CancellationToken ct) =>
        (await db.Set<ApPayment>().AsNoTracking().Where(p => p.Status == ApPaymentStatus.Paid && p.PaidOn <= asOf).Select(p => new { p.BillId, p.Amount }).ToListAsync(ct))
            .GroupBy(p => p.BillId).ToDictionary(g => g.Key, g => g.Sum(p => p.Amount));
}

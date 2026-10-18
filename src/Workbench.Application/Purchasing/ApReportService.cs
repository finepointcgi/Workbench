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
    /// Open payable balance as of <paramref name="asOf"/>: posted bills invoiced by then, less payments made by then, bucketed
    /// by days past the due date. Retainage is held, not due, so it's shown apart.
    /// </summary>
    public async Task<AgingReportDto> GetAgingAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.Status == ApBillStatus.Posted && b.InvoiceDate <= asOf).ToListAsync(cancellationToken);
        var paid = await PaidByBillAsync(db, asOf, cancellationToken);
        var vendors = await db.Set<Vendor>().AsNoTracking().ToDictionaryAsync(v => v.Id, cancellationToken);
        var rows = new List<AgingRowDto>();
        foreach (var group in bills.GroupBy(b => b.VendorId))
        {
            decimal current = 0, d30 = 0, d60 = 0, d90 = 0, over = 0;
            foreach (var bill in group)
            {
                var open = bill.Payable - paid.GetValueOrDefault(bill.Id);
                if (open == 0)
                {
                    continue;
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

            var retainage = group.Sum(b => b.RetainageAmount);
            if (current + d30 + d60 + d90 + over == 0 && retainage == 0)
            {
                continue;
            }

            var vendor = vendors[group.Key];
            rows.Add(new AgingRowDto(vendor.Id, vendor.Code, vendor.Name, current, d30, d60, d90, over, retainage));
        }

        return new AgingReportDto(asOf, rows.OrderBy(r => r.VendorCode).ToList());
    }

    public async Task<VendorLedgerDto> GetVendorLedgerAsync(Guid vendorId, DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => b.VendorId == vendorId && b.Status == ApBillStatus.Posted).ToListAsync(cancellationToken);
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => p.VendorId == vendorId && p.PaidOn != null).ToListAsync(cancellationToken);
        var billNumbers = bills.ToDictionary(b => b.Id, b => b.Number);

        // Every movement in date order: bills (charges, net of retainage), payments, and voids (a payment given back).
        var events = new List<(DateOnly Date, int Order, string Kind, string Reference, string Description, decimal Charges, decimal Payments)>();
        foreach (var b in bills)
        {
            events.Add((b.InvoiceDate, 0, "Bill", b.Number, $"Invoice {b.InvoiceNumber}" + (b.RetainageAmount > 0 ? $" (retainage {b.RetainageAmount:N2} held)" : ""), b.Payable, 0));
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
        var threshold = options.Value.Form1099Threshold;
        var rows = payments.GroupBy(p => p.Vendor.Id).Select(g =>
        {
            var v = g.First().Vendor;
            return new Form1099RowDto(v.Id, v.Code, v.Name, PurchasingCatalog.MaskTaxId(v.TaxIdLast4), v.Form1099Box ?? PurchasingCatalog.DefaultForm1099Box, g.Sum(p => p.Amount), g.Count());
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
            .Where(b => b.Status == ApBillStatus.Posted && b.InvoiceDate >= from && b.InvoiceDate <= to)
            .Join(db.Set<Vendor>(), b => b.VendorId, v => v.Id, (b, v) => new { b.Amount, Vendor = v })
            .ToListAsync(cancellationToken);
        var total = bills.Sum(b => b.Amount);
        decimal Percent(decimal amount) => total == 0 ? 0 : Math.Round(amount * 100m / total, 1);

        var bySize = bills.GroupBy(b => string.IsNullOrWhiteSpace(b.Vendor.BusinessSize) ? "Not specified" : b.Vendor.BusinessSize!)
            .Select(g => new SmallBusinessRowDto(g.Key, g.Select(b => b.Vendor.Id).Distinct().Count(), g.Sum(b => b.Amount), Percent(g.Sum(b => b.Amount))))
            .OrderByDescending(r => r.Amount).ToList();

        // A vendor counts toward every category it holds, so these don't add up to the total.
        var bySocio = PurchasingCatalog.SocioEconomicCategories.Select(category =>
        {
            var matching = bills.Where(b => (b.Vendor.SocioEconomicCategories ?? "").Split(',', StringSplitOptions.TrimEntries).Contains(category, StringComparer.OrdinalIgnoreCase)).ToList();
            return new SmallBusinessRowDto(category, matching.Select(b => b.Vendor.Id).Distinct().Count(), matching.Sum(b => b.Amount), Percent(matching.Sum(b => b.Amount)));
        }).ToList();
        return new SmallBusinessReportDto(from, to, total, bySize, bySocio);
    }

    public async Task<PurchasingExportFile> ExportAgingCsvAsync(DateOnly asOf, CancellationToken cancellationToken = default)
    {
        var report = await GetAgingAsync(asOf, cancellationToken);
        var rows = new List<IEnumerable<object?>> { new object?[] { "VendorCode", "Vendor", "Current", "1-30", "31-60", "61-90", "Over 90", "Total", "Retainage" } };
        rows.AddRange(report.Rows.Select(r => new object?[] { r.VendorCode, r.VendorName, r.Current, r.Days1To30, r.Days31To60, r.Days61To90, r.Over90, r.Total, r.Retainage }));
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

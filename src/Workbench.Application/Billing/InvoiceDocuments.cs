using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Billing;

/// <summary>
/// The printable voucher (SF 1034 style or commercial) with its attachment of hours by employee and labor category and
/// ODC detail, and the WAWF/PIEE CSV.
/// </summary>
public sealed class InvoiceDocuments(IOptions<BillingOptions> options)
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal async Task<InvoicePrintDto> PrintAsync(IWorkbenchDbContext db, Invoice invoice, InvoiceDto detail, CancellationToken ct)
    {
        var ctx = await BillingContext.LoadAsync(db, invoice.ContractId, ct);
        var company = options.Value;
        var payee = new InvoicePartyDto(
            string.IsNullOrWhiteSpace(company.CompanyName) ? "Contractor (set Billing:CompanyName)" : company.CompanyName,
            company.RemitToAddress,
            string.Join(" · ", new[]
            {
                (ctx.Settings?.CageCode ?? company.CageCode) is { Length: > 0 } cage ? $"CAGE {cage}" : null,
                company.Uei is { Length: > 0 } uei ? $"UEI {uei}" : null,
            }.OfType<string>()));
        var payer = new InvoicePartyDto(ctx.Customer.Name, ctx.Profile?.BillingAddress, ctx.Customer.Code);

        var billed = (await InvoiceGenerator.PriorInvoicesAsync(db, invoice.ContractId, invoice.Id, approvedOnly: true, ct))
            .Where(i => !invoice.IsBilled || i.ApprovedAtUtc <= invoice.ApprovedAtUtc)
            .ToList();
        var clinIds = invoice.Lines.Select(l => l.ClinId).Distinct().ToList();
        var clinTotals = clinIds.Select(id =>
            {
                var clin = ctx.Clin(id);
                var mine = invoice.Lines.Where(l => l.ClinId == id).Sum(l => l.Amount);
                var before = billed.SelectMany(i => i.Lines).Where(l => l.ClinId == id).Sum(l => l.Amount);
                return new ClinTotalDto(clin?.Number, clin?.Description ?? "Not assigned to a CLIN", mine, before + mine, clin?.FundedValue ?? 0);
            })
            .OrderBy(c => c.ClinNumber, StringComparer.Ordinal)
            .ToList();

        var employees = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, ct);
        var categories = await db.Set<LaborCategory>().AsNoTracking().ToDictionaryAsync(c => c.Id, ct);
        string ClinOf(Guid? wbsId) => wbsId is { } w && ctx.Wbs.GetValueOrDefault(w)?.ClinId is { } c ? ctx.Clin(c)?.Number ?? "" : "";

        var sources = invoice.Lines.SelectMany(l => l.Sources.Select(s => (Line: l, Source: s))).ToList();
        List<LaborDetailRowDto> labor;
        List<OdcDetailRowDto> odcs;
        if (sources.Count > 0)
        {
            labor = sources.Where(x => x.Source.Kind == BilledSourceKind.TimeEntry)
                .GroupBy(x => (x.Source.EmployeeId, x.Source.LaborCategoryId, x.Line.ClinId, x.Source.Rate))
                .Select(g => new LaborDetailRowDto(
                    Name(employees, g.Key.EmployeeId), Number(employees, g.Key.EmployeeId), Category(categories, g.Key.LaborCategoryId),
                    ctx.Clin(g.Key.ClinId)?.Number, g.Sum(x => x.Source.Quantity), g.Key.Rate, g.Sum(x => x.Source.Amount)))
                .ToList();
            var odcIds = sources.Where(x => x.Source.Kind == BilledSourceKind.Odc).Select(x => x.Source.SourceId).ToList();
            var odcRows = await db.Set<OdcEntry>().AsNoTracking().Where(o => odcIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
            odcs = sources.Where(x => x.Source.Kind == BilledSourceKind.Odc)
                .Select(x => odcRows.TryGetValue(x.Source.SourceId, out var o)
                    ? new OdcDetailRowDto(o.IncurredOn, ctx.Clin(x.Line.ClinId)?.Number, o.Type.ToString(), o.Description, o.Vendor, x.Source.Amount)
                    : new OdcDetailRowDto(x.Source.Date, ctx.Clin(x.Line.ClinId)?.Number, x.Line.OdcType ?? "ODC", x.Source.Description ?? "", null, x.Source.Amount))
                .ToList();
        }
        else
        {
            // Cost-plus and true-up vouchers bill cost by WBS: attach the period's approved hours and allowable ODCs behind it.
            var wbsIds = invoice.Lines.Where(l => l.WbsElementId is not null).Select(l => l.WbsElementId!.Value).Distinct().ToList();
            var hours = await (
                    from e in db.Set<TimeEntry>().AsNoTracking()
                    join t in db.Set<Timesheet>().AsNoTracking() on e.TimesheetId equals t.Id
                    where t.Status == TimesheetStatus.Approved && wbsIds.Contains(e.WbsElementId) && e.WorkDate >= invoice.PeriodFrom && e.WorkDate <= invoice.PeriodTo
                    select new { t.EmployeeId, e.LaborCategoryId, e.WbsElementId, e.Hours })
                .ToListAsync(ct);
            labor = hours.GroupBy(h => (h.EmployeeId, h.LaborCategoryId, Clin: ClinOf(h.WbsElementId)))
                .Select(g => new LaborDetailRowDto(Name(employees, g.Key.EmployeeId), Number(employees, g.Key.EmployeeId),
                    Category(categories, g.Key.LaborCategoryId), g.Key.Clin, g.Sum(h => h.Hours), 0, 0))
                .ToList();
            odcs = (await db.Set<OdcEntry>().AsNoTracking()
                    .Where(o => wbsIds.Contains(o.WbsElementId) && o.Status == OdcStatus.Approved && !o.IsUnallowable
                        && o.IncurredOn >= invoice.PeriodFrom && o.IncurredOn <= invoice.PeriodTo)
                    .ToListAsync(ct))
                .Select(o => new OdcDetailRowDto(o.IncurredOn, ClinOf(o.WbsElementId), o.Type.ToString(), o.Description, o.Vendor, o.Amount))
                .ToList();
        }

        return new InvoicePrintDto(
            detail,
            ctx.Contract.ContractNumber,
            ctx.Contract.Name,
            BillingMapping.Label(ctx.Contract.Type),
            payee,
            payer,
            ctx.Settings?.PayingOffice,
            ctx.Settings?.AdminOffice,
            clinTotals,
            labor.OrderBy(l => l.ClinNumber, StringComparer.Ordinal).ThenBy(l => l.EmployeeName, StringComparer.Ordinal).ThenBy(l => l.LaborCategory, StringComparer.Ordinal).ToList(),
            odcs.OrderBy(o => o.Date).ThenBy(o => o.Description, StringComparer.Ordinal).ToList(),
            billed.Sum(i => i.Total) + (invoice.Status == InvoiceStatus.Void ? 0 : invoice.Total));
    }

    /// <summary>
    /// One header row (H) and one row per line item (L), in the fields WAWF/PIEE asks for on a 2-in-1, cost voucher or
    /// commercial invoice. Upload or key it in PIEE; there is no live integration.
    /// </summary>
    internal static BillingExportFile WawfCsv(InvoicePrintDto print, BillingContext ctx)
    {
        var inv = print.Invoice.Summary;
        var sb = new StringBuilder();
        sb.AppendLine("RecordType,InvoiceNumber,InvoiceDate,DocumentType,ContractNumber,CageCode,PayingOfficeDoDAAC,AdminOfficeDoDAAC,PeriodFrom,PeriodTo,LineItem,Description,Quantity,UnitOfMeasure,UnitPrice,Amount,InvoiceTotal");
        var docType = inv.Type switch
        {
            InvoiceKind.CostPlus or InvoiceKind.TrueUp => "Cost Voucher",
            InvoiceKind.FixedPriceMilestone => "2-in-1",
            InvoiceKind.CreditMemo => "Credit Memo",
            _ => "Commercial Item Financing / T&M Invoice",
        };
        var cage = ctx.Settings?.CageCode ?? "";
        Row(sb, "H", inv.Number, D(inv.InvoiceDate), docType, print.ContractNumber, cage, print.PayingOffice ?? "", print.AdminOffice ?? "",
            D(inv.PeriodFrom), D(inv.PeriodTo), "", $"{print.ContractName} ({print.ContractType})", "", "", "", "", M(inv.Total));
        foreach (var l in print.Invoice.Lines)
        {
            var hourly = l.Kind == InvoiceLineType.Labor && l.Rate != 0;
            Row(sb, "L", inv.Number, D(inv.InvoiceDate), docType, print.ContractNumber, cage, print.PayingOffice ?? "", print.AdminOffice ?? "",
                D(inv.PeriodFrom), D(inv.PeriodTo), l.ClinNumber ?? "", l.Description,
                hourly ? l.Quantity.ToString("0.00", Inv) : "1", hourly ? "HR" : "LO",
                hourly ? l.Rate.ToString("0.00##", Inv) : M(l.Amount), M(l.Amount), M(inv.Total));
        }

        return new BillingExportFile($"wawf-{inv.Number}.csv", "text/csv", sb.ToString());
    }

    private static string D(DateOnly d) => d.ToString("yyyy-MM-dd", Inv);

    private static string M(decimal m) => m.ToString("0.00", Inv);

    private static void Row(StringBuilder sb, params string[] cells) => sb.AppendLine(string.Join(",", cells.Select(Csv)));

    internal static string Csv(string value)
    {
        // Neutralize spreadsheet formulas and quote anything with separators.
        if (value.Length > 0 && "=+-@".Contains(value[0]) && !decimal.TryParse(value, NumberStyles.Number, Inv, out _))
        {
            value = "'" + value;
        }

        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    private static string Name(Dictionary<Guid, Employee> employees, Guid? id) => id is { } e && employees.TryGetValue(e, out var emp) ? emp.DisplayName : "Vendor";

    private static string Number(Dictionary<Guid, Employee> employees, Guid? id) => id is { } e && employees.TryGetValue(e, out var emp) ? emp.EmployeeNumber : "";

    private static string Category(Dictionary<Guid, LaborCategory> categories, Guid? id) => id is { } c && categories.TryGetValue(c, out var cat) ? $"{cat.Code} {cat.Name}" : "";
}

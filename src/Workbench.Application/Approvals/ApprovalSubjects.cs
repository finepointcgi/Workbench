using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Approvals;
using Workbench.Domain.Accounting;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Approvals;

/// <summary>How each module's documents look to the approval engine (submitter, amount, charges, link).</summary>
public static class ApprovalSubjects
{
    public static async Task<ApprovalSubject> TimesheetAsync(IWorkbenchDbContext db, Timesheet sheet, CancellationToken ct)
    {
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == sheet.EmployeeId, ct);
        var wbsIds = sheet.Entries.Select(e => e.WbsElementId).Distinct().ToList();
        var projects = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.ProjectId, ct);
        var hours = sheet.Entries.Sum(e => e.Hours);
        return new ApprovalSubject(ApprovalDocumentTypes.Timesheet, sheet.Id, $"{owner.DisplayName}: timesheet {sheet.PeriodStart:MMM d}–{sheet.PeriodEnd:MMM d}",
            owner.Email, $"time/history/{sheet.Id}", hours, $"{hours:0.0} h", owner.Id,
            sheet.Entries.GroupBy(e => projects.GetValueOrDefault(e.WbsElementId)).Select(g => new ApprovalCharge(g.Key == Guid.Empty ? null : g.Key, g.Sum(e => e.Hours), owner.OrgUnitId, "Labor")).ToList());
    }

    public static async Task<ApprovalSubject> ExpenseReportAsync(IWorkbenchDbContext db, ExpenseReport report, IReadOnlyList<OdcEntry> lines, CancellationToken ct)
    {
        var owner = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == report.EmployeeId, ct);
        var total = lines.Sum(l => l.Amount);
        return new ApprovalSubject(ApprovalDocumentTypes.ExpenseReport, report.Id, $"{owner.DisplayName}: {report.Title}",
            report.SubmittedBy ?? owner.Email, $"expenses/{report.Id}", total, $"{lines.Count} line(s)", owner.Id,
            lines.Select(l => Charge(l, owner.OrgUnitId)).ToList());
    }

    public static ApprovalSubject VendorOdc(OdcEntry entry) =>
        new(ApprovalDocumentTypes.VendorOdc, entry.Id, $"{entry.Vendor} {entry.InvoiceNumber}".Trim(), entry.SubmittedBy ?? "",
            $"projects/{entry.ProjectId}/odcs", entry.Amount, entry.Description, Charges: [Charge(entry, null)]);

    public static ApprovalSubject Requisition(PurchaseRequisition req, decimal financeThreshold) =>
        new(ApprovalDocumentTypes.Requisition, req.Id, $"Requisition {req.Number}", req.RequesterEmail, $"purchasing/requisitions/{req.Id}",
            req.Total, req.Justification, req.RequesterEmployeeId,
            req.Lines.Select(l => new ApprovalCharge(l.ProjectId, l.Amount, l.OrgUnitId ?? req.OrgUnitId, l.OdcType?.ToString(), IsAccount: l.ProjectId is null)).ToList(),
            financeThreshold);

    /// <summary>Project lines go to their PM; account lines are account charges. Whoever entered a linked ODC can't approve it.</summary>
    public static async Task<ApprovalSubject> ApBillAsync(IWorkbenchDbContext db, ApBill bill, CancellationToken ct)
    {
        var odcIds = bill.Lines.Where(l => l.OdcEntryId != null).Select(l => l.OdcEntryId!.Value).Distinct().ToList();
        var submitters = odcIds.Count == 0 ? [] : await db.Set<OdcEntry>().AsNoTracking().Where(o => odcIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, o => o.SubmittedBy, ct);
        var vendor = await db.Set<Vendor>().AsNoTracking().Where(v => v.Id == bill.VendorId).Select(v => v.Name).FirstOrDefaultAsync(ct);
        return new ApprovalSubject(ApprovalDocumentTypes.ApBill, bill.Id, $"Bill {bill.Number}: {vendor} {bill.InvoiceNumber}", bill.EnteredBy, $"purchasing/bills/{bill.Id}",
            bill.Amount, bill.Description, Charges: bill.Lines.Select(l =>
            {
                var account = l.Kind == ApBillLineKind.Account || l.ProjectId is null;
                var excluded = l.OdcEntryId is { } o && submitters.GetValueOrDefault(o) is { } by ? new[] { by } : [];
                return new ApprovalCharge(account ? null : l.ProjectId, l.Amount, l.OrgUnitId, l.OdcType?.ToString(), IsAccount: account, ExcludedEmails: excluded);
            }).ToList());
    }

    public static ApprovalSubject Invoice(Invoice invoice) =>
        new(ApprovalDocumentTypes.Invoice, invoice.Id, $"Invoice {invoice.Number}", invoice.CreatedBy, $"billing/invoices/{invoice.Id}", invoice.Total,
            $"{invoice.PeriodFrom:yyyy-MM-dd} to {invoice.PeriodTo:yyyy-MM-dd}");

    public static ApprovalSubject JournalEntry(JournalEntry entry) =>
        new(ApprovalDocumentTypes.JournalEntry, entry.Id, $"Journal entry {entry.Description}", entry.CreatedBy, $"accounting/journal/{entry.Id}",
            entry.Lines.Sum(l => l.Debit), entry.Description);

    private static ApprovalCharge Charge(OdcEntry l, Guid? orgUnitId) =>
        new(l.ProjectId, l.Amount, orgUnitId, l.Type.ToString(), l.IsUnallowable);
}

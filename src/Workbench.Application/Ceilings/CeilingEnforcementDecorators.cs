using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Application.Financials;
using Workbench.Application.Identity;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;
using Workbench.Domain.Billing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Ceilings;

// Enforcement points: each decorator checks closed contracts/projects and blocking ceilings before the inner call, and
// sends threshold alerts after it succeeds. They sit inside the permission decorators (registered above AddWorkbenchIdentity).

/// <summary>Timesheets: saving more hours past a blocking ceiling, or any hours on a closed contract or project, is refused.</summary>
public sealed class CeilingCheckingTimesheetService(ITimesheetService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard, AccessControl access) : ITimesheetService
{
    public Task<TimesheetDetailDto> GetOrCreateMyTimesheetAsync(DateOnly date, CancellationToken cancellationToken = default) => inner.GetOrCreateMyTimesheetAsync(date, cancellationToken);

    public Task<IReadOnlyList<TimesheetDto>> ListMyTimesheetsAsync(CancellationToken cancellationToken = default) => inner.ListMyTimesheetsAsync(cancellationToken);

    public Task<TimesheetDetailDto> GetAsync(Guid timesheetId, CancellationToken cancellationToken = default) => inner.GetAsync(timesheetId, cancellationToken);

    public async Task<TimesheetDetailDto> SaveEntriesAsync(Guid timesheetId, SaveTimeEntriesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        List<ChargeDelta> deltas = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken) is { } sheet
                && sheet.Status is TimesheetStatus.Draft or TimesheetStatus.Rejected
                && await OwnerAsync(db, sheet, cancellationToken) is { } owner)
            {
                var before = sheet.Entries.Where(e => request.Day is not { } day || e.WorkDate == day).ToList();
                var assignments = await db.Set<ChargeAssignment>().AsNoTracking().Where(a => a.EmployeeId == owner.Id).ToListAsync(cancellationToken);
                var rates = await CostRatesAsync(db, owner.Id, cancellationToken);
                var rows = before.Select(e => (e.WbsElementId, (Guid?)e.LaborCategoryId, -e.Hours, e.WorkDate)).ToList();
                foreach (var input in (request.Entries ?? []).Where(i => i.Hours != 0))
                {
                    var existing = input.EntryId is { } id ? sheet.Entries.FirstOrDefault(e => e.Id == id)
                        : sheet.Entries.FirstOrDefault(e => e.WbsElementId == input.WbsElementId && e.WorkDate == input.WorkDate);
                    var category = input.LaborCategoryId
                        ?? (existing?.WbsElementId == input.WbsElementId ? existing?.LaborCategoryId : null)
                        ?? assignments.FirstOrDefault(a => a.WbsElementId == input.WbsElementId && a.LaborCategoryId != null)?.LaborCategoryId
                        ?? owner.DefaultLaborCategoryId;
                    rows.Add((input.WbsElementId, category, input.Hours, input.WorkDate));
                }

                deltas = rows.GroupBy(r => (r.Item1, r.Item2))
                    .Select(g => new ChargeDelta(g.Key.Item1, g.Key.Item2, g.Sum(r => r.Item3), Money.Round(g.Sum(r => r.Item3 * (rates.Find(owner.Id, r.WorkDate) ?? 0m)))))
                    .ToList();
            }
        }

        await guard.CheckChargesAsync(deltas, "this time", cancellationToken);
        var result = await inner.SaveEntriesAsync(timesheetId, request, cancellationToken);
        if (deltas.Any(d => d.Hours > 0))
        {
            await guard.AlertForWbsAsync(deltas.Where(d => d.Hours > 0).Select(d => d.WbsElementId), cancellationToken);
        }

        return result;
    }

    public async Task<SubmitTimesheetResult> SubmitAsync(Guid timesheetId, SubmitTimesheetRequest request, CancellationToken cancellationToken = default)
    {
        List<string> warnings = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<Timesheet>().AsNoTracking().Include(t => t.Entries).FirstOrDefaultAsync(t => t.Id == timesheetId, cancellationToken) is { } sheet
                && await OwnerAsync(db, sheet, cancellationToken) is not null)
            {
                var own = sheet.Entries.GroupBy(e => (e.WbsElementId, e.LaborCategoryId))
                    .Select(g => new ChargeDelta(g.Key.WbsElementId, g.Key.LaborCategoryId, g.Sum(e => e.Hours), 0)).ToList();
                warnings = await guard.CheckStandingAsync(own, "this timesheet", cancellationToken);
            }
        }

        var result = await inner.SubmitAsync(timesheetId, request, cancellationToken);
        return warnings.Count == 0 ? result : result with { Warnings = [.. result.Warnings, .. warnings] };
    }

    public Task<TimesheetDetailDto> ReopenAsync(Guid timesheetId, string reason, CancellationToken cancellationToken = default) => inner.ReopenAsync(timesheetId, reason, cancellationToken);

    /// <summary>Only the owner's own saves are checked here; anyone else is refused by the timesheet service itself.</summary>
    private async Task<Employee?> OwnerAsync(IWorkbenchDbContext db, Timesheet sheet, CancellationToken ct) =>
        await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == sheet.EmployeeId, ct) is { } e
            && string.Equals(AccessControl.Normalize(e.Email), access.Email, StringComparison.Ordinal) ? e : null;

    private static async Task<EffectiveRateTable<Guid>> CostRatesAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken ct)
    {
        var table = new EffectiveRateTable<Guid>();
        foreach (var r in await db.Set<EmployeeCostRate>().AsNoTracking().Where(r => r.EmployeeId == employeeId).ToListAsync(ct))
        {
            table.Add(r.EmployeeId, r.EffectiveFrom, r.HourlyCost);
        }

        return table;
    }
}

/// <summary>Vendor ODCs: none on a closed contract or project; submitting past a blocking cost ceiling is refused.</summary>
public sealed class CeilingCheckingOdcService(IOdcService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IOdcService
{
    public Task<OdcListDto> ListProjectOdcsAsync(Guid projectId, OdcListQuery? query = null, CancellationToken cancellationToken = default) => inner.ListProjectOdcsAsync(projectId, query, cancellationToken);

    public Task<OdcEntryDto?> GetAsync(Guid odcId, CancellationToken cancellationToken = default) => inner.GetAsync(odcId, cancellationToken);

    public async Task<OdcEntryDto> CreateVendorOdcAsync(Guid projectId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync([request.WbsElementId], "this ODC", cancellationToken);
        return await inner.CreateVendorOdcAsync(projectId, request, cancellationToken);
    }

    public async Task<OdcEntryDto> UpdateVendorOdcAsync(Guid odcId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync([request.WbsElementId], "this ODC", cancellationToken);
        return await inner.UpdateVendorOdcAsync(odcId, request, cancellationToken);
    }

    public Task DeleteVendorOdcAsync(Guid odcId, CancellationToken cancellationToken = default) => inner.DeleteVendorOdcAsync(odcId, cancellationToken);

    public Task<OdcEntryDto> AttachReceiptAsync(Guid odcId, string fileName, Stream content, CancellationToken cancellationToken = default) => inner.AttachReceiptAsync(odcId, fileName, content, cancellationToken);

    public Task<ReceiptContent> OpenReceiptAsync(Guid odcId, CancellationToken cancellationToken = default) => inner.OpenReceiptAsync(odcId, cancellationToken);

    public string GetReceiptUrl(Guid odcId) => inner.GetReceiptUrl(odcId);

    public async Task<OdcEntryDto> SubmitAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        List<ChargeDelta> deltas = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<OdcEntry>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken) is { Status: OdcStatus.Draft or OdcStatus.Rejected } odc)
            {
                deltas = [new ChargeDelta(odc.WbsElementId, null, 0, odc.IsUnallowable ? 0 : odc.Amount)];
                await guard.EnsureOpenAsync(db, [odc.WbsElementId], "this ODC", cancellationToken);
            }
        }

        await guard.CheckChargesAsync(deltas, "this ODC", cancellationToken);
        var result = await inner.SubmitAsync(odcId, cancellationToken);
        await guard.AlertForWbsAsync(deltas.Select(d => d.WbsElementId), cancellationToken);
        return result;
    }

    public Task<ExpensePolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default) => inner.GetPolicyAsync(cancellationToken);
}

/// <summary>Expense reports: no lines on closed contracts or projects; submitting past a blocking cost ceiling is refused.</summary>
public sealed class CeilingCheckingExpenseReportService(IExpenseReportService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IExpenseReportService
{
    public Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default) => inner.ListMyReportsAsync(cancellationToken);

    public Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.GetReportAsync(reportId, cancellationToken);

    public Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default) => inner.CreateReportAsync(request, cancellationToken);

    public Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default) => inner.UpdateReportAsync(reportId, request, cancellationToken);

    public Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default) => inner.DeleteReportAsync(reportId, cancellationToken);

    public async Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync([request.WbsElementId], "this expense", cancellationToken);
        return await inner.AddLineAsync(reportId, request, cancellationToken);
    }

    public async Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync([request.WbsElementId], "this expense", cancellationToken);
        return await inner.UpdateLineAsync(reportId, lineId, request, cancellationToken);
    }

    public Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default) => inner.RemoveLineAsync(reportId, lineId, cancellationToken);

    public Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default) =>
        inner.AttachReceiptAsync(reportId, lineId, fileName, content, cancellationToken);

    public async Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        List<ChargeDelta> deltas;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            deltas = (await db.Set<OdcEntry>().AsNoTracking()
                    .Where(o => o.ExpenseReportId == reportId && (o.Status == OdcStatus.Draft || o.Status == OdcStatus.Rejected))
                    .Select(o => new { o.WbsElementId, o.Amount, o.IsUnallowable }).ToListAsync(cancellationToken))
                .GroupBy(o => o.WbsElementId).Select(g => new ChargeDelta(g.Key, null, 0, g.Where(o => !o.IsUnallowable).Sum(o => o.Amount))).ToList();
            await guard.EnsureOpenAsync(db, deltas.Select(d => d.WbsElementId), "this expense report", cancellationToken);
        }

        await guard.CheckChargesAsync(deltas, "this expense report", cancellationToken);
        var result = await inner.SubmitAsync(reportId, cancellationToken);
        await guard.AlertForWbsAsync(deltas.Select(d => d.WbsElementId), cancellationToken);
        return result;
    }
}

/// <summary>AP bills: no project lines on closed contracts or projects; approving lines past a blocking cost ceiling is refused.</summary>
public sealed class CeilingCheckingApBillService(IApBillService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IApBillService
{
    public Task<IReadOnlyList<BillSummaryDto>> ListAsync(BillQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

    public Task<BillDto> GetAsync(Guid billId, CancellationToken cancellationToken = default) => inner.GetAsync(billId, cancellationToken);

    public Task<IReadOnlyList<BillDuplicateDto>> CheckDuplicatesAsync(DuplicateCheckRequest request, CancellationToken cancellationToken = default) => inner.CheckDuplicatesAsync(request, cancellationToken);

    public async Task<BillDto> CreateAsync(SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync(WbsOf(request), "this bill", cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<BillDto> UpdateAsync(Guid billId, SaveBillRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync(WbsOf(request), "this bill", cancellationToken);
        return await inner.UpdateAsync(billId, request, cancellationToken);
    }

    public Task DeleteDraftAsync(Guid billId, CancellationToken cancellationToken = default) => inner.DeleteDraftAsync(billId, cancellationToken);

    public async Task<BillDto> SubmitAsync(Guid billId, SubmitBillRequest request, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            await guard.EnsureOpenAsync(db, await LineWbsAsync(db, billId, cancellationToken), "this bill", cancellationToken);
        }

        return await inner.SubmitAsync(billId, request, cancellationToken);
    }

    public Task<IReadOnlyList<BillApprovalItemDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default) => inner.ListApprovalQueueAsync(cancellationToken);

    public async Task<BillDto> ApproveAsync(Guid billId, CancellationToken cancellationToken = default)
    {
        List<ChargeDelta> deltas;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var lines = await db.Set<ApBillLine>().AsNoTracking()
                .Where(l => l.BillId == billId && l.Status == ApLineStatus.Pending && l.WbsElementId != null)
                .Select(l => new { WbsElementId = l.WbsElementId!.Value, l.Amount, l.OdcEntryId, l.PurchaseOrderLineId })
                .ToListAsync(cancellationToken);
            await guard.EnsureOpenAsync(db, lines.Select(l => l.WbsElementId), "this bill", cancellationToken);

            // Lines linked to an ODC already count; PO lines move cost from the commitment to actuals (no net change).
            // A credit or debit memo takes cost off, so it never pushes past a ceiling.
            var credit = await db.Set<ApBill>().AsNoTracking().AnyAsync(b => b.Id == billId && (b.Kind == ApDocumentKind.CreditMemo || b.Kind == ApDocumentKind.DebitMemo), cancellationToken);
            deltas = credit ? [] : lines.Where(l => l.OdcEntryId == null && l.PurchaseOrderLineId == null).GroupBy(l => l.WbsElementId)
                .Select(g => new ChargeDelta(g.Key, null, 0, g.Sum(l => l.Amount))).ToList();
        }

        await guard.CheckChargesAsync(deltas, "this bill", cancellationToken);
        var result = await inner.ApproveAsync(billId, cancellationToken);
        await guard.AlertForWbsAsync(deltas.Select(d => d.WbsElementId), cancellationToken);
        return result;
    }

    public Task<BillDto> RejectAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default) => inner.RejectAsync(billId, request, cancellationToken);

    public Task<BillDto> PostAsync(Guid billId, CancellationToken cancellationToken = default) => inner.PostAsync(billId, cancellationToken);

    public Task<BillDto> VoidAsync(Guid billId, BillReasonRequest request, CancellationToken cancellationToken = default) => inner.VoidAsync(billId, request, cancellationToken);

    public Task<IReadOnlyList<LinkableOdcDto>> ListLinkableOdcsAsync(Guid vendorId, CancellationToken cancellationToken = default) => inner.ListLinkableOdcsAsync(vendorId, cancellationToken);

    public Task<IReadOnlyList<OdcBillLinkDto>> GetOdcLinksAsync(IReadOnlyCollection<Guid> odcEntryIds, CancellationToken cancellationToken = default) => inner.GetOdcLinksAsync(odcEntryIds, cancellationToken);

    private static IEnumerable<Guid> WbsOf(SaveBillRequest request) =>
        (request?.Lines ?? []).Where(l => l.WbsElementId != null).Select(l => l.WbsElementId!.Value);

    private static async Task<List<Guid>> LineWbsAsync(IWorkbenchDbContext db, Guid billId, CancellationToken ct) =>
        await db.Set<ApBillLine>().AsNoTracking().Where(l => l.BillId == billId && l.WbsElementId != null).Select(l => l.WbsElementId!.Value).ToListAsync(ct);
}

/// <summary>Purchase orders: none on closed contracts or projects; issuing or changing past a blocking cost ceiling (commitments count) is refused.</summary>
public sealed class CeilingCheckingPurchaseOrderService(IPurchaseOrderService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IPurchaseOrderService
{
    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

    public Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.GetAsync(purchaseOrderId, cancellationToken);

    public Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default) => inner.ListOrderableRequisitionLinesAsync(cancellationToken);

    public async Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync(WbsOf(request?.Lines), "this purchase order", cancellationToken);
        return await inner.CreateAsync(request!, cancellationToken);
    }

    public Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default) =>
        inner.CreateFromRequisitionsAsync(request, cancellationToken);

    public async Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await guard.EnsureOpenAsync(WbsOf(request?.Lines), "this purchase order", cancellationToken);
        return await inner.UpdateAsync(purchaseOrderId, request!, cancellationToken);
    }

    public async Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        List<ChargeDelta> deltas = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (await db.Set<PurchaseOrder>().AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == purchaseOrderId, cancellationToken) is { Status: PurchaseOrderStatus.Draft } po)
            {
                deltas = po.Lines.Where(l => l.WbsElementId != null).GroupBy(l => l.WbsElementId!.Value)
                    .Select(g => new ChargeDelta(g.Key, null, 0, g.Sum(l => l.Amount))).ToList();
                await guard.EnsureOpenAsync(db, deltas.Select(d => d.WbsElementId), "this purchase order", cancellationToken);
            }
        }

        await guard.CheckChargesAsync(deltas, "this purchase order", cancellationToken);
        var result = await inner.IssueAsync(purchaseOrderId, cancellationToken);
        await guard.AlertForWbsAsync(deltas.Select(d => d.WbsElementId), cancellationToken);
        return result;
    }

    public async Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        List<ChargeDelta> deltas = [];
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            if (request?.Lines is { } lines
                && await db.Set<PurchaseOrder>().AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == purchaseOrderId, cancellationToken) is { } po)
            {
                var before = po.Lines.Where(l => l.WbsElementId != null).Select(l => (Wbs: l.WbsElementId!.Value, Amount: -l.Amount));
                var after = lines.Where(l => l.WbsElementId != null).Select(l => (Wbs: l.WbsElementId!.Value, Amount: Money.Round(l.Quantity * l.UnitPrice)));
                deltas = before.Concat(after).GroupBy(x => x.Wbs).Select(g => new ChargeDelta(g.Key, null, 0, g.Sum(x => x.Amount))).ToList();
            }
        }

        await guard.CheckChargesAsync(deltas, "this change order", cancellationToken);
        var result = await inner.ChangeOrderAsync(purchaseOrderId, request!, cancellationToken);
        await guard.AlertForWbsAsync(deltas.Where(d => d.Cost > 0).Select(d => d.WbsElementId), cancellationToken);
        return result;
    }

    public Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default) => inner.ReceiveAsync(purchaseOrderId, request, cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default) => inner.ListReceivableAsync(cancellationToken);

    public Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.CloseAsync(purchaseOrderId, cancellationToken);

    public Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default) => inner.CancelAsync(purchaseOrderId, request, cancellationToken);

    private static IEnumerable<Guid> WbsOf(IReadOnlyList<SavePurchaseOrderLineRequest>? lines) =>
        (lines ?? []).Where(l => l.WbsElementId != null).Select(l => l.WbsElementId!.Value);
}

/// <summary>
/// Invoices: generating, regenerating, adding or removing lines on, or approving a draft that would bill past a blocking contract
/// or CLIN billing ceiling is refused. Drafts don't count as billed, so each check takes the whole draft as it would stand.
/// </summary>
public sealed class CeilingCheckingInvoiceService(IInvoiceService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IInvoiceService
{
    public Task<IReadOnlyList<InvoiceSummaryDto>> ListAsync(InvoiceQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

    public Task<InvoiceDto> GetAsync(Guid invoiceId, CancellationToken cancellationToken = default) => inner.GetAsync(invoiceId, cancellationToken);

    public Task<IReadOnlyList<BillingContractDto>> ListBillingContractsAsync(CancellationToken cancellationToken = default) => inner.ListBillingContractsAsync(cancellationToken);

    public async Task<InvoicePreviewDto> PreviewAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var preview = await inner.PreviewAsync(request, cancellationToken);
        try
        {
            var warnings = await guard.CheckBillingAsync(request.ContractId, Deltas(preview.Lines), "this invoice", cancellationToken);
            return warnings.Count == 0 ? preview : preview with { Warnings = [.. preview.Warnings, .. warnings] };
        }
        catch (Workbench.Contracts.Ceilings.CeilingExceededException ex)
        {
            return preview with { Warnings = [.. preview.Warnings, ex.Message] };
        }
    }

    public async Task<InvoiceDto> GenerateDraftAsync(GenerateInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var preview = await inner.PreviewAsync(request, cancellationToken);
        await guard.CheckBillingAsync(request.ContractId, Deltas(preview.Lines), "this invoice", cancellationToken);
        return await inner.GenerateDraftAsync(request, cancellationToken);
    }

    public async Task<InvoiceDto> RegenerateDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default)
    {
        Invoice? draft;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            draft = await db.Set<Invoice>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == invoiceId, cancellationToken);
        }

        if (draft is { Status: InvoiceStatus.Draft } && draft.Type != InvoiceType.CreditMemo)
        {
            // The preview finds this draft (same contract, type and period) and keeps its manual lines, as regenerating does.
            var preview = await inner.PreviewAsync(new GenerateInvoiceRequest(draft.ContractId, draft.Type.ToContract(), draft.PeriodFrom, draft.PeriodTo,
                ReleaseWithhold: draft.ReleasesWithhold), cancellationToken);
            await guard.CheckBillingAsync(draft.ContractId, Deltas(preview.Lines), "this invoice", cancellationToken);
        }

        return await inner.RegenerateDraftAsync(invoiceId, cancellationToken);
    }

    public Task<InvoiceDto> UpdateDraftAsync(Guid invoiceId, UpdateInvoiceDraftRequest request, CancellationToken cancellationToken = default) => inner.UpdateDraftAsync(invoiceId, request, cancellationToken);

    public async Task<InvoiceDto> AddLineAsync(Guid invoiceId, AddInvoiceLineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Amount > 0)
        {
            var invoice = await inner.GetAsync(invoiceId, cancellationToken);
            if (invoice.Summary.Status == InvoiceState.Draft)
            {
                await guard.CheckBillingAsync(invoice.Summary.ContractId,
                    [.. Deltas(invoice.Lines), new BillingDelta(request.ClinId, Math.Round(request.Amount, 2, MidpointRounding.AwayFromZero))], "this line", cancellationToken);
            }
        }

        return await inner.AddLineAsync(invoiceId, request, cancellationToken);
    }

    /// <summary>Removing a credit (negative) line raises what the draft bills, so it's checked too.</summary>
    public async Task<InvoiceDto> RemoveLineAsync(Guid invoiceId, Guid lineId, CancellationToken cancellationToken = default)
    {
        var invoice = await inner.GetAsync(invoiceId, cancellationToken);
        if (invoice.Summary.Status == InvoiceState.Draft && invoice.Lines.FirstOrDefault(l => l.Id == lineId) is { Amount: < 0 })
        {
            await guard.CheckBillingAsync(invoice.Summary.ContractId, Deltas(invoice.Lines.Where(l => l.Id != lineId)), "removing this line", cancellationToken);
        }

        return await inner.RemoveLineAsync(invoiceId, lineId, cancellationToken);
    }

    public Task DeleteDraftAsync(Guid invoiceId, CancellationToken cancellationToken = default) => inner.DeleteDraftAsync(invoiceId, cancellationToken);

    public async Task<InvoiceDto> ApproveAsync(Guid invoiceId, ApproveInvoiceRequest request, CancellationToken cancellationToken = default)
    {
        var invoice = await inner.GetAsync(invoiceId, cancellationToken);
        if (invoice.Summary.Status == InvoiceState.Draft)
        {
            await guard.CheckBillingAsync(invoice.Summary.ContractId, Deltas(invoice.Lines), "this invoice", cancellationToken);
        }

        var result = await inner.ApproveAsync(invoiceId, request, cancellationToken);
        await guard.AlertForContractAsync(invoice.Summary.ContractId, cancellationToken);
        return result;
    }

    public Task<InvoiceDto> MarkSentAsync(Guid invoiceId, CancellationToken cancellationToken = default) => inner.MarkSentAsync(invoiceId, cancellationToken);

    public Task<InvoiceDto> VoidAsync(Guid invoiceId, VoidInvoiceRequest request, CancellationToken cancellationToken = default) => inner.VoidAsync(invoiceId, request, cancellationToken);

    public Task<InvoiceDto> CreateCreditMemoAsync(Guid invoiceId, CreditMemoRequest request, CancellationToken cancellationToken = default) => inner.CreateCreditMemoAsync(invoiceId, request, cancellationToken);

    public Task<InvoicePrintDto> GetPrintAsync(Guid invoiceId, CancellationToken cancellationToken = default) => inner.GetPrintAsync(invoiceId, cancellationToken);

    public Task<BillingExportFile> ExportWawfCsvAsync(Guid invoiceId, CancellationToken cancellationToken = default) => inner.ExportWawfCsvAsync(invoiceId, cancellationToken);

    public Task<ContractBillingSummaryDto> GetContractSummaryAsync(Guid contractId, CancellationToken cancellationToken = default) => inner.GetContractSummaryAsync(contractId, cancellationToken);

    public Task<ContractBillingSettingsDto> GetSettingsAsync(Guid contractId, CancellationToken cancellationToken = default) => inner.GetSettingsAsync(contractId, cancellationToken);

    public Task<ContractBillingSettingsDto> SaveSettingsAsync(Guid contractId, SaveContractBillingSettingsRequest request, CancellationToken cancellationToken = default) =>
        inner.SaveSettingsAsync(contractId, request, cancellationToken);

    public Task<UnbilledAccrualDto> GetUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.GetUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);

    public Task<UnbilledAccrualDto> PostUnbilledAccrualAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) => inner.PostUnbilledAccrualAsync(fiscalPeriodId, cancellationToken);

    private static List<BillingDelta> Deltas(IEnumerable<InvoiceLineDto> lines) => lines.Select(l => new BillingDelta(l.ClinId, l.Amount)).ToList();
}

/// <summary>
/// Requisitions: submitting and approving are checked against cost ceilings as if the requisition were ordered. They warn by
/// default (the purchase order is the commitment, checked when issued); a blocking ceiling set to check at requisition refuses
/// them. Closed contracts and projects refuse them too. Open requisitions carry the warnings for their approvers.
/// </summary>
public sealed class CeilingCheckingRequisitionService(IRequisitionService inner, IWorkbenchDbContextFactory dbFactory, ChargeGuard guard) : IRequisitionService
{
    private const string What = "this requisition";

    public Task<IReadOnlyList<RequisitionSummaryDto>> ListAsync(RequisitionQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

    public async Task<RequisitionDto> GetAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        var result = await inner.GetAsync(requisitionId, cancellationToken);
        if (result.Requisition.Status is not (RequisitionState.Draft or RequisitionState.Rejected or RequisitionState.PendingApproval))
        {
            return result;
        }

        List<string> warnings;
        try
        {
            warnings = await guard.CheckRequisitionAsync(await DeltasAsync(requisitionId, cancellationToken), What, cancellationToken);
        }
        catch (Exception ex) when (ex is Workbench.Contracts.Ceilings.CeilingExceededException or Workbench.Contracts.Ceilings.ChargingClosedException)
        {
            warnings = [ex.Message];
        }

        return WithWarnings(result, warnings);
    }

    public Task<RequisitionOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default) => inner.GetOptionsAsync(cancellationToken);

    public Task<RequisitionDto> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken = default) => inner.CreateAsync(request, cancellationToken);

    public Task<RequisitionDto> UpdateAsync(Guid requisitionId, SaveRequisitionRequest request, CancellationToken cancellationToken = default) => inner.UpdateAsync(requisitionId, request, cancellationToken);

    public async Task<RequisitionDto> SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default)
    {
        var warnings = await guard.CheckRequisitionAsync(await DeltasAsync(requisitionId, cancellationToken, RequisitionStatus.Draft, RequisitionStatus.Rejected), What, cancellationToken);
        return WithWarnings(await inner.SubmitAsync(requisitionId, cancellationToken), warnings);
    }

    public Task<IReadOnlyList<RequisitionSummaryDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default) => inner.ListApprovalQueueAsync(cancellationToken);

    public async Task<RequisitionDto> ApproveAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default)
    {
        var warnings = await guard.CheckRequisitionAsync(await DeltasAsync(requisitionId, cancellationToken, RequisitionStatus.PendingApproval), What, cancellationToken);
        return WithWarnings(await inner.ApproveAsync(requisitionId, request, cancellationToken), warnings);
    }

    public Task<RequisitionDto> RejectAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default) => inner.RejectAsync(requisitionId, request, cancellationToken);

    public Task<RequisitionDto> CancelAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default) => inner.CancelAsync(requisitionId, request, cancellationToken);

    /// <summary>The cost the requisition's project lines would commit, by WBS element (lines already on a purchase order count there).</summary>
    private async Task<List<ChargeDelta>> DeltasAsync(Guid requisitionId, CancellationToken ct, params RequisitionStatus[] statuses)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var req = await db.Set<PurchaseRequisition>().AsNoTracking().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == requisitionId, ct);
        if (req is null || (statuses.Length > 0 && !statuses.Contains(req.Status)))
        {
            return [];
        }

        return req.Lines.Where(l => l.WbsElementId != null && l.PurchaseOrderLineId == null).GroupBy(l => l.WbsElementId!.Value)
            .Select(g => new ChargeDelta(g.Key, null, 0, g.Sum(l => l.Amount))).ToList();
    }

    private static RequisitionDto WithWarnings(RequisitionDto dto, List<string> warnings) =>
        warnings.Count == 0 ? dto : dto with { CeilingWarnings = [.. dto.CeilingWarnings ?? [], .. warnings] };
}

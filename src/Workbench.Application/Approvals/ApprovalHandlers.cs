using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Identity;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Application.Approvals;

// Inbox decisions go through each module's own (permission-checked, posting-decorated) service. Services are resolved
// when used, so the inbox doesn't build every module's graph to list tasks.

public sealed class TimesheetApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.Timesheet;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<ITimesheetApprovalService>().ApproveAsync(documentId, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<ITimesheetApprovalService>().RejectAsync(documentId, reason, ct);
}

public sealed class ExpenseReportApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.ExpenseReport;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IOdcApprovalService>().ApproveReportAsync(documentId, comment, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IOdcApprovalService>().RejectReportAsync(documentId, reason, ct);
}

public sealed class VendorOdcApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.VendorOdc;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IOdcApprovalService>().ApproveOdcAsync(documentId, comment, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IOdcApprovalService>().RejectOdcAsync(documentId, reason, ct);
}

public sealed class RequisitionApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.Requisition;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IRequisitionService>().ApproveAsync(documentId, new RequisitionDecisionRequest(comment), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IRequisitionService>().RejectAsync(documentId, new RequisitionDecisionRequest(reason), ct);
}

public sealed class ApBillApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.ApBill;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IApBillService>().ApproveAsync(documentId, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        services.GetRequiredService<IApBillService>().RejectAsync(documentId, new BillReasonRequest(reason), ct);
}

/// <summary>
/// Invoices and manual journal entries have no "rejected" state of their own: approving runs the module (approve and
/// post); rejecting ends the workflow trip and leaves the draft for Finance to correct.
/// </summary>
public sealed class InvoiceApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.Invoice;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IInvoiceService>().ApproveAsync(documentId, new ApproveInvoiceRequest(), ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        EngineOnly.RejectAsync(services, DocumentType, documentId, reason, ct);
}

public sealed class JournalEntryApprovalHandler(IServiceProvider services) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.JournalEntry;

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) =>
        services.GetRequiredService<IJournalService>().PostAsync(documentId, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) =>
        EngineOnly.RejectAsync(services, DocumentType, documentId, reason, ct);
}

internal static class EngineOnly
{
    public static async Task RejectAsync(IServiceProvider services, string documentType, Guid documentId, string reason, CancellationToken ct)
    {
        var engine = services.GetRequiredService<ApprovalEngine>();
        var access = services.GetRequiredService<AccessControl>();
        await using var db = await services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(ct);
        var check = await engine.CheckAsync(db, documentType, documentId, access.Email, null, ct);
        if (!check.CanDecide)
        {
            throw new Contracts.WorkbenchForbiddenException("Nothing on this document is waiting on you.");
        }

        await engine.DecideAsync(db, check, ApprovalDecisionKind.Reject, access.Email, reason, ct);
        await db.SaveChangesAsync(ct);
    }
}

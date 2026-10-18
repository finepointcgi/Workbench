using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Odcs;

public sealed class OdcService(IWorkbenchDbContextFactory dbFactory, OdcWorkflow workflow) : IOdcService
{
    public async Task<OdcListDto> ListProjectOdcsAsync(Guid projectId, OdcListQuery? query = null, CancellationToken cancellationToken = default)
    {
        query ??= new OdcListQuery();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var q = db.Set<OdcEntry>().AsNoTracking().Where(o => o.ProjectId == projectId);
        if (!query.IncludeExpenses)
        {
            q = q.Where(o => o.ExpenseReportId == null);
        }

        if (query.From is { } from)
        {
            q = q.Where(o => o.IncurredOn >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(o => o.IncurredOn <= to);
        }

        if (query.Type is { } type)
        {
            var t = (OdcType)(int)type;
            q = q.Where(o => o.Type == t);
        }

        if (query.Status is { } status)
        {
            var s = (OdcStatus)(int)status;
            q = q.Where(o => o.Status == s);
        }

        var entries = await q.OrderByDescending(o => o.IncurredOn).ThenByDescending(o => o.CreatedAtUtc).ToListAsync(cancellationToken);
        return OdcTotals.Summarize(await OdcWorkflow.ToDtosAsync(db, entries, cancellationToken));
    }

    public async Task<OdcEntryDto?> GetAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<OdcEntry>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken);
        return entry is null ? null : await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<OdcEntryDto> CreateVendorOdcAsync(Guid projectId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == projectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", projectId);
        }

        var entry = new OdcEntry { Description = "", SubmittedBy = workflow.Actor, CreatedAtUtc = workflow.UtcNow, ProjectId = projectId };
        await ApplyAsync(db, entry, request, cancellationToken);
        db.Set<OdcEntry>().Add(entry);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Created", after: OdcWorkflow.Snapshot(entry));
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<OdcEntryDto> UpdateVendorOdcAsync(Guid odcId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await FindVendorAsync(db, odcId, cancellationToken);
        OdcWorkflow.EnsureEditable(entry.Status, "ODC");
        var before = OdcWorkflow.Snapshot(entry);
        await ApplyAsync(db, entry, request, cancellationToken);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Updated", before, OdcWorkflow.Snapshot(entry));
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task DeleteVendorOdcAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await FindVendorAsync(db, odcId, cancellationToken);
        OdcWorkflow.EnsureEditable(entry.Status, "ODC");
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Deleted", before: OdcWorkflow.Snapshot(entry));
        db.Set<OdcEntry>().Remove(entry);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OdcEntryDto> AttachReceiptAsync(Guid odcId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (entry.ExpenseReportId is { } reportId)
        {
            var report = await db.Set<ExpenseReport>().FirstAsync(r => r.Id == reportId, cancellationToken);
            await ExpenseReportService.EnsureOwnerAsync(db, workflow, report, cancellationToken);
            OdcWorkflow.EnsureEditable(report.Status, "expense report");
        }
        else
        {
            OdcWorkflow.EnsureEditable(entry.Status, "ODC");
        }

        return await AttachAsync(db, workflow, entry, fileName, content, cancellationToken);
    }

    internal static async Task<OdcEntryDto> AttachAsync(IWorkbenchDbContext db, OdcWorkflow workflow, OdcEntry entry, string fileName, Stream content, CancellationToken cancellationToken)
    {
        var file = await workflow.Files.SaveAsync(db, OdcWorkflow.OdcEntityType, entry.Id, fileName, content, cancellationToken);
        try
        {
            var previous = entry.ReceiptAttachmentId;
            entry.ReceiptAttachmentId = file.Id;
            workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "ReceiptAttached",
                before: previous is null ? null : new { ReceiptFileId = previous },
                after: new { ReceiptFileId = file.Id, file.FileName, file.SizeBytes });
            await db.SaveChangesAsync(cancellationToken);
        }
        catch
        {
            await workflow.Files.DiscardAsync(file);
            throw;
        }

        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<ReceiptContent> OpenReceiptAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var fileId = await db.Set<OdcEntry>().Where(o => o.Id == odcId).Select(o => (Guid?)o.ReceiptAttachmentId).FirstOrDefaultAsync(cancellationToken)
            ?? throw new WorkbenchNotFoundException($"ODC {odcId} has no receipt.");
        var (file, stream) = await workflow.Files.OpenAsync(db, fileId, cancellationToken);
        return new ReceiptContent(file.FileName, file.ContentType, stream);
    }

    public string GetReceiptUrl(Guid odcId) => $"api/v1/odcs/{odcId}/receipt";

    public async Task<OdcEntryDto> SubmitAsync(Guid odcId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await FindVendorAsync(db, odcId, cancellationToken);
        OdcWorkflow.EnsureEditable(entry.Status, "ODC");
        await workflow.ValidateForSubmitAsync(db, entry, cancellationToken);
        var before = entry.Status;
        entry.Status = OdcStatus.Submitted;
        entry.SubmittedAtUtc = workflow.UtcNow;
        entry.RejectionReason = null;
        entry.RejectedBy = null;
        entry.RejectedAtUtc = null;
        // The workflow (Setup › Approval workflows; by default the PM, or the PM's supervisor when they entered it) routes it.
        var route = await workflow.Approvals.StartAsync(db, Workbench.Application.Approvals.ApprovalSubjects.VendorOdc(entry), cancellationToken);
        if (route.HasUnassigned)
        {
            throw new ArgumentException(workflow.IsMe(entry.SubmittedBy)
                ? "Nobody else can approve this: you entered it and either the project has no manager or you are its manager with no supervisor."
                : "This project has no project manager to approve it. Set the project manager first.");
        }

        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Submitted",
            new { Status = before.ToString() }, new { Status = entry.Status.ToString(), ApproverEmail = string.Join(", ", route.NotifyEmails) });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var approver in route.NotifyEmails)
        {
            await workflow.NotifyAsync(OdcNotificationKind.NeedsApproval, approver, $"ODC needs approval: {entry.Vendor} {entry.Amount:C}",
                $"{workflow.Actor} submitted {entry.Vendor} invoice {entry.InvoiceNumber} for {entry.Amount:C}.", "odcs/approvals", cancellationToken);
        }
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public Task<ExpensePolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new ExpensePolicyDto(workflow.ReceiptThreshold));

    private static async Task<OdcEntry> FindVendorAsync(IWorkbenchDbContext db, Guid odcId, CancellationToken cancellationToken)
    {
        var entry = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        return entry.ExpenseReportId is null
            ? entry
            : throw new ArgumentException("This entry is an expense report line; change it through its report.");
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, OdcEntry entry, SaveVendorOdcRequest request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Vendor))
        {
            throw new ArgumentException("Vendor is required.");
        }

        if (string.IsNullOrWhiteSpace(request.InvoiceNumber))
        {
            throw new ArgumentException("Invoice number is required.");
        }

        if (!Enum.IsDefined(request.Type))
        {
            throw new ArgumentException("Unknown ODC type.");
        }

        var wbs = await workflow.ValidateLineAsync(db, request.WbsElementId, request.IncurredOn, request.Amount, request.Description,
            request.IsUnallowable, request.UnallowableReason, null, cancellationToken);
        if (wbs.ProjectId != entry.ProjectId)
        {
            throw new ArgumentException($"WBS {wbs.Code} belongs to another project.");
        }

        entry.WbsElementId = wbs.Id;
        entry.Type = (OdcType)(int)request.Type;
        entry.IncurredOn = request.IncurredOn;
        entry.Amount = request.Amount;
        entry.Description = request.Description.Trim();
        entry.Vendor = request.Vendor.Trim();
        entry.InvoiceNumber = request.InvoiceNumber.Trim();
        entry.IsUnallowable = request.IsUnallowable;
        entry.UnallowableReason = request.IsUnallowable ? request.UnallowableReason!.Trim() : null;
    }
}

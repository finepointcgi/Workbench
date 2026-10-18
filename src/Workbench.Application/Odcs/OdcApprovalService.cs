using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Odcs;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Odcs;

public sealed class OdcApprovalService(IWorkbenchDbContextFactory dbFactory, OdcWorkflow workflow) : IOdcApprovalService
{
    public Task<OdcApprovalQueueDto> GetMyQueueAsync(CancellationToken cancellationToken = default) =>
        ListRoutedToMeAsync(OdcStatus.Submitted, cancellationToken);

    public Task<OdcApprovalQueueDto> ListMyApprovedAsync(CancellationToken cancellationToken = default) =>
        ListRoutedToMeAsync(OdcStatus.Approved, cancellationToken);

    public async Task<ExpenseReportDto> ApproveReportAsync(Guid reportId, string? comment = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (report, lines) = await LoadReportForDecisionAsync(db, reportId, OdcStatus.Submitted, cancellationToken);
        var now = workflow.UtcNow;
        report.Status = OdcStatus.Approved;
        report.ApprovedBy = workflow.Actor;
        report.ApprovedAtUtc = now;
        foreach (var line in lines)
        {
            line.Status = OdcStatus.Approved;
            line.ApprovedBy = workflow.Actor;
            line.ApprovedAtUtc = now;
        }

        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Approved",
            new { Status = nameof(OdcStatus.Submitted) }, new { Status = report.Status.ToString(), Total = lines.Sum(l => l.Amount) }, Trim(comment));
        await db.SaveChangesAsync(cancellationToken);
        await workflow.NotifyAsync(OdcNotificationKind.Approved, await EmployeeEmailAsync(db, report.EmployeeId, cancellationToken),
            $"Expense report approved: {report.Title}", $"{workflow.Actor} approved \"{report.Title}\".", $"expenses/{report.Id}", cancellationToken);
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    public async Task<ExpenseReportDto> RejectReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default)
    {
        reason = RequireReason(reason, "rejecting");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (report, lines) = await LoadReportForDecisionAsync(db, reportId, OdcStatus.Submitted, cancellationToken);
        var now = workflow.UtcNow;
        report.Status = OdcStatus.Rejected;
        report.RejectedBy = workflow.Actor;
        report.RejectedAtUtc = now;
        report.RejectionReason = reason;
        foreach (var line in lines)
        {
            line.Status = OdcStatus.Rejected;
            line.RejectedBy = workflow.Actor;
            line.RejectedAtUtc = now;
            line.RejectionReason = reason;
        }

        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Rejected",
            new { Status = nameof(OdcStatus.Submitted) }, new { Status = report.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        await workflow.NotifyAsync(OdcNotificationKind.Rejected, await EmployeeEmailAsync(db, report.EmployeeId, cancellationToken),
            $"Expense report rejected: {report.Title}", $"{workflow.Actor} rejected \"{report.Title}\": {reason}", $"expenses/{report.Id}", cancellationToken);
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    public async Task<ExpenseReportDto> ReopenReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default)
    {
        reason = RequireReason(reason, "reopening");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (report, lines) = await LoadReportForDecisionAsync(db, reportId, null, cancellationToken);
        if (report.Status is not (OdcStatus.Approved or OdcStatus.Rejected))
        {
            throw new InvalidOperationException("Only approved or rejected expense reports can be reopened.");
        }

        var before = report.Status;
        Reopen(report);
        foreach (var line in lines)
        {
            Reopen(line);
        }

        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Reopened",
            new { Status = before.ToString() }, new { Status = report.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    public async Task<OdcEntryDto> ApproveOdcAsync(Guid odcId, string? comment = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await LoadVendorForDecisionAsync(db, odcId, OdcStatus.Submitted, cancellationToken);
        entry.Status = OdcStatus.Approved;
        entry.ApprovedBy = workflow.Actor;
        entry.ApprovedAtUtc = workflow.UtcNow;
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Approved",
            new { Status = nameof(OdcStatus.Submitted) }, new { Status = entry.Status.ToString(), entry.Amount }, Trim(comment));
        await db.SaveChangesAsync(cancellationToken);
        await workflow.NotifyAsync(OdcNotificationKind.Approved, entry.SubmittedBy, $"ODC approved: {entry.Vendor} {entry.Amount:C}",
            $"{workflow.Actor} approved {entry.Vendor} invoice {entry.InvoiceNumber}.", $"projects/{entry.ProjectId}/odcs", cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<OdcEntryDto> RejectOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default)
    {
        reason = RequireReason(reason, "rejecting");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await LoadVendorForDecisionAsync(db, odcId, OdcStatus.Submitted, cancellationToken);
        entry.Status = OdcStatus.Rejected;
        entry.RejectedBy = workflow.Actor;
        entry.RejectedAtUtc = workflow.UtcNow;
        entry.RejectionReason = reason;
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Rejected",
            new { Status = nameof(OdcStatus.Submitted) }, new { Status = entry.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        await workflow.NotifyAsync(OdcNotificationKind.Rejected, entry.SubmittedBy, $"ODC rejected: {entry.Vendor} {entry.Amount:C}",
            $"{workflow.Actor} rejected {entry.Vendor} invoice {entry.InvoiceNumber}: {reason}", $"projects/{entry.ProjectId}/odcs", cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<OdcEntryDto> ReopenOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default)
    {
        reason = RequireReason(reason, "reopening");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await LoadVendorForDecisionAsync(db, odcId, null, cancellationToken);
        if (entry.Status is not (OdcStatus.Approved or OdcStatus.Rejected))
        {
            throw new InvalidOperationException("Only approved or rejected ODCs can be reopened.");
        }

        var before = entry.Status;
        Reopen(entry);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, "Reopened",
            new { Status = before.ToString() }, new { Status = entry.Status.ToString() }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    public async Task<OdcEntryDto> SetUnallowableAsync(Guid odcId, bool isUnallowable, string? reason, CancellationToken cancellationToken = default)
    {
        if (isUnallowable && string.IsNullOrWhiteSpace(reason))
        {
            throw new ArgumentException("Give a reason when marking a cost unallowable (for example \"Alcohol, FAR 31.205-51\").");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var entry = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (entry.ExpenseReportId is { } reportId)
        {
            await LoadReportForDecisionAsync(db, reportId, OdcStatus.Submitted, cancellationToken);
        }
        else
        {
            await LoadVendorForDecisionAsync(db, odcId, OdcStatus.Submitted, cancellationToken);
        }

        var before = new { entry.IsUnallowable, entry.UnallowableReason };
        entry.IsUnallowable = isUnallowable;
        entry.UnallowableReason = isUnallowable ? reason!.Trim() : null;
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, entry.Id, isUnallowable ? "MarkedUnallowable" : "ClearedUnallowable",
            before, new { entry.IsUnallowable, entry.UnallowableReason }, Trim(reason));
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
    }

    private async Task<OdcApprovalQueueDto> ListRoutedToMeAsync(OdcStatus status, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await workflow.FindCurrentEmployeeAsync(db, cancellationToken) is not { } me)
        {
            return new OdcApprovalQueueDto([], []);
        }

        var reports = await db.Set<ExpenseReport>().AsNoTracking()
            .Where(r => r.Status == status && r.EmployeeId != me.Id
                && db.Set<Employee>().Any(e => e.Id == r.EmployeeId && e.SupervisorId == me.Id))
            .OrderBy(r => r.SubmittedAtUtc)
            .ToListAsync(cancellationToken);

        var candidates = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.ExpenseReportId == null && o.Status == status)
            .OrderBy(o => o.SubmittedAtUtc)
            .ToListAsync(cancellationToken);
        var vendor = new List<OdcEntry>();
        foreach (var entry in candidates.Where(e => !workflow.IsMe(e.SubmittedBy)))
        {
            if ((await OdcWorkflow.ResolveVendorApproverAsync(db, entry, cancellationToken))?.Id == me.Id)
            {
                vendor.Add(entry);
            }
        }

        if (status == OdcStatus.Approved)
        {
            reports.Reverse();
            vendor.Reverse();
        }

        return new OdcApprovalQueueDto(await OdcWorkflow.ToDetailsAsync(db, reports, cancellationToken), await OdcWorkflow.ToDtosAsync(db, vendor, cancellationToken));
    }

    private async Task<(ExpenseReport Report, List<OdcEntry> Lines)> LoadReportForDecisionAsync(IWorkbenchDbContext db, Guid reportId, OdcStatus? requiredStatus, CancellationToken cancellationToken)
    {
        var report = await db.Set<ExpenseReport>().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Expense report", reportId);
        var approver = await OdcWorkflow.ResolveReportApproverAsync(db, report, cancellationToken);
        await workflow.EnsureApproverAsync(db, approver, report.SubmittedBy ?? report.CreatedBy, report.EmployeeId, cancellationToken);
        if (requiredStatus is { } s && report.Status != s)
        {
            throw new InvalidOperationException($"The expense report is {report.Status}, not {s}.");
        }

        var lines = await db.Set<OdcEntry>().Where(o => o.ExpenseReportId == reportId).ToListAsync(cancellationToken);
        return (report, lines);
    }

    private async Task<OdcEntry> LoadVendorForDecisionAsync(IWorkbenchDbContext db, Guid odcId, OdcStatus? requiredStatus, CancellationToken cancellationToken)
    {
        var entry = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (entry.ExpenseReportId is not null)
        {
            throw new ArgumentException("This entry is an expense report line; approve or reject its report.");
        }

        var approver = await OdcWorkflow.ResolveVendorApproverAsync(db, entry, cancellationToken);
        await workflow.EnsureApproverAsync(db, approver, entry.SubmittedBy, null, cancellationToken);
        if (requiredStatus is { } s && entry.Status != s)
        {
            throw new InvalidOperationException($"The ODC is {entry.Status}, not {s}.");
        }

        return entry;
    }

    private static void Reopen(ExpenseReport report)
    {
        report.Status = OdcStatus.Draft;
        report.ApprovedBy = null;
        report.ApprovedAtUtc = null;
        report.SubmittedAtUtc = null;
    }

    private static void Reopen(OdcEntry entry)
    {
        entry.Status = OdcStatus.Draft;
        entry.ApprovedBy = null;
        entry.ApprovedAtUtc = null;
        entry.SubmittedAtUtc = null;
    }

    private static async Task<string?> EmployeeEmailAsync(IWorkbenchDbContext db, Guid employeeId, CancellationToken cancellationToken) =>
        await db.Set<Employee>().Where(e => e.Id == employeeId).Select(e => e.Email).FirstOrDefaultAsync(cancellationToken);

    private static string RequireReason(string? reason, string action) =>
        string.IsNullOrWhiteSpace(reason) ? throw new ArgumentException($"A reason is required when {action}.") : reason.Trim();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

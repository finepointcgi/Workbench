using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
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
        var (report, lines, check) = await LoadReportForDecisionAsync(db, reportId, OdcStatus.Submitted, cancellationToken);
        var outcome = await workflow.Approvals.DecideAsync(db, check!, ApprovalDecisionKind.Approve, workflow.Actor, Trim(comment), cancellationToken);
        if (!outcome.IsComplete)
        {
            await StepApprovedAsync(db, OdcWorkflow.ReportEntityType, report.Id, outcome, comment, $"Expense report needs approval: {report.Title}",
                $"{workflow.Actor} approved a step of \"{report.Title}\"; it now needs yours.", cancellationToken);
            return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
        }

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
        var (report, lines, check) = await LoadReportForDecisionAsync(db, reportId, OdcStatus.Submitted, cancellationToken);
        await workflow.Approvals.DecideAsync(db, check!, ApprovalDecisionKind.Reject, workflow.Actor, reason, cancellationToken);
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
        var (report, lines, _) = await LoadReportForDecisionAsync(db, reportId, null, cancellationToken);
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
        var (entry, check) = await LoadVendorForDecisionAsync(db, odcId, OdcStatus.Submitted, cancellationToken);
        var outcome = await workflow.Approvals.DecideAsync(db, check!, ApprovalDecisionKind.Approve, workflow.Actor, Trim(comment), cancellationToken);
        if (!outcome.IsComplete)
        {
            await StepApprovedAsync(db, OdcWorkflow.OdcEntityType, entry.Id, outcome, comment, $"ODC needs approval: {entry.Vendor} {entry.Amount:C}",
                $"{workflow.Actor} approved a step of {entry.Vendor} invoice {entry.InvoiceNumber}; it now needs yours.", cancellationToken);
            return await OdcWorkflow.ToDtoAsync(db, entry, cancellationToken);
        }

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
        var (entry, check) = await LoadVendorForDecisionAsync(db, odcId, OdcStatus.Submitted, cancellationToken);
        await workflow.Approvals.DecideAsync(db, check!, ApprovalDecisionKind.Reject, workflow.Actor, reason, cancellationToken);
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
        var (entry, _) = await LoadVendorForDecisionAsync(db, odcId, null, cancellationToken);
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

        List<ExpenseReport> reports;
        List<OdcEntry> vendor;
        if (status == OdcStatus.Submitted)
        {
            // Waiting on me now: the workflow's pending tasks, plus anything submitted before workflows existed.
            var reportIds = await RoutedToMeAsync(db, ApprovalDocumentTypes.ExpenseReport, cancellationToken);
            var routedReports = await workflow.Approvals.RoutedDocumentIdsAsync(db, ApprovalDocumentTypes.ExpenseReport, cancellationToken);
            foreach (var legacy in await db.Set<ExpenseReport>().AsNoTracking().Where(r => r.Status == OdcStatus.Submitted && !routedReports.Contains(r.Id)).ToListAsync(cancellationToken))
            {
                var lines = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == legacy.Id).ToListAsync(cancellationToken);
                if (await workflow.Approvals.PreviewCanDecideAsync(db, await ApprovalSubjects.ExpenseReportAsync(db, legacy, lines, cancellationToken), workflow.Actor, cancellationToken))
                {
                    reportIds.Add(legacy.Id);
                }
            }

            var odcIds = await RoutedToMeAsync(db, ApprovalDocumentTypes.VendorOdc, cancellationToken);
            var routedOdcs = await workflow.Approvals.RoutedDocumentIdsAsync(db, ApprovalDocumentTypes.VendorOdc, cancellationToken);
            foreach (var legacy in await db.Set<OdcEntry>().AsNoTracking()
                .Where(o => o.ExpenseReportId == null && o.Status == OdcStatus.Submitted && !routedOdcs.Contains(o.Id)).ToListAsync(cancellationToken))
            {
                if (await workflow.Approvals.PreviewCanDecideAsync(db, ApprovalSubjects.VendorOdc(legacy), workflow.Actor, cancellationToken))
                {
                    odcIds.Add(legacy.Id);
                }
            }

            reports = await db.Set<ExpenseReport>().AsNoTracking().Where(r => r.Status == status && reportIds.Contains(r.Id))
                .OrderBy(r => r.SubmittedAtUtc).ToListAsync(cancellationToken);
            vendor = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == null && o.Status == status && odcIds.Contains(o.Id))
                .OrderBy(o => o.SubmittedAtUtc).ToListAsync(cancellationToken);
        }
        else
        {
            // Approved: what I approved through the workflow, plus what the fixed rules routed to me before it.
            var decidedReports = await DecidedByMeAsync(db, ApprovalDocumentTypes.ExpenseReport, cancellationToken);
            reports = await db.Set<ExpenseReport>().AsNoTracking()
                .Where(r => r.Status == status && r.EmployeeId != me.Id
                    && (decidedReports.Contains(r.Id) || db.Set<Employee>().Any(e => e.Id == r.EmployeeId && e.SupervisorId == me.Id)))
                .OrderBy(r => r.SubmittedAtUtc)
                .ToListAsync(cancellationToken);

            var decidedOdcs = await DecidedByMeAsync(db, ApprovalDocumentTypes.VendorOdc, cancellationToken);
            var candidates = await db.Set<OdcEntry>().AsNoTracking()
                .Where(o => o.ExpenseReportId == null && o.Status == status)
                .OrderBy(o => o.SubmittedAtUtc)
                .ToListAsync(cancellationToken);
            vendor = [];
            foreach (var entry in candidates.Where(e => !workflow.IsMe(e.SubmittedBy)))
            {
                if (decidedOdcs.Contains(entry.Id) || (await OdcWorkflow.ResolveVendorApproverAsync(db, entry, cancellationToken))?.Id == me.Id)
                {
                    vendor.Add(entry);
                }
            }

            reports.Reverse();
            vendor.Reverse();
        }

        return new OdcApprovalQueueDto(await OdcWorkflow.ToDetailsAsync(db, reports, cancellationToken), await OdcWorkflow.ToDtosAsync(db, vendor, cancellationToken));
    }

    private async Task<HashSet<Guid>> RoutedToMeAsync(IWorkbenchDbContext db, string documentType, CancellationToken cancellationToken) =>
        (await workflow.Approvals.ActionableAsync(db, documentType, workflow.Actor, cancellationToken)).Select(x => x.Instance.DocumentId).ToHashSet();

    private async Task<HashSet<Guid>> DecidedByMeAsync(IWorkbenchDbContext db, string documentType, CancellationToken cancellationToken)
    {
        var me = ApprovalEngine.Normalize(workflow.Actor);
        return (await db.Set<Domain.Approvals.ApprovalTask>().AsNoTracking()
            .Where(t => t.DecidedBy == me && t.Status == Domain.Approvals.ApprovalTaskStatus.Approved)
            .Join(db.Set<Domain.Approvals.ApprovalInstance>().Where(i => i.DocumentType == documentType), t => t.InstanceId, i => i.Id, (t, i) => i.DocumentId)
            .ToListAsync(cancellationToken)).ToHashSet();
    }

    /// <summary>An earlier step approved; the document stays submitted for the next step's approvers.</summary>
    private async Task StepApprovedAsync(IWorkbenchDbContext db, string entityType, Guid id, ApprovalOutcome outcome, string? comment, string subject, string message, CancellationToken cancellationToken)
    {
        workflow.Audit.Record(db, entityType, id, "StepApproved", null, new { Step = string.Join(", ", outcome.Decided.Select(t => t.StepName).Distinct()) }, Trim(comment));
        await db.SaveChangesAsync(cancellationToken);
        foreach (var next in outcome.NotifyEmails)
        {
            await workflow.NotifyAsync(OdcNotificationKind.NeedsApproval, next, subject, message, "odcs/approvals", cancellationToken);
        }
    }

    /// <summary>
    /// The report, its lines and (for a submitted one) what the signed-in user may decide on it. Deciding needs a
    /// pending workflow task; reopening needs the employee's approver or someone who approved it.
    /// </summary>
    private async Task<(ExpenseReport Report, List<OdcEntry> Lines, ApprovalCheck? Check)> LoadReportForDecisionAsync(
        IWorkbenchDbContext db, Guid reportId, OdcStatus? requiredStatus, CancellationToken cancellationToken)
    {
        var report = await db.Set<ExpenseReport>().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Expense report", reportId);
        var lines = await db.Set<OdcEntry>().Where(o => o.ExpenseReportId == reportId).ToListAsync(cancellationToken);
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        if (workflow.IsMe(report.SubmittedBy ?? report.CreatedBy) || me.Id == report.EmployeeId)
        {
            throw new UnauthorizedAccessException("You can't approve, reject or reopen your own submission.");
        }

        ApprovalCheck? check = null;
        if (requiredStatus == OdcStatus.Submitted && report.Status == OdcStatus.Submitted)
        {
            check = await workflow.Approvals.CheckAsync(db, ApprovalDocumentTypes.ExpenseReport, report.Id, workflow.Actor,
                ct => ApprovalSubjects.ExpenseReportAsync(db, report, lines, ct), cancellationToken);
            if (!check.CanDecide)
            {
                throw new UnauthorizedAccessException($"This is routed to {await RoutedToAsync(db, check, cancellationToken)}, not you.");
            }
        }
        else
        {
            var approver = await OdcWorkflow.ResolveReportApproverAsync(db, report, cancellationToken);
            if (approver?.Id != me.Id && !await workflow.Approvals.IsParticipantAsync(db, ApprovalDocumentTypes.ExpenseReport, report.Id, workflow.Actor, cancellationToken))
            {
                throw new UnauthorizedAccessException($"This is routed to {approver?.DisplayName ?? "no approver"}, not you.");
            }
        }

        if (requiredStatus is { } s && report.Status != s)
        {
            throw new InvalidOperationException($"The expense report is {report.Status}, not {s}.");
        }

        return (report, lines, check);
    }

    private async Task<(OdcEntry Entry, ApprovalCheck? Check)> LoadVendorForDecisionAsync(IWorkbenchDbContext db, Guid odcId, OdcStatus? requiredStatus, CancellationToken cancellationToken)
    {
        var entry = await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == odcId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("ODC", odcId);
        if (entry.ExpenseReportId is not null)
        {
            throw new ArgumentException("This entry is an expense report line; approve or reject its report.");
        }

        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        if (workflow.IsMe(entry.SubmittedBy))
        {
            throw new UnauthorizedAccessException("You can't approve, reject or reopen your own submission.");
        }

        ApprovalCheck? check = null;
        if (requiredStatus == OdcStatus.Submitted && entry.Status == OdcStatus.Submitted)
        {
            check = await workflow.Approvals.CheckAsync(db, ApprovalDocumentTypes.VendorOdc, entry.Id, workflow.Actor,
                _ => Task.FromResult(ApprovalSubjects.VendorOdc(entry)), cancellationToken);
            if (!check.CanDecide)
            {
                throw new UnauthorizedAccessException($"This is routed to {await RoutedToAsync(db, check, cancellationToken)}, not you.");
            }
        }
        else
        {
            var approver = await OdcWorkflow.ResolveVendorApproverAsync(db, entry, cancellationToken);
            if (approver?.Id != me.Id && !await workflow.Approvals.IsParticipantAsync(db, ApprovalDocumentTypes.VendorOdc, entry.Id, workflow.Actor, cancellationToken))
            {
                throw new UnauthorizedAccessException($"This is routed to {approver?.DisplayName ?? "no approver"}, not you.");
            }
        }

        if (requiredStatus is { } s && entry.Status != s)
        {
            throw new InvalidOperationException($"The ODC is {entry.Status}, not {s}.");
        }

        return (entry, check);
    }

    private async Task<string> RoutedToAsync(IWorkbenchDbContext db, ApprovalCheck check, CancellationToken cancellationToken)
    {
        var people = new List<string>();
        foreach (var task in check.Tasks.Where(t => t.Status == Domain.Approvals.ApprovalTaskStatus.Pending))
        {
            people.AddRange(await workflow.Approvals.EligibleAsync(db, task, cancellationToken));
        }

        var names = await db.Set<Employee>().AsNoTracking().Where(e => people.Contains(e.Email.ToLower())).Select(e => e.DisplayName).ToListAsync(cancellationToken);
        return names.Count > 0 ? string.Join(", ", names) : people.Count > 0 ? string.Join(", ", people.Distinct()) : "no approver";
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

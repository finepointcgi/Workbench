using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Odcs;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Odcs;

public sealed class ExpenseReportService(IWorkbenchDbContextFactory dbFactory, OdcWorkflow workflow) : IExpenseReportService
{
    public async Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await workflow.FindCurrentEmployeeAsync(db, cancellationToken) is not { } me)
        {
            return [];
        }

        var reports = await db.Set<ExpenseReport>().AsNoTracking().Where(r => r.EmployeeId == me.Id)
            .OrderByDescending(r => r.CreatedAtUtc).ToListAsync(cancellationToken);
        return (await OdcWorkflow.ToDetailsAsync(db, reports, cancellationToken)).Select(d => d.Report).ToList();
    }

    public async Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindAsync(db, reportId, cancellationToken);
        var me = await workflow.FindCurrentEmployeeAsync(db, cancellationToken);
        if ((me is null || (report.EmployeeId != me.Id && (await OdcWorkflow.ResolveReportApproverAsync(db, report, cancellationToken))?.Id != me.Id))
            && !await workflow.Approvals.IsParticipantAsync(db, ApprovalDocumentTypes.ExpenseReport, report.Id, workflow.Actor, cancellationToken))
        {
            throw new UnauthorizedAccessException("Only the employee and their supervisor can see this expense report.");
        }

        return await OdcWorkflow.ToDetailAsync(db, report, cancellationToken);
    }

    public async Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        var report = new ExpenseReport { EmployeeId = me.Id, Title = "", CreatedBy = workflow.Actor, CreatedAtUtc = workflow.UtcNow };
        Apply(report, request);
        db.Set<ExpenseReport>().Add(report);
        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Created", after: OdcWorkflow.Snapshot(report));
        await db.SaveChangesAsync(cancellationToken);
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    public async Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, cancellationToken);
        var before = OdcWorkflow.Snapshot(report);
        Apply(report, request);
        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Updated", before, OdcWorkflow.Snapshot(report));
        await db.SaveChangesAsync(cancellationToken);
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    public async Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, cancellationToken);
        var lines = await db.Set<OdcEntry>().Where(o => o.ExpenseReportId == reportId).ToListAsync(cancellationToken);
        foreach (var line in lines)
        {
            workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Deleted", before: OdcWorkflow.Snapshot(line));
        }

        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Deleted", before: OdcWorkflow.Snapshot(report));
        db.Set<OdcEntry>().RemoveRange(lines);
        db.Set<ExpenseReport>().Remove(report);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, cancellationToken);
        var line = new OdcEntry
        {
            Description = "",
            SubmittedBy = workflow.Actor,
            CreatedAtUtc = workflow.UtcNow,
            EmployeeId = report.EmployeeId,
            ExpenseReportId = report.Id,
            Status = report.Status,
        };
        await ApplyAsync(db, report, line, request, cancellationToken);
        db.Set<OdcEntry>().Add(line);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Created", after: OdcWorkflow.Snapshot(line));
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, line, cancellationToken);
    }

    public async Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, cancellationToken);
        var line = await FindLineAsync(db, reportId, lineId, cancellationToken);
        var before = OdcWorkflow.Snapshot(line);
        await ApplyAsync(db, report, line, request, cancellationToken);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Updated", before, OdcWorkflow.Snapshot(line));
        await db.SaveChangesAsync(cancellationToken);
        return await OdcWorkflow.ToDtoAsync(db, line, cancellationToken);
    }

    public async Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindEditableAsync(db, reportId, cancellationToken);
        var line = await FindLineAsync(db, reportId, lineId, cancellationToken);
        workflow.Audit.Record(db, OdcWorkflow.OdcEntityType, line.Id, "Deleted", before: OdcWorkflow.Snapshot(line));
        db.Set<OdcEntry>().Remove(line);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindEditableAsync(db, reportId, cancellationToken);
        var line = await FindLineAsync(db, reportId, lineId, cancellationToken);
        return await OdcService.AttachAsync(db, workflow, line, fileName, content, cancellationToken);
    }

    public async Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await FindEditableAsync(db, reportId, cancellationToken);
        var lines = await db.Set<OdcEntry>().Where(o => o.ExpenseReportId == reportId).ToListAsync(cancellationToken);
        if (lines.Count == 0)
        {
            throw new ArgumentException("Add at least one expense line before submitting.");
        }

        foreach (var line in lines)
        {
            await workflow.ValidateForSubmitAsync(db, line, cancellationToken);
        }

        var now = workflow.UtcNow;
        var before = report.Status;
        report.Status = OdcStatus.Submitted;
        report.SubmittedBy = workflow.Actor;
        report.SubmittedAtUtc = now;
        report.RejectionReason = null;
        report.RejectedBy = null;
        report.RejectedAtUtc = null;
        foreach (var line in lines)
        {
            line.Status = OdcStatus.Submitted;
            line.SubmittedAtUtc = now;
            line.RejectionReason = null;
            line.RejectedBy = null;
            line.RejectedAtUtc = null;
        }

        // The workflow (Setup › Approval workflows; by default the employee's supervisor) routes it.
        var route = await workflow.Approvals.StartAsync(db, await ApprovalSubjects.ExpenseReportAsync(db, report, lines, cancellationToken), cancellationToken);
        if (route.HasUnassigned)
        {
            throw new ArgumentException("You have no supervisor to approve expense reports. Ask an administrator to set one.");
        }

        workflow.Audit.Record(db, OdcWorkflow.ReportEntityType, report.Id, "Submitted",
            new { Status = before.ToString() },
            new { Status = report.Status.ToString(), ApproverEmail = string.Join(", ", route.NotifyEmails), Lines = lines.Count, Total = lines.Sum(l => l.Amount) });
        await db.SaveChangesAsync(cancellationToken);

        foreach (var approver in route.NotifyEmails)
        {
            await workflow.NotifyAsync(OdcNotificationKind.NeedsApproval, approver, $"Expense report needs approval: {report.Title}",
                $"{workflow.Actor} submitted \"{report.Title}\" for {lines.Sum(l => l.Amount):C}.", "odcs/approvals", cancellationToken);
        }
        return (await OdcWorkflow.ToDetailAsync(db, report, cancellationToken)).Report;
    }

    internal static async Task EnsureOwnerAsync(IWorkbenchDbContext db, OdcWorkflow workflow, ExpenseReport report, CancellationToken cancellationToken)
    {
        var me = await workflow.RequireCurrentEmployeeAsync(db, cancellationToken);
        if (report.EmployeeId != me.Id)
        {
            throw new UnauthorizedAccessException("Only the employee can change their expense report.");
        }
    }

    private static async Task<ExpenseReport> FindAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken cancellationToken) =>
        await db.Set<ExpenseReport>().FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Expense report", reportId);

    private async Task<ExpenseReport> FindEditableAsync(IWorkbenchDbContext db, Guid reportId, CancellationToken cancellationToken)
    {
        var report = await FindAsync(db, reportId, cancellationToken);
        await EnsureOwnerAsync(db, workflow, report, cancellationToken);
        OdcWorkflow.EnsureEditable(report.Status, "expense report");
        return report;
    }

    private static async Task<OdcEntry> FindLineAsync(IWorkbenchDbContext db, Guid reportId, Guid lineId, CancellationToken cancellationToken) =>
        await db.Set<OdcEntry>().FirstOrDefaultAsync(o => o.Id == lineId && o.ExpenseReportId == reportId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Expense line", lineId);

    private static void Apply(ExpenseReport report, SaveExpenseReportRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Title))
        {
            throw new ArgumentException("Title is required.");
        }

        if (request.TripStart is { } start && request.TripEnd is { } end && end < start)
        {
            throw new ArgumentException("The trip can't end before it starts.");
        }

        report.Title = request.Title.Trim();
        report.Purpose = string.IsNullOrWhiteSpace(request.Purpose) ? null : request.Purpose.Trim();
        report.TripStart = request.TripStart;
        report.TripEnd = request.TripEnd;
    }

    private async Task ApplyAsync(IWorkbenchDbContext db, ExpenseReport report, OdcEntry line, SaveExpenseLineRequest request, CancellationToken cancellationToken)
    {
        if (request.Type is not (OdcCostType.Travel or OdcCostType.Other))
        {
            throw new ArgumentException("Expense lines are Travel or Other. Enter vendor invoices as vendor ODCs on the project.");
        }

        var wbs = await workflow.ValidateLineAsync(db, request.WbsElementId, request.IncurredOn, request.Amount, request.Description,
            request.IsUnallowable, request.UnallowableReason, report.EmployeeId, cancellationToken);
        line.ProjectId = wbs.ProjectId;
        line.WbsElementId = wbs.Id;
        line.Type = (OdcType)(int)request.Type;
        line.IncurredOn = request.IncurredOn;
        line.Amount = request.Amount;
        line.Description = request.Description.Trim();
        line.Vendor = string.IsNullOrWhiteSpace(request.Vendor) ? null : request.Vendor.Trim();
        line.IsUnallowable = request.IsUnallowable;
        line.UnallowableReason = request.IsUnallowable ? request.UnallowableReason!.Trim() : null;
    }
}

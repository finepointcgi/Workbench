using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Files;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Files;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Odcs;

/// <summary>Expense policy, bound from config section <c>Expenses</c>.</summary>
public sealed class ExpenseOptions
{
    public const string Section = "Expenses";

    /// <summary>Lines of this amount or more need a receipt (config <c>Expenses:ReceiptThreshold</c>, default $75).</summary>
    public decimal ReceiptThreshold { get; set; } = 75m;
}

/// <summary>
/// Rules shared by vendor ODCs, expense reports and approvals: line validation, approver routing, locking, audit and
/// notifications. One instance per scope; each service method still opens its own DbContext.
/// </summary>
public sealed class OdcWorkflow(
    IProjectService projects,
    AuditRecorder audit,
    ICurrentUser currentUser,
    IOdcNotifier notifier,
    FileStorage files,
    IOptions<ExpenseOptions> options,
    TimeProvider clock)
{
    public const string OdcEntityType = nameof(OdcEntry);
    public const string ReportEntityType = nameof(ExpenseReport);

    public decimal ReceiptThreshold => options.Value.ReceiptThreshold;

    public AuditRecorder Audit => audit;

    public FileStorage Files => files;

    public string Actor => currentUser.Email;

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public bool IsMe(string? email) => string.Equals(email, currentUser.Email, StringComparison.OrdinalIgnoreCase);

    public async Task<Employee?> FindCurrentEmployeeAsync(IWorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var email = currentUser.Email.Trim().ToLowerInvariant();
        return await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Email.ToLower() == email, cancellationToken);
    }

    public async Task<Employee> RequireCurrentEmployeeAsync(IWorkbenchDbContext db, CancellationToken cancellationToken) =>
        await FindCurrentEmployeeAsync(db, cancellationToken)
        ?? throw new UnauthorizedAccessException($"{currentUser.Email} has no employee record.");

    // ---- Validation ----

    /// <summary>
    /// Checks the rules every ODC line must meet: an open, chargeable leaf WBS element of an active project (and, for an
    /// employee's expense, one of that employee's charge codes on the date), an open fiscal period, a positive amount,
    /// a description, and a reason when the cost is flagged unallowable. Returns the WBS element.
    /// </summary>
    public async Task<WbsElement> ValidateLineAsync(
        IWorkbenchDbContext db,
        Guid wbsElementId,
        DateOnly incurredOn,
        decimal amount,
        string? description,
        bool isUnallowable,
        string? unallowableReason,
        Guid? expenseEmployeeId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            throw new ArgumentException("Description is required.");
        }

        if (amount <= 0)
        {
            throw new ArgumentException("Amount must be greater than 0.");
        }

        if (decimal.Round(amount, 2) != amount)
        {
            throw new ArgumentException("Amount can have at most two decimal places.");
        }

        if (isUnallowable && string.IsNullOrWhiteSpace(unallowableReason))
        {
            throw new ArgumentException("Give a reason when marking a cost unallowable (for example \"Alcohol, FAR 31.205-51\").");
        }

        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsElementId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("WBS element", wbsElementId);
        var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == wbs.ProjectId, cancellationToken);
        if (project.Status != ProjectStatus.Active)
        {
            throw new ArgumentException($"Project {project.Code} is not active.");
        }

        if (!wbs.IsOpen)
        {
            throw new ArgumentException($"WBS {wbs.Code} is closed.");
        }

        if (!wbs.IsChargeable)
        {
            throw new ArgumentException($"WBS {wbs.Code} is not chargeable.");
        }

        if (await db.Set<WbsElement>().AnyAsync(w => w.ParentId == wbs.Id, cancellationToken))
        {
            throw new ArgumentException($"WBS {wbs.Code} has children; charge one of its lowest-level elements.");
        }

        if (expenseEmployeeId is { } employeeId)
        {
            var codes = await projects.ListChargeCodesAsync(employeeId, incurredOn, cancellationToken);
            if (codes.All(c => c.WbsElementId != wbs.Id))
            {
                throw new ArgumentException($"WBS {wbs.Code} is not one of your charge codes on {incurredOn:yyyy-MM-dd}.");
            }
        }

        var period = await db.Set<FiscalPeriod>().AsNoTracking()
            .FirstOrDefaultAsync(p => p.Start <= incurredOn && p.End >= incurredOn, cancellationToken)
            ?? throw new ArgumentException($"No fiscal period covers {incurredOn:yyyy-MM-dd}.");
        if (period.Status != PeriodStatus.Open)
        {
            throw new ArgumentException($"Fiscal period {period.Name} is closed.");
        }

        return wbs;
    }

    /// <summary>Re-validates a saved line before submit, including the receipt threshold.</summary>
    public async Task ValidateForSubmitAsync(IWorkbenchDbContext db, OdcEntry entry, CancellationToken cancellationToken)
    {
        await ValidateLineAsync(db, entry.WbsElementId, entry.IncurredOn, entry.Amount, entry.Description, entry.IsUnallowable,
            entry.UnallowableReason, entry.ExpenseReportId is null ? null : entry.EmployeeId, cancellationToken);
        if (entry.Amount >= ReceiptThreshold && entry.ReceiptAttachmentId is null
            && !await PerDiem.PerDiemReceipts.IsExemptAsync(db, entry.Id, cancellationToken)) // Per diem M&IE needs no receipt.
        {
            throw new ArgumentException($"\"{entry.Description}\" ({entry.Amount:C}) needs a receipt: receipts are required for {ReceiptThreshold:C} or more.");
        }
    }

    public static void EnsureEditable(OdcStatus status, string what)
    {
        if (status is OdcStatus.Submitted)
        {
            throw new InvalidOperationException($"The {what} is waiting for approval and can't be changed.");
        }

        if (status is OdcStatus.Approved)
        {
            throw new InvalidOperationException($"The {what} is approved and locked. Ask the approver to reopen it to make corrections.");
        }
    }

    // ---- Routing ----

    /// <summary>A vendor ODC goes to the project manager, or to the PM's supervisor when the PM entered it.</summary>
    public static async Task<Employee?> ResolveVendorApproverAsync(IWorkbenchDbContext db, OdcEntry entry, CancellationToken cancellationToken)
    {
        var pmId = await db.Set<Project>().Where(p => p.Id == entry.ProjectId).Select(p => p.ProjectManagerId).FirstOrDefaultAsync(cancellationToken);
        var pm = pmId is null ? null : await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == pmId, cancellationToken);
        if (pm is not null && string.Equals(pm.Email, entry.SubmittedBy, StringComparison.OrdinalIgnoreCase))
        {
            pm = pm.SupervisorId is { } sup ? await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == sup, cancellationToken) : null;
        }

        return pm;
    }

    /// <summary>An expense report goes to the employee's supervisor.</summary>
    public static async Task<Employee?> ResolveReportApproverAsync(IWorkbenchDbContext db, ExpenseReport report, CancellationToken cancellationToken)
    {
        var supervisorId = await db.Set<Employee>().Where(e => e.Id == report.EmployeeId).Select(e => e.SupervisorId).FirstOrDefaultAsync(cancellationToken);
        return supervisorId is null ? null : await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == supervisorId, cancellationToken);
    }

    /// <summary>Throws unless the signed-in user is <paramref name="approver"/> and didn't submit the item themselves.</summary>
    public async Task<Employee> EnsureApproverAsync(IWorkbenchDbContext db, Employee? approver, string? submittedBy, Guid? employeeId, CancellationToken cancellationToken)
    {
        var me = await RequireCurrentEmployeeAsync(db, cancellationToken);
        if (IsMe(submittedBy) || me.Id == employeeId)
        {
            throw new UnauthorizedAccessException("You can't approve, reject or reopen your own submission.");
        }

        if (approver is null || approver.Id != me.Id)
        {
            throw new UnauthorizedAccessException($"This is routed to {approver?.DisplayName ?? "no approver"}, not you.");
        }

        return me;
    }

    // ---- Notifications ----

    public async Task NotifyAsync(OdcNotificationKind kind, string? recipientEmail, string subject, string message, string link, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(recipientEmail))
        {
            await notifier.NotifyAsync(new OdcNotification(kind, recipientEmail, subject, message, link), cancellationToken);
        }
    }

    // ---- Mapping ----

    public static object Snapshot(OdcEntry e) => new
    {
        e.ProjectId,
        e.WbsElementId,
        Type = e.Type.ToString(),
        e.IncurredOn,
        e.Amount,
        e.Description,
        e.Vendor,
        e.InvoiceNumber,
        e.EmployeeId,
        e.ExpenseReportId,
        e.IsUnallowable,
        e.UnallowableReason,
        e.ReceiptAttachmentId,
        Status = e.Status.ToString(),
    };

    public static object Snapshot(ExpenseReport r) => new { r.EmployeeId, r.Title, r.Purpose, r.TripStart, r.TripEnd, Status = r.Status.ToString() };

    public static async Task<IReadOnlyList<OdcEntryDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyCollection<OdcEntry> entries, CancellationToken cancellationToken)
    {
        if (entries.Count == 0)
        {
            return [];
        }

        var wbsIds = entries.Select(e => e.WbsElementId).Distinct().ToList();
        var projectIds = entries.Select(e => e.ProjectId).Distinct().ToList();
        var fileIds = entries.Where(e => e.ReceiptAttachmentId is not null).Select(e => e.ReceiptAttachmentId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, cancellationToken);
        var projectCodes = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        var fileNames = await db.Set<StoredFile>().AsNoTracking().Where(f => fileIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, f => f.FileName, cancellationToken);
        return entries.Select(e =>
        {
            var w = wbs.GetValueOrDefault(e.WbsElementId);
            return new OdcEntryDto(e.Id, e.ProjectId, projectCodes.GetValueOrDefault(e.ProjectId) ?? "", e.WbsElementId, w?.Code ?? "", w?.Name ?? "",
                (OdcCostType)(int)e.Type, e.IncurredOn, e.Amount, e.Description, e.Vendor, e.InvoiceNumber, e.EmployeeId, e.ExpenseReportId,
                e.IsUnallowable, e.UnallowableReason, e.ReceiptAttachmentId,
                e.ReceiptAttachmentId is { } f ? fileNames.GetValueOrDefault(f) : null,
                (OdcApprovalState)(int)e.Status, e.SubmittedBy, e.CreatedAtUtc, e.SubmittedAtUtc, e.ApprovedBy, e.ApprovedAtUtc, e.RejectionReason);
        }).ToList();
    }

    public static async Task<OdcEntryDto> ToDtoAsync(IWorkbenchDbContext db, OdcEntry entry, CancellationToken cancellationToken) =>
        (await ToDtosAsync(db, [entry], cancellationToken))[0];

    public static async Task<IReadOnlyList<ExpenseReportDetailDto>> ToDetailsAsync(IWorkbenchDbContext db, IReadOnlyCollection<ExpenseReport> reports, CancellationToken cancellationToken)
    {
        if (reports.Count == 0)
        {
            return [];
        }

        var reportIds = reports.Select(r => r.Id).ToList();
        var employeeIds = reports.Select(r => r.EmployeeId).Distinct().ToList();
        var lines = await db.Set<OdcEntry>().AsNoTracking()
            .Where(o => o.ExpenseReportId != null && reportIds.Contains(o.ExpenseReportId.Value))
            .OrderBy(o => o.IncurredOn).ThenBy(o => o.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        var lineDtos = await ToDtosAsync(db, lines, cancellationToken);
        var names = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return reports.Select(r =>
        {
            var mine = lineDtos.Where(l => l.ExpenseReportId == r.Id).ToList();
            var dto = new ExpenseReportDto(r.Id, r.EmployeeId, names.GetValueOrDefault(r.EmployeeId) ?? "", r.Title, r.Purpose, r.TripStart, r.TripEnd,
                (OdcApprovalState)(int)r.Status, mine.Count, mine.Sum(l => l.Amount), OdcTotals.Allowable(mine), r.CreatedAtUtc,
                r.SubmittedBy, r.SubmittedAtUtc, r.ApprovedBy, r.ApprovedAtUtc, r.RejectionReason);
            return new ExpenseReportDetailDto(dto, mine);
        }).ToList();
    }

    public static async Task<ExpenseReportDetailDto> ToDetailAsync(IWorkbenchDbContext db, ExpenseReport report, CancellationToken cancellationToken) =>
        (await ToDetailsAsync(db, [report], cancellationToken))[0];
}

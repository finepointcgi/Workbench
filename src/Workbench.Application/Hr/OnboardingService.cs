using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.Leave;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Assets;
using Workbench.Contracts.Hr;
using Workbench.Domain.Assets;
using Workbench.Domain.Identity;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using D = Workbench.Domain.Hr;

namespace Workbench.Application.Hr;

/// <summary>
/// Onboarding and offboarding checklists. Offboarding adds Workbench's own tasks: deactivate the sign-in (IT), one task per
/// asset in the employee's custody (Finance), the final timesheet, the leave payout and the end of benefits (HR).
/// </summary>
public sealed class OnboardingService(
    IWorkbenchDbContextFactory dbFactory,
    HrAccess hrAccess,
    AuditRecorder audit,
    INotificationPublisher notifications,
    UserLifecycle lifecycle,
    IServiceProvider services,
    TimeProvider clock,
    SecurityEventLog? securityLog = null) : IOnboardingService
{
    public const string AuditEntityType = "EmployeeChecklist";

    private DateOnly Today => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<ChecklistTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<D.ChecklistTemplate>().AsNoTracking().Include(t => t.Tasks).ToListAsync(cancellationToken))
            .OrderBy(t => t.Kind).ThenBy(t => t.Name).Select(ToDto).ToList();
    }

    public async Task<ChecklistTemplateDto> SaveTemplateAsync(Guid? templateId, SaveChecklistTemplateRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var name = HrText.Clean(request.Name, 200, "The name") ?? throw new ArgumentException("Give the template a name.");
        var tasks = request.Tasks ?? [];
        if (tasks.Count is 0 or > 100)
        {
            throw new ArgumentException("A template needs between 1 and 100 tasks.");
        }

        if (tasks.Any(t => t.DueOffsetDays is < -365 or > 365))
        {
            throw new ArgumentException("Due dates are at most a year before or after the start or last day.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var template = templateId is { } id
            ? await db.Set<D.ChecklistTemplate>().Include(t => t.Tasks).FirstOrDefaultAsync(t => t.Id == id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Checklist template", id)
            : db.Set<D.ChecklistTemplate>().Add(new D.ChecklistTemplate { Name = name }).Entity;
        template.Name = name;
        template.Kind = (D.ChecklistKind)(int)request.Kind;
        template.IsActive = request.IsActive;
        template.UpdatedAtUtc = UtcNow;
        db.Set<D.ChecklistTemplateTask>().RemoveRange(template.Tasks);
        template.Tasks.Clear();
        var sequence = 0;
        foreach (var t in tasks)
        {
            var task = new D.ChecklistTemplateTask
            {
                TemplateId = template.Id,
                Sequence = ++sequence,
                Title = HrText.Clean(t.Title, 200, "A task") ?? throw new ArgumentException("Every task needs a title."),
                Owner = (D.ChecklistOwner)(int)t.Owner,
                DueOffsetDays = t.DueOffsetDays,
            };
            template.Tasks.Add(task);
            db.Set<D.ChecklistTemplateTask>().Add(task);
        }

        audit.Record(db, "ChecklistTemplate", template.Id, templateId is null ? "Created" : "Updated", null, new { template.Name, Kind = template.Kind.ToString(), Tasks = tasks.Count });
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(template);
    }

    public async Task<EmployeeChecklistDto> StartOnboardingAsync(StartOnboardingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(request.EmployeeId);
        if (await db.Set<D.EmployeeChecklist>().AnyAsync(c => c.EmployeeId == employee.Id && c.Kind == D.ChecklistKind.Onboarding && c.Status == D.ChecklistStatus.Open, cancellationToken))
        {
            throw new ArgumentException($"{employee.DisplayName} already has an open onboarding checklist.");
        }

        var template = await TemplateAsync(db, request.TemplateId, D.ChecklistKind.Onboarding, cancellationToken);
        var checklist = New(employee, D.ChecklistKind.Onboarding, employee.HireDate, template?.Id, viewer.Email);
        var supervisor = await SupervisorEmailAsync(db, employee, cancellationToken);
        foreach (var t in template?.Tasks.OrderBy(t => t.Sequence) ?? Enumerable.Empty<D.ChecklistTemplateTask>())
        {
            Add(checklist, t.Title, t.Owner, employee.HireDate.AddDays(t.DueOffsetDays), employee, supervisor);
        }

        Add(checklist, "Enroll in benefits (or waive them)", D.ChecklistOwner.Employee, employee.HireDate.AddDays(BenefitRules.EnrollmentWindowDays), employee, supervisor);
        Add(checklist, "Complete EEO and veteran self-identification (voluntary)", D.ChecklistOwner.Employee, employee.HireDate.AddDays(7), employee, supervisor);
        return await SaveNewAsync(db, checklist, employee, viewer, cancellationToken);
    }

    public async Task<EmployeeChecklistDto> StartOffboardingAsync(StartOffboardingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var employee = await db.Set<Employee>().FirstOrDefaultAsync(e => e.Id == request.EmployeeId, cancellationToken) ?? throw HrAccess.EmployeeNotFound(request.EmployeeId);
        if (viewer.IsSelf(employee.Id))
        {
            throw new WorkbenchForbiddenException("You can't start your own offboarding.");
        }

        if (request.LastDay < employee.HireDate)
        {
            throw new ArgumentException($"The last day can't be before the hire date ({employee.HireDate:yyyy-MM-dd}).");
        }

        if (await db.Set<D.EmployeeChecklist>().AnyAsync(c => c.EmployeeId == employee.Id && c.Kind == D.ChecklistKind.Offboarding && c.Status == D.ChecklistStatus.Open, cancellationToken))
        {
            throw new ArgumentException($"{employee.DisplayName} is already being offboarded.");
        }

        var template = await TemplateAsync(db, request.TemplateId, D.ChecklistKind.Offboarding, cancellationToken);
        var before = new { employee.TerminationDate };
        employee.TerminationDate = request.LastDay;
        var profile = await HrPersonnelService.ProfileAsync(db, employee.Id, cancellationToken);
        profile.TerminationReason = (D.TerminationReason)(int)request.Reason;
        if (request.LastDay < Today)
        {
            profile.Status = D.EmploymentStatus.Terminated;
        }

        profile.UpdatedAtUtc = UtcNow;
        profile.UpdatedBy = viewer.Email;
        audit.Record(db, "Employee", employee.Id, "Termination date set", before, new { employee.TerminationDate, Reason = request.Reason.ToString() }, HrText.Clean(request.Note, 500, "The note"));

        var checklist = New(employee, D.ChecklistKind.Offboarding, request.LastDay, template?.Id, viewer.Email);
        checklist.TerminationReason = (D.TerminationReason)(int)request.Reason;
        var supervisor = await SupervisorEmailAsync(db, employee, cancellationToken);
        foreach (var t in template?.Tasks.OrderBy(t => t.Sequence) ?? Enumerable.Empty<D.ChecklistTemplateTask>())
        {
            Add(checklist, t.Title, t.Owner, request.LastDay.AddDays(t.DueOffsetDays), employee, supervisor);
        }

        var last = request.LastDay;
        Add(checklist, "Deactivate sign-in and remove access (and from the identity provider when SCIM-managed)", D.ChecklistOwner.It, last, employee, supervisor,
            D.ChecklistAction.DeprovisionAccess);
        foreach (var asset in await db.Set<FixedAsset>().AsNoTracking().Where(a => a.CustodianEmployeeId == employee.Id && a.Status == AssetStatus.InService)
            .OrderBy(a => a.TagNumber).ToListAsync(cancellationToken))
        {
            Add(checklist, $"Recover asset {asset.TagNumber}: {asset.Description}", D.ChecklistOwner.Finance, last, employee, supervisor, D.ChecklistAction.RecoverAsset, asset.Id);
        }

        Add(checklist, "Approve the final timesheet (through the last day)", D.ChecklistOwner.Manager, last.AddDays(2), employee, supervisor, D.ChecklistAction.FinalTimesheet);
        Add(checklist, "Record the leave balance to pay out with the final check", D.ChecklistOwner.Hr, last.AddDays(3), employee, supervisor, D.ChecklistAction.LeavePayout);
        Add(checklist, "End benefits at the end of the month", D.ChecklistOwner.Hr, last.AddDays(3), employee, supervisor, D.ChecklistAction.EndBenefits);
        return await SaveNewAsync(db, checklist, employee, viewer, cancellationToken);
    }

    public async Task<IReadOnlyList<EmployeeChecklistDto>> ListAsync(bool openOnly = true, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.EmployeeChecklist>().AsNoTracking().Include(c => c.Tasks).Where(c => !openOnly || c.Status == D.ChecklistStatus.Open);
        if (!viewer.IsHr)
        {
            var scope = viewer.EmployeeId is { } me ? await HrAccess.ReportsOfAsync(db, me, cancellationToken) : [];
            if (viewer.EmployeeId is { } self)
            {
                scope.Add(self);
            }

            var ids = scope.ToList();
            query = query.Where(c => ids.Contains(c.EmployeeId));
        }

        var rows = await query.ToListAsync(cancellationToken);
        return await MapAsync(db, viewer, rows.OrderBy(c => c.AnchorDate).ToList(), cancellationToken);
    }

    public async Task<EmployeeChecklistDto> GetAsync(Guid checklistId, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var checklist = await db.Set<D.EmployeeChecklist>().AsNoTracking().Include(c => c.Tasks).FirstOrDefaultAsync(c => c.Id == checklistId, cancellationToken);
        if (checklist is null || !await MayReadAsync(db, viewer, checklist, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Checklist", checklistId);
        }

        return (await MapAsync(db, viewer, [checklist], cancellationToken))[0];
    }

    public async Task<IReadOnlyList<MyChecklistTaskDto>> ListMyTasksAsync(CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var owners = OwnedRoles(viewer);
        var email = viewer.Email;
        var tasks = await db.Set<D.ChecklistTask>().AsNoTracking()
            .Where(t => t.Status == D.ChecklistTaskStatus.Open && (t.AssigneeEmail == email || (t.AssigneeEmail == null && owners.Contains(t.Owner))))
            .Join(db.Set<D.EmployeeChecklist>().Where(c => c.Status == D.ChecklistStatus.Open), t => t.ChecklistId, c => c.Id, (t, c) => new { t, c.EmployeeId, c.Kind })
            .ToListAsync(cancellationToken);
        var names = await db.Set<Employee>().AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.DisplayName, cancellationToken);
        return tasks
            .Where(x => !viewer.IsSelf(x.EmployeeId) || x.t.Owner == D.ChecklistOwner.Employee) // HR doesn't work their own offboarding
            .OrderBy(x => x.t.DueDate).ThenBy(x => x.t.Sequence)
            .Select(x => new MyChecklistTaskDto(ToDto(x.t, true), names.GetValueOrDefault(x.EmployeeId, ""), (ChecklistKind)(int)x.Kind))
            .ToList();
    }

    public async Task<EmployeeChecklistDto> CompleteTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (task, checklist) = await LoadTaskAsync(db, viewer, taskId, cancellationToken);
        if (!CanComplete(viewer, task, checklist))
        {
            throw new WorkbenchForbiddenException(task.Owner switch
            {
                D.ChecklistOwner.It => "Only IT (administrators) can complete this task.",
                D.ChecklistOwner.Finance => "Only Finance can complete this task.",
                D.ChecklistOwner.Manager => "Only the employee's manager (or HR) can complete this task.",
                D.ChecklistOwner.Employee => "Only the employee (or HR) can complete this task.",
                _ => "Only HR can complete this task.",
            });
        }

        var employee = await db.Set<Employee>().AsNoTracking().FirstAsync(e => e.Id == checklist.EmployeeId, cancellationToken);
        var note = HrText.Clean(request?.Note, 1000, "The note");
        switch (task.Action)
        {
            case D.ChecklistAction.DeprovisionAccess:
                note = await DeprovisionAsync(db, employee, checklist, note, cancellationToken);
                break;
            case D.ChecklistAction.RecoverAsset:
                await services.GetRequiredService<IAssetService>().TransferAsync(task.AssetId!.Value,
                    new TransferAssetRequest(Today, "Returned at offboarding", null, null, null, $"Recovered from {employee.DisplayName} at offboarding"), cancellationToken);
                break;
            case D.ChecklistAction.FinalTimesheet:
                var approved = await FinalTimesheetApprovedAsync(db, employee.Id, checklist.AnchorDate, cancellationToken);
                if (!approved)
                {
                    throw new ArgumentException($"{employee.DisplayName}'s timesheet through {checklist.AnchorDate:MMM d} isn't approved yet.");
                }

                checklist.FinalTimesheetApproved = true;
                break;
            case D.ChecklistAction.LeavePayout:
                var hours = await PayoutHoursAsync(db, employee.Id, checklist.AnchorDate, cancellationToken);
                checklist.LeavePayoutHours = hours;
                note = $"{hours:0.##} h of paid time off to pay out{(note is null ? "" : $". {note}")}";
                break;
            case D.ChecklistAction.EndBenefits:
                var ended = await EndBenefitsAsync(db, employee.Id, checklist.AnchorDate, cancellationToken);
                note = $"{ended} election(s) end {EndOfMonth(checklist.AnchorDate):MMM d, yyyy}{(note is null ? "" : $". {note}")}";
                break;
        }

        task.Status = D.ChecklistTaskStatus.Done;
        task.CompletedBy = viewer.Email;
        task.CompletedAtUtc = UtcNow;
        task.Note = note is { Length: > 1000 } ? note[..1000] : note;
        CloseIfDone(checklist);
        audit.Record(db, AuditEntityType, checklist.Id, "Task done", null, new { task.Title, Action = task.Action.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, viewer, [checklist], cancellationToken))[0];
    }

    public async Task<EmployeeChecklistDto> SkipTaskAsync(Guid taskId, CompleteChecklistTaskRequest request, CancellationToken cancellationToken = default)
    {
        var viewer = await hrAccess.ViewerAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (task, checklist) = await LoadTaskAsync(db, viewer, taskId, cancellationToken);
        task.Status = D.ChecklistTaskStatus.Skipped;
        task.CompletedBy = viewer.Email;
        task.CompletedAtUtc = UtcNow;
        task.Note = HrText.Clean(request?.Note, 1000, "The note") ?? throw new ArgumentException("Say why the task isn't needed.");
        CloseIfDone(checklist);
        audit.Record(db, AuditEntityType, checklist.Id, "Task skipped", null, new { task.Title }, task.Note);
        await db.SaveChangesAsync(cancellationToken);
        return (await MapAsync(db, viewer, [checklist], cancellationToken))[0];
    }

    private async Task<string?> DeprovisionAsync(IWorkbenchDbContext db, Employee employee, D.EmployeeChecklist checklist, string? note, CancellationToken ct)
    {
        var email = AccessControl.Normalize(employee.Email);
        var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, ct);
        string result;
        if (user is null)
        {
            result = "No sign-in account to deactivate";
        }
        else
        {
            if (user.IsActive)
            {
                await lifecycle.SetActiveAsync(email, false, hrAccess.Access.Email, $"Offboarding (last day {checklist.AnchorDate:yyyy-MM-dd})", ct);
            }

            result = user.ScimManaged
                ? "Sign-in deactivated; the account is SCIM-managed, so also remove it from the identity provider's Workbench group"
                : "Sign-in deactivated and sessions ended";
        }

        checklist.AccessDeprovisionedAtUtc = UtcNow;
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(HrSecurityEvents.AccessDeprovisioned, email, detail: result, cancellationToken: ct);
        }

        return note is null ? result : $"{result}. {note}";
    }

    /// <summary>Every day from the last timesheet period start through the last day is on an approved timesheet.</summary>
    internal static async Task<bool> FinalTimesheetApprovedAsync(IWorkbenchDbContext db, Guid employeeId, DateOnly lastDay, CancellationToken ct)
    {
        var covering = await db.Set<Timesheet>().AsNoTracking()
            .Where(t => t.EmployeeId == employeeId && t.PeriodStart <= lastDay && t.PeriodEnd >= lastDay).ToListAsync(ct);
        return covering.Count > 0 && covering.All(t => t.Status == TimesheetStatus.Approved);
    }

    /// <summary>The paid-time-off balance (PTO-type policies) on the last day.</summary>
    internal static async Task<decimal> PayoutHoursAsync(IWorkbenchDbContext db, Guid employeeId, DateOnly lastDay, CancellationToken ct)
    {
        var pto = await db.Set<LeavePolicy>().AsNoTracking().Where(p => p.Type == LeaveType.Pto).Select(p => p.Id).ToListAsync(ct);
        var balances = await LeaveEngine.BalancesAsync(db, [employeeId], lastDay, ct);
        return Math.Max(0m, balances.Where(b => pto.Contains(b.Key.PolicyId)).Sum(b => b.Value));
    }

    private static DateOnly EndOfMonth(DateOnly date) => new DateOnly(date.Year, date.Month, 1).AddMonths(1).AddDays(-1);

    private async Task<int> EndBenefitsAsync(IWorkbenchDbContext db, Guid employeeId, DateOnly lastDay, CancellationToken ct)
    {
        var end = EndOfMonth(lastDay);
        var elections = await db.Set<D.BenefitElection>().Where(e => e.EmployeeId == employeeId && (e.CoverageEnd == null || e.CoverageEnd > end)).ToListAsync(ct);
        foreach (var e in elections)
        {
            if (e.CoverageStart > end)
            {
                db.Set<D.BenefitElection>().Remove(e);
            }
            else
            {
                e.CoverageEnd = end;
            }
        }

        audit.Record(db, BenefitService.ElectionAudit, employeeId, "Ended at termination", null, new { CoverageEnd = end, Count = elections.Count });
        return elections.Count;
    }

    private static void CloseIfDone(D.EmployeeChecklist checklist)
    {
        if (checklist.Tasks.All(t => t.Status != D.ChecklistTaskStatus.Open))
        {
            checklist.Status = D.ChecklistStatus.Completed;
            checklist.CompletedAtUtc = DateTime.UtcNow;
        }
    }

    private async Task<(D.ChecklistTask Task, D.EmployeeChecklist Checklist)> LoadTaskAsync(IWorkbenchDbContext db, HrAccess.Viewer viewer, Guid taskId, CancellationToken ct)
    {
        var task = await db.Set<D.ChecklistTask>().FirstOrDefaultAsync(t => t.Id == taskId, ct) ?? throw WorkbenchNotFoundException.For("Checklist task", taskId);
        var checklist = await db.Set<D.EmployeeChecklist>().Include(c => c.Tasks).FirstAsync(c => c.Id == task.ChecklistId, ct);
        if (!await MayReadAsync(db, viewer, checklist, ct) && !CanComplete(viewer, task, checklist))
        {
            throw WorkbenchNotFoundException.For("Checklist task", taskId);
        }

        task = checklist.Tasks.First(t => t.Id == taskId);
        if (task.Status != D.ChecklistTaskStatus.Open || checklist.Status != D.ChecklistStatus.Open)
        {
            throw new ArgumentException("This task is already done.");
        }

        return (task, checklist);
    }

    private static async Task<bool> MayReadAsync(IWorkbenchDbContext db, HrAccess.Viewer viewer, D.EmployeeChecklist checklist, CancellationToken ct) =>
        viewer.IsHr || viewer.IsSelf(checklist.EmployeeId)
        || (viewer.EmployeeId is { } me && (await HrAccess.ReportsOfAsync(db, me, ct)).Contains(checklist.EmployeeId));

    private static HashSet<D.ChecklistOwner> OwnedRoles(HrAccess.Viewer viewer)
    {
        var owners = new HashSet<D.ChecklistOwner>();
        if (viewer.IsHr)
        {
            owners.Add(D.ChecklistOwner.Hr);
        }

        if (viewer.IsAdmin)
        {
            owners.Add(D.ChecklistOwner.It);
        }

        if (viewer.IsFinance)
        {
            owners.Add(D.ChecklistOwner.Finance);
        }

        return owners;
    }

    /// <summary>
    /// The owner completes a task: the assignee (manager or employee) or a member of the owning role. HR may complete
    /// manager and employee tasks too; system actions stay with their role (IT deprovisions, Finance recovers assets).
    /// Nobody works the tasks of their own offboarding except the employee's own.
    /// </summary>
    private static bool CanComplete(HrAccess.Viewer viewer, D.ChecklistTask task, D.EmployeeChecklist checklist)
    {
        if (viewer.IsSelf(checklist.EmployeeId) && task.Owner != D.ChecklistOwner.Employee)
        {
            return false;
        }

        if (task.AssigneeEmail is { } assignee && string.Equals(assignee, viewer.Email, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return task.Owner switch
        {
            D.ChecklistOwner.Hr => viewer.IsHr,
            D.ChecklistOwner.It => viewer.IsAdmin,
            D.ChecklistOwner.Finance => viewer.IsFinance,
            _ => viewer.IsHr,
        };
    }

    private async Task<D.ChecklistTemplate?> TemplateAsync(IWorkbenchDbContext db, Guid? templateId, D.ChecklistKind kind, CancellationToken ct)
    {
        var query = db.Set<D.ChecklistTemplate>().AsNoTracking().Include(t => t.Tasks).Where(t => t.Kind == kind);
        if (templateId is { } id)
        {
            return await query.FirstOrDefaultAsync(t => t.Id == id, ct) ?? throw new ArgumentException($"That isn't an {kind.ToString().ToLowerInvariant()} template.");
        }

        return await query.Where(t => t.IsActive).OrderBy(t => t.Name).FirstOrDefaultAsync(ct);
    }

    private static async Task<string?> SupervisorEmailAsync(IWorkbenchDbContext db, Employee employee, CancellationToken ct) =>
        employee.SupervisorId is { } s ? await db.Set<Employee>().AsNoTracking().Where(e => e.Id == s).Select(e => e.Email).FirstOrDefaultAsync(ct) : null;

    private D.EmployeeChecklist New(Employee employee, D.ChecklistKind kind, DateOnly anchor, Guid? templateId, string actor) => new()
    {
        EmployeeId = employee.Id, Kind = kind, AnchorDate = anchor, TemplateId = templateId, CreatedBy = actor, CreatedAtUtc = UtcNow,
    };

    private static void Add(D.EmployeeChecklist checklist, string title, D.ChecklistOwner owner, DateOnly due, Employee employee, string? supervisorEmail,
        D.ChecklistAction action = D.ChecklistAction.None, Guid? assetId = null) =>
        checklist.Tasks.Add(new D.ChecklistTask
        {
            ChecklistId = checklist.Id,
            Sequence = checklist.Tasks.Count + 1,
            Title = title.Length > 200 ? title[..200] : title,
            Owner = owner,
            AssigneeEmail = owner switch
            {
                D.ChecklistOwner.Employee => AccessControl.Normalize(employee.Email),
                D.ChecklistOwner.Manager => supervisorEmail is null ? null : AccessControl.Normalize(supervisorEmail),
                _ => null,
            },
            DueDate = due,
            Action = action,
            AssetId = assetId,
        });

    private async Task<EmployeeChecklistDto> SaveNewAsync(IWorkbenchDbContext db, D.EmployeeChecklist checklist, Employee employee, HrAccess.Viewer viewer, CancellationToken ct)
    {
        db.Set<D.EmployeeChecklist>().Add(checklist);
        audit.Record(db, AuditEntityType, checklist.Id, $"{checklist.Kind} started", null, new { Employee = employee.EmployeeNumber, checklist.AnchorDate, Tasks = checklist.Tasks.Count });
        await db.SaveChangesAsync(ct);

        // People with a task get told; role tasks go to the role's members.
        var people = checklist.Tasks.Where(t => t.AssigneeEmail is not null).Select(t => t.AssigneeEmail!).ToList();
        foreach (var role in checklist.Tasks.Where(t => t.AssigneeEmail is null).Select(t => t.Owner).Distinct())
        {
            people.AddRange(await ApprovalEngine.RoleMembersAsync(db, role switch
            {
                D.ChecklistOwner.It => ApprovalOrgRole.Admin,
                D.ChecklistOwner.Finance => ApprovalOrgRole.Finance,
                _ => ApprovalOrgRole.Hr,
            }, ct));
        }

        var label = checklist.Kind == D.ChecklistKind.Onboarding ? $"Onboarding {employee.DisplayName}" : $"Offboarding {employee.DisplayName}";
        await notifications.PublishAsync(people.Where(p => !string.Equals(p, viewer.Email, StringComparison.OrdinalIgnoreCase)), HrNotificationKinds.ChecklistTask,
            $"{label}: you have tasks", $"{(checklist.Kind == D.ChecklistKind.Onboarding ? "Starts" : "Last day")} {checklist.AnchorDate:MMM d}", link: "hr/onboarding");
        return (await MapAsync(db, viewer, [checklist], ct))[0];
    }

    private async Task<List<EmployeeChecklistDto>> MapAsync(IWorkbenchDbContext db, HrAccess.Viewer viewer, IReadOnlyList<D.EmployeeChecklist> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.EmployeeId).ToList();
        var names = await db.Set<Employee>().AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var templates = await db.Set<D.ChecklistTemplate>().AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        return rows.Select(c => new EmployeeChecklistDto(c.Id, c.EmployeeId, names.GetValueOrDefault(c.EmployeeId, ""), (ChecklistKind)(int)c.Kind, c.AnchorDate,
            (ChecklistStatus)(int)c.Status, c.TemplateId is { } t ? templates.GetValueOrDefault(t) : null,
            c.TerminationReason is { } r ? (TerminationReason)(int)r : null, c.LeavePayoutHours, c.FinalTimesheetApproved, c.AccessDeprovisionedAtUtc,
            c.Tasks.OrderBy(t => t.Sequence).Select(t => ToDto(t, t.Status == D.ChecklistTaskStatus.Open && c.Status == D.ChecklistStatus.Open && CanComplete(viewer, t, c))).ToList()))
            .ToList();
    }

    private static ChecklistTaskDto ToDto(D.ChecklistTask t, bool canComplete) =>
        new(t.Id, t.ChecklistId, t.Sequence, t.Title, (ChecklistOwner)(int)t.Owner, t.AssigneeEmail, t.DueDate, (ChecklistAction)(int)t.Action, t.AssetId,
            (ChecklistTaskStatus)(int)t.Status, t.CompletedBy, t.CompletedAtUtc, t.Note, canComplete);

    private static ChecklistTemplateDto ToDto(D.ChecklistTemplate t) =>
        new(t.Id, t.Name, (ChecklistKind)(int)t.Kind, t.IsActive,
            t.Tasks.OrderBy(x => x.Sequence).Select(x => new ChecklistTemplateTaskDto(x.Id, x.Sequence, x.Title, (ChecklistOwner)(int)x.Owner, x.DueOffsetDays)).ToList());
}

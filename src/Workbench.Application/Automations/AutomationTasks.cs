using Workbench.Contracts.Identity;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Approvals;
using Workbench.Application.Identity;
using Workbench.Application.MyTodo;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.Approvals;
using Workbench.Domain.Automations;

namespace Workbench.Application.Automations;

/// <summary>To-dos automations give people. Each person sees and completes only their own (administrators may complete anyone's).</summary>
public sealed class AutomationTaskService(IWorkbenchDbContextFactory dbFactory, ICurrentUser currentUser, AccessControl access, TimeProvider clock) : IAutomationTaskService
{
    private string Me => ApprovalEngine.Normalize(currentUser.Email);

    public async Task<IReadOnlyList<AutomationTaskDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var me = Me;
        var tasks = await db.Set<AutomationTask>().AsNoTracking().Where(t => t.AssigneeEmail == me && t.CompletedAtUtc == null)
            .OrderBy(t => t.CreatedAtUtc).Take(200).ToListAsync(cancellationToken);
        return await ToDtosAsync(db, tasks, cancellationToken);
    }

    public async Task<AutomationTaskDto> CompleteAsync(Guid taskId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var task = await db.Set<AutomationTask>().FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken) ?? throw WorkbenchNotFoundException.For("To-do", taskId);
        if (task.AssigneeEmail != Me && !await access.HasAsync(Permission.AdminAutomations, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("To-do", taskId);
        }

        task.CompletedAtUtc ??= clock.GetUtcNow().UtcDateTime;
        task.CompletedBy ??= Me;
        await db.SaveChangesAsync(cancellationToken);
        return (await ToDtosAsync(db, [task], cancellationToken))[0];
    }

    private static async Task<IReadOnlyList<AutomationTaskDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<AutomationTask> tasks, CancellationToken ct)
    {
        var ids = tasks.Select(t => t.AutomationId).Distinct().ToList();
        var names = await db.Set<AutomationRecord>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return tasks.Select(t => new AutomationTaskDto(t.Id, t.Title, t.Notes, t.DueOn, t.Link, names.GetValueOrDefault(t.AutomationId) ?? "Automation", t.CreatedAtUtc, t.CompletedAtUtc)).ToList();
    }
}

/// <summary>Open automation to-dos on the home page.</summary>
public sealed class AutomationTaskTodoProvider : IMyTodoProvider
{
    public string Name => "Automation to-dos";

    public async Task<IReadOnlyList<MyTodoItem>> GetAsync(MyTodoContext context, CancellationToken cancellationToken)
    {
        if (context.Service<IAutomationTaskService>() is not { } tasks)
        {
            return [];
        }

        return (await tasks.ListMineAsync(cancellationToken)).Select(t => new MyTodoItem(
            $"automation-task:{t.Id}", TodoCategories.Project, t.Title, t.Notes ?? $"From the automation \"{t.AutomationName}\".",
            context.UrgencyFor(t.DueOn, TodoUrgency.ThisWeek), t.DueOn, t.Link ?? "", t.Link is null ? "Open" : "Open record")).ToList();
    }
}

/// <summary>
/// Inbox decisions on record approval workflows (document types <c>custom-…</c>) that automations start. The decision is
/// recorded by the engine and audited on the record, which fires the record's "approved"/"rejected" automations.
/// </summary>
public sealed class RecordApprovalHandler(IWorkbenchDbContextFactory dbFactory, ApprovalEngine engine, ICurrentUser currentUser, AuditRecorder audit,
    Notifications.INotificationPublisher publisher, IServiceProvider? services = null) : IApprovalDocumentHandler
{
    public string DocumentType => ApprovalDocumentTypes.CustomPrefix;

    public bool Handles(string documentType) => ApprovalDocumentTypes.IsCustom(documentType);

    public Task ApproveAsync(Guid documentId, string? comment, CancellationToken ct) => DecideAsync(documentId, ApprovalDecisionKind.Approve, comment, ct);

    public Task RejectAsync(Guid documentId, string reason, CancellationToken ct) => DecideAsync(documentId, ApprovalDecisionKind.Reject, reason, ct);

    private async Task DecideAsync(Guid recordId, ApprovalDecisionKind decision, string? comment, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var me = ApprovalEngine.Normalize(currentUser.Email);
        var requests = await db.Set<AutomationApprovalRequest>().Where(r => r.RecordId == recordId && r.Outcome == null).OrderByDescending(r => r.CreatedAtUtc).ToListAsync(ct);
        foreach (var request in requests)
        {
            var instance = await db.Set<ApprovalInstance>().AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.ApprovalInstanceId && i.Status == ApprovalInstanceStatus.Pending, ct);
            if (instance is null)
            {
                continue;
            }

            var check = await engine.CheckAsync(db, request.DocumentType, recordId, me, null, ct);
            if (!check.CanDecide)
            {
                continue;
            }

            var outcome = await engine.DecideAsync(db, check, decision, me, comment, ct);
            var sheetItem = request.EntityType == AutomationSheets.EntityType;
            if (outcome.Status != ApprovalInstanceStatus.Pending)
            {
                request.Outcome = outcome.Status.ToString();
                if (!sheetItem)
                {
                    var entity = AutomationEntities.Require(request.EntityType);
                    audit.Record(db, entity.AuditEntityType, recordId, outcome.IsComplete ? "Approved" : "Rejected",
                        new { Approval = "Pending" }, new { Approval = outcome.Status.ToString(), Workflow = request.DocumentType }, comment);
                }
            }

            await db.SaveChangesAsync(ct);
            if (sheetItem && outcome.Status != ApprovalInstanceStatus.Pending)
            {
                await WriteSheetDecisionAsync(db, request, outcome.IsComplete ? "Approved" : "Rejected", ct);
            }

            await publisher.PublishAsync(outcome.NotifyEmails, "approval.requested", $"Approval needed: {instance.Title}", instance.Summary, "approvals");
            if (outcome.Status != ApprovalInstanceStatus.Pending && instance.SubmitterEmail != me)
            {
                await publisher.PublishAsync(instance.SubmitterEmail, "approval.decided", $"{instance.Title}: {outcome.Status.ToString().ToLowerInvariant()}", comment,
                    string.IsNullOrEmpty(instance.Link) ? null : instance.Link);
            }

            return;
        }

        throw new InvalidOperationException("Nothing on this record is waiting on you.");
    }

    /// <summary>
    /// A sheet row's approval was decided: write "Approved" or "Rejected" into the column the automation's request-approval action
    /// names (when it names one), as the automation acting for its author, so "when Approval changes to Approved" automations run.
    /// </summary>
    private async Task WriteSheetDecisionAsync(IWorkbenchDbContext db, AutomationApprovalRequest request, string decision, CancellationToken ct)
    {
        var automation = await db.Set<AutomationRecord>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == request.AutomationId, ct);
        var version = automation?.CurrentVersionId is { } versionId ? await db.Set<AutomationVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == versionId, ct) : null;
        var field = version is null ? null : AutomationJson.Definition(version.DefinitionJson).Actions.FirstOrDefault(a => a.Kind == AutomationActionKind.RequireApproval)?.FieldKey;
        var item = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == request.RecordId, ct);
        if (automation?.SheetId is not { } sheetId || field is null || item is null || item.SheetId != sheetId || services is null)
        {
            return;
        }

        // Through the automation decorator (inside the permission checks): the approver needn't be able to edit the sheet.
        _ = services.GetService(typeof(Contracts.Sheets.IItemService));
        if ((services.GetService(typeof(AutomationItemServiceAccessor)) as AutomationItemServiceAccessor)?.Instance is not { } items)
        {
            return;
        }

        var values = new Dictionary<string, string?>(item.Values);
        var request2 = new Contracts.Sheets.UpdateItemRequest(item.Title, item.Start, item.Finish, item.CostpointWbsId, values);
        switch (field)
        {
            case AutomationSheets.Title:
                request2 = request2 with { Title = decision };
                break;
            case AutomationSheets.Start or AutomationSheets.Finish:
                return;
            default:
                values[field] = decision;
                break;
        }

        using (Automation.AutomationContext.Enter(automation.Name, automation.UpdatedBy))
        {
            await items.UpdateAsync(sheetId, item.Id, request2, ct);
        }
    }
}

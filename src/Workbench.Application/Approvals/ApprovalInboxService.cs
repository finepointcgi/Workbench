using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Approvals;

/// <summary>
/// Acts on one document type's approvals through its own module, so the module's state changes, checks and posting run
/// as if the decision were made on its own page. Register one per document type.
/// </summary>
public interface IApprovalDocumentHandler
{
    string DocumentType { get; }

    /// <summary>Whether this handler decides <paramref name="documentType"/> (by default, exactly its own type).</summary>
    bool Handles(string documentType) => documentType == DocumentType;

    Task ApproveAsync(Guid documentId, string? comment, CancellationToken cancellationToken);

    Task RejectAsync(Guid documentId, string reason, CancellationToken cancellationToken);
}

/// <summary>The unified inbox: every task waiting on the signed-in user (or on someone they stand in for).</summary>
public sealed class ApprovalInboxService(
    IWorkbenchDbContextFactory dbFactory,
    ApprovalEngine engine,
    AccessControl access,
    IEnumerable<IApprovalDocumentHandler> handlers,
    ApprovalBackfill? backfill = null) : IApprovalInboxService
{
    private readonly ApprovalBackfill _backfill = backfill ?? new ApprovalBackfill(dbFactory, engine);

    public async Task<IReadOnlyList<ApprovalInboxItemDto>> ListMineAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return [];
        }

        await _backfill.RunAsync(cancellationToken); // Documents pending since before workflows (seeded, imported, upgraded).
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var now = engine.UtcNow;
        var items = (await engine.ActionableAsync(db, null, access.Email, cancellationToken))
            .SelectMany(x => x.Tasks.Select(t => (x.Instance, t.Task, t.OnBehalfOf)))
            .GroupBy(x => x.Instance.Id)
            .Select(g =>
            {
                var (instance, task, onBehalfOf) = g.OrderBy(x => x.Task.ActivatedAtUtc).First();
                var scopes = g.Select(x => x.Task.ScopeLabel).Where(l => l is not null).Distinct().ToList();
                var projects = g.Select(x => ProjectOf(x.Task.ScopeKey)).ToList();
                var item = new ApprovalInboxItemDto(task.Id, instance.Id, instance.DocumentType, ApprovalDocumentTypes.Label(instance.DocumentType), instance.DocumentId,
                    instance.Title, instance.Summary, instance.Amount, instance.SubmitterEmail, string.Join(", ", g.Select(x => x.Task.StepName).Distinct()),
                    scopes.Count == 0 ? null : string.Join(", ", scopes), task.ActivatedAtUtc,
                    task.ActivatedAtUtc is { } since ? BusinessDays.Between(since, now) : 0, onBehalfOf, instance.Link,
                    projects.FirstOrDefault(p => p is not null));
                // Only my projects' share of it is mine to decide when every task I have on it is a project's.
                return (Item: item, OnlyProjects: projects.All(p => p is not null) ? projects.Select(p => p!.Value).Distinct().ToList() : null);
            })
            .OrderBy(x => x.Item.WaitingSinceUtc)
            .ToList();
        items.AddRange(await PlansWaitingAsync(db, now, cancellationToken));
        items = items.OrderBy(x => x.Item.WaitingSinceUtc).ToList();
        var shares = await ProjectHoursAsync(db, items.Where(x => x.OnlyProjects is not null && x.Item.DocumentType == ApprovalDocumentTypes.Timesheet)
            .Select(x => (x.Item.DocumentId, (IReadOnlyList<Guid>)x.OnlyProjects!)).ToList(), cancellationToken);
        var emails = items.Select(x => x.Item.SubmitterEmail).Distinct().ToList();
        var names = await db.Set<Domain.Identity.AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email) && u.DisplayName != "")
            .ToDictionaryAsync(u => u.Email, u => u.DisplayName, cancellationToken);
        return items.Select(x => (shares.TryGetValue(x.Item.DocumentId, out var share)
                ? x.Item with { Amount = share.Hours, Summary = $"{share.Hours:0.0} h on {share.Codes} of {share.Total:0.0} h" }
                : x.Item) with { SubmitterName = names.GetValueOrDefault(x.Item.SubmitterEmail) })
            .ToList();
    }

    /// <summary>
    /// Project plans submitted to Finance, listed here for people who set up projects (not their own submissions) so Finance has
    /// one place for what's waiting. They're decided through <see cref="ProjectAccounting.ProjectPlanApprovalHandler"/>, not the workflow engine.
    /// </summary>
    private async Task<List<(ApprovalInboxItemDto Item, List<Guid>? OnlyProjects)>> PlansWaitingAsync(IWorkbenchDbContext db, DateTime now, CancellationToken ct)
    {
        if (!await access.HasAsync(Permission.SetupProjects, ct))
        {
            return [];
        }

        var me = ApprovalEngine.Normalize(access.Email);
        var waiting = await (
                from plan in db.Set<ProjectPlan>().AsNoTracking()
                join project in db.Set<Project>().AsNoTracking() on plan.ProjectId equals project.Id
                where plan.Status == ProjectPlanStatus.Submitted
                select new { project.Id, project.Code, project.Name, plan.SubmittedBy, plan.SubmittedAtUtc, plan.SubmitNote })
            .ToListAsync(ct);
        var result = new List<(ApprovalInboxItemDto, List<Guid>?)>();
        foreach (var w in waiting.Where(w => ApprovalEngine.Normalize(w.SubmittedBy) != me))
        {
            var hours = (await ProjectAccounting.ProjectPlanService.PlanLinesAsync(db, w.Id, ct)).Sum(l => l.Hours);
            result.Add((new ApprovalInboxItemDto(w.Id, w.Id, ApprovalDocumentTypes.ProjectPlan, ApprovalDocumentTypes.Label(ApprovalDocumentTypes.ProjectPlan), w.Id,
                $"Plan for {w.Code} {w.Name}", w.SubmitNote is { } note ? $"{hours:0.#} h planned. \"{note}\"" : $"{hours:0.#} h planned", null, w.SubmittedBy ?? "", "Finance review", null,
                w.SubmittedAtUtc, w.SubmittedAtUtc is { } since ? BusinessDays.Between(since, now) : 0, null, $"projects/{w.Id}/plan", w.Id), null));
        }

        return result;
    }

    /// <summary>For timesheets a project manager decides only part of: the hours on their projects, the project codes and the sheet's total.</summary>
    private static async Task<Dictionary<Guid, (decimal Hours, string Codes, decimal Total)>> ProjectHoursAsync(
        IWorkbenchDbContext db, IReadOnlyList<(Guid SheetId, IReadOnlyList<Guid> Projects)> sheets, CancellationToken ct)
    {
        if (sheets.Count == 0)
        {
            return [];
        }

        var sheetIds = sheets.Select(s => s.SheetId).ToList();
        var entries = await db.Set<TimeEntry>().AsNoTracking().Where(e => sheetIds.Contains(e.TimesheetId))
            .Join(db.Set<WbsElement>(), e => e.WbsElementId, w => w.Id, (e, w) => new { e.TimesheetId, e.Hours, w.ProjectId })
            .ToListAsync(ct);
        var projectIds = sheets.SelectMany(s => s.Projects).Distinct().ToList();
        var codes = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var bySheet = entries.ToLookup(e => e.TimesheetId);
        return sheets.ToDictionary(s => s.SheetId, s => (
            bySheet[s.SheetId].Where(e => s.Projects.Contains(e.ProjectId)).Sum(e => e.Hours),
            string.Join(", ", s.Projects.Select(p => codes.GetValueOrDefault(p) ?? "?").Order()),
            bySheet[s.SheetId].Sum(e => e.Hours)));
    }

    public async Task<int> CountMineAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return 0;
        }

        await _backfill.RunAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await engine.ActionableAsync(db, null, access.Email, cancellationToken)).Count + (await PlansWaitingAsync(db, engine.UtcNow, cancellationToken)).Count;
    }

    public async Task DecideAsync(string documentType, Guid documentId, ApprovalDecisionRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var handler = handlers.LastOrDefault(h => h.Handles(documentType))
            ?? throw new WorkbenchNotFoundException($"There's no approval handler for \"{documentType}\".");
        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        switch (request.Decision)
        {
            case ApprovalDecisionKind.Approve:
                await handler.ApproveAsync(documentId, comment, cancellationToken);
                break;
            case ApprovalDecisionKind.Reject:
                await handler.RejectAsync(documentId, comment ?? throw new ArgumentException("A reason is required when rejecting."), cancellationToken);
                break;
            default:
                await handler.RejectAsync(documentId, $"Returned for changes: {comment ?? throw new ArgumentException("Say what needs to change.")}", cancellationToken);
                await using (var db = await dbFactory.CreateAsync(cancellationToken))
                {
                    await engine.MarkReturnedAsync(db, documentType, documentId, access.Email, cancellationToken);
                    await db.SaveChangesAsync(cancellationToken);
                }

                break;
        }
    }

    /// <summary>The most a bulk decision takes at once (each document is its own unit of work).</summary>
    public const int MaxBulkItems = 200;

    public async Task<BulkApprovalResultDto> DecideManyAsync(BulkApprovalRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Decision is not (ApprovalDecisionKind.Approve or ApprovalDecisionKind.Reject))
        {
            throw new ArgumentException("Return documents one at a time, saying what each one needs.");
        }

        var items = (request.Items ?? []).Where(i => i is not null && !string.IsNullOrWhiteSpace(i.DocumentType))
            .DistinctBy(i => (i.DocumentType, i.DocumentId)).ToList();
        if (items.Count == 0)
        {
            throw new ArgumentException("Pick at least one item.");
        }

        if (items.Count > MaxBulkItems)
        {
            throw new ArgumentException($"Pick at most {MaxBulkItems} items at a time.");
        }

        var comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim();
        if (request.Decision == ApprovalDecisionKind.Reject && comment is null)
        {
            throw new ArgumentException("A reason is required when rejecting.");
        }

        Dictionary<(string, Guid), ApprovalInstance> instances;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var ids = items.Select(i => i.DocumentId).Distinct().ToList();
            instances = (await db.Set<ApprovalInstance>().AsNoTracking().Where(i => ids.Contains(i.DocumentId)).ToListAsync(cancellationToken))
                .GroupBy(i => (i.DocumentType, i.DocumentId))
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.StartedAtUtc).First());
        }

        var me = ApprovalEngine.Normalize(access.Email);
        var results = new List<BulkApprovalItemResult>();
        foreach (var item in items)
        {
            var instance = instances.GetValueOrDefault((item.DocumentType, item.DocumentId));
            var title = instance?.Title ?? ApprovalDocumentTypes.Label(item.DocumentType);
            try
            {
                // One document at a time through its module, as if decided on its own: its checks decide, not this loop.
                await DecideAsync(item.DocumentType, item.DocumentId, new ApprovalDecisionRequest(request.Decision, comment), cancellationToken);
                results.Add(new BulkApprovalItemResult(item.DocumentType, item.DocumentId, title, true, null));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or WorkbenchNotFoundException)
            {
                var problem = instance is not null && ApprovalEngine.Normalize(instance.SubmitterEmail) == me
                    ? "you entered it yourself"
                    : instance is { Status: not ApprovalInstanceStatus.Pending } ? "it isn't waiting for approval any more" : ex.Message;
                results.Add(new BulkApprovalItemResult(item.DocumentType, item.DocumentId, title, false, problem));
            }
        }

        return new BulkApprovalResultDto(results.Count(r => r.Succeeded), results.Count(r => !r.Succeeded), Summarize(request.Decision, results), results);
    }

    /// <summary>"12 approved, 1 couldn't be: you entered it yourself" (several reasons are listed with their counts).</summary>
    public static string Summarize(ApprovalDecisionKind decision, IReadOnlyList<BulkApprovalItemResult> results)
    {
        var done = results.Count(r => r.Succeeded);
        var text = $"{done} {(decision == ApprovalDecisionKind.Reject ? "rejected" : "approved")}";
        var failed = results.Where(r => !r.Succeeded).GroupBy(r => r.Problem ?? "unknown problem").ToList();
        if (failed.Count == 0)
        {
            return text;
        }

        var count = failed.Sum(g => g.Count());
        var reasons = failed.Count == 1
            ? failed[0].Key
            : string.Join("; ", failed.OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} because {g.Key.TrimEnd('.')}"));
        return $"{text}, {count} couldn't be: {reasons.TrimEnd('.')}";
    }

    private static Guid? ProjectOf(string? scopeKey) =>
        scopeKey is not null && scopeKey.StartsWith("project:", StringComparison.Ordinal) && Guid.TryParse(scopeKey["project:".Length..], out var id) ? id : null;

    public async Task<IReadOnlyList<ApprovalInstanceDto>> GetHistoryAsync(string documentType, Guid documentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var instances = await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId)
            .ToListAsync(cancellationToken);
        if (instances.Count == 0)
        {
            return [];
        }

        var me = access.Email;
        if (!instances.Any(i => i.SubmitterEmail == me)
            && !await access.HasAsync(Permission.ApprovalWorkflowsView, cancellationToken)
            && !await engine.IsParticipantAsync(db, documentType, documentId, me, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the submitter, its approvers, Finance and administrators can see this approval history.");
        }

        var ids = instances.Select(i => i.Id).ToList();
        var versionIds = instances.Select(i => i.WorkflowVersionId).Distinct().ToList();
        var versions = await db.Set<ApprovalWorkflowVersion>().AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken);
        var tasks = (await db.Set<ApprovalTask>().AsNoTracking().Where(t => ids.Contains(t.InstanceId)).ToListAsync(cancellationToken)).ToLookup(t => t.InstanceId);
        var actions = (await db.Set<ApprovalAction>().AsNoTracking().Where(a => ids.Contains(a.InstanceId)).ToListAsync(cancellationToken)).ToLookup(a => a.InstanceId);
        var roleLabels = new Dictionary<string, string?>();
        foreach (var key in tasks.SelectMany(g => g).Select(t => t.AssigneeRole).OfType<string>().Distinct())
        {
            roleLabels[key] = await ApproverRoles.LabelAsync(db, key, cancellationToken);
        }

        return instances.OrderByDescending(i => i.StartedAtUtc).Select(i =>
        {
            var steps = tasks[i.Id].ToDictionary(t => t.Id, t => t.StepName);
            return new ApprovalInstanceDto(i.Id, i.DocumentType, i.DocumentId, i.Title, (ApprovalInstanceState)(int)i.Status, versions.GetValueOrDefault(i.WorkflowVersionId),
                i.SubmitterEmail, i.StartedAtUtc, i.CompletedAtUtc,
                tasks[i.Id].OrderBy(t => t.Sequence).ThenBy(t => t.StepIndex).Select(t => new ApprovalTaskDto(t.Id, t.Sequence, t.StepName, t.ScopeLabel,
                    (ApprovalTaskState)(int)t.Status, ApprovalEngine.Split(t.AssigneesCsv).ToList(), t.AssigneeRole is { } r ? roleLabels[r] : null, t.DecidedBy, t.OnBehalfOf, t.DecidedAtUtc,
                    t.Comment, t.SkipReason)).ToList(),
                actions[i.Id].OrderBy(a => a.OccurredAtUtc).Select(a => new ApprovalActionDto(a.Action, a.Actor, a.OnBehalfOf, a.Comment, a.OccurredAtUtc,
                    a.TaskId is { } t ? steps.GetValueOrDefault(t) : null)).ToList());
        }).ToList();
    }
}

/// <summary>Monday to Friday counting (holidays aren't excluded).</summary>
public static class BusinessDays
{
    public static int Between(DateTime fromUtc, DateTime toUtc)
    {
        var day = DateOnly.FromDateTime(fromUtc);
        var end = DateOnly.FromDateTime(toUtc);
        var count = 0;
        while (day < end)
        {
            day = day.AddDays(1);
            if (day.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday))
            {
                count++;
            }
        }

        return count;
    }
}

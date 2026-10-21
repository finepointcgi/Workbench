using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;

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
        return (await engine.ActionableAsync(db, null, access.Email, cancellationToken))
            .SelectMany(x => x.Tasks.Select(t => (x.Instance, t.Task, t.OnBehalfOf)))
            .GroupBy(x => x.Instance.Id)
            .Select(g =>
            {
                var (instance, task, onBehalfOf) = g.OrderBy(x => x.Task.ActivatedAtUtc).First();
                var scopes = g.Select(x => x.Task.ScopeLabel).Where(l => l is not null).Distinct().ToList();
                return new ApprovalInboxItemDto(task.Id, instance.Id, instance.DocumentType, ApprovalDocumentTypes.Label(instance.DocumentType), instance.DocumentId,
                    instance.Title, instance.Summary, instance.Amount, instance.SubmitterEmail, string.Join(", ", g.Select(x => x.Task.StepName).Distinct()),
                    scopes.Count == 0 ? null : string.Join(", ", scopes), task.ActivatedAtUtc,
                    task.ActivatedAtUtc is { } since ? BusinessDays.Between(since, now) : 0, onBehalfOf, instance.Link);
            })
            .OrderBy(i => i.WaitingSinceUtc)
            .ToList();
    }

    public async Task<int> CountMineAsync(CancellationToken cancellationToken = default)
    {
        if (!access.IsAuthenticated)
        {
            return 0;
        }

        await _backfill.RunAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await engine.ActionableAsync(db, null, access.Email, cancellationToken)).Count;
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
            && !(await access.GetOrgRolesAsync(cancellationToken)).IsFinance()
            && !await engine.IsParticipantAsync(db, documentType, documentId, me, cancellationToken))
        {
            throw new WorkbenchForbiddenException("Only the submitter, its approvers, Finance and administrators can see this approval history.");
        }

        var ids = instances.Select(i => i.Id).ToList();
        var versionIds = instances.Select(i => i.WorkflowVersionId).Distinct().ToList();
        var versions = await db.Set<ApprovalWorkflowVersion>().AsNoTracking().Where(v => versionIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Version, cancellationToken);
        var tasks = (await db.Set<ApprovalTask>().AsNoTracking().Where(t => ids.Contains(t.InstanceId)).ToListAsync(cancellationToken)).ToLookup(t => t.InstanceId);
        var actions = (await db.Set<ApprovalAction>().AsNoTracking().Where(a => ids.Contains(a.InstanceId)).ToListAsync(cancellationToken)).ToLookup(a => a.InstanceId);
        return instances.OrderByDescending(i => i.StartedAtUtc).Select(i =>
        {
            var steps = tasks[i.Id].ToDictionary(t => t.Id, t => t.StepName);
            return new ApprovalInstanceDto(i.Id, i.DocumentType, i.DocumentId, i.Title, (ApprovalInstanceState)(int)i.Status, versions.GetValueOrDefault(i.WorkflowVersionId),
                i.SubmitterEmail, i.StartedAtUtc, i.CompletedAtUtc,
                tasks[i.Id].OrderBy(t => t.Sequence).ThenBy(t => t.StepIndex).Select(t => new ApprovalTaskDto(t.Id, t.Sequence, t.StepName, t.ScopeLabel,
                    (ApprovalTaskState)(int)t.Status, ApprovalEngine.Split(t.AssigneesCsv).ToList(), t.AssigneeRole, t.DecidedBy, t.OnBehalfOf, t.DecidedAtUtc,
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

using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Identity;
using Workbench.Domain.Approvals;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Approvals;

/// <summary>One cost on a document, as the workflow sees it: what it charges and who may not approve it.</summary>
public sealed record ApprovalCharge(
    Guid? ProjectId,
    decimal Amount,
    Guid? OrgUnitId = null,
    string? CostType = null,
    bool IsUnallowable = false,
    bool IsAccount = false,
    IReadOnlyCollection<string>? ExcludedEmails = null);

/// <summary>A document being routed: who submitted it, what it costs and where it's charged.</summary>
public sealed record ApprovalSubject(
    string DocumentType,
    Guid DocumentId,
    string Title,
    string SubmitterEmail,
    string Link,
    decimal? Amount = null,
    string? Summary = null,
    Guid? SubmitterEmployeeId = null,
    IReadOnlyList<ApprovalCharge>? Charges = null,
    decimal? PolicyThreshold = null);

/// <summary>The route a submission got: every task (later steps waiting), and who to tell now.</summary>
public sealed record ApprovalRoute(ApprovalInstance Instance, IReadOnlyList<ApprovalTask> Tasks, IReadOnlyList<string> NotifyEmails, bool HasUnassigned)
{
    public IEnumerable<ApprovalTask> Active => Tasks.Where(t => t.Status == ApprovalTaskStatus.Pending);

    /// <summary>The routed approver of a project's charges (the first assignee of its task), or null when they go elsewhere.</summary>
    public string? ProjectApprover(Guid projectId) =>
        Tasks.FirstOrDefault(t => t.ScopeKey == ApprovalEngine.ProjectScope(projectId) && t.Status != ApprovalTaskStatus.Skipped && t.AssigneesCsv.Length > 0)
            ?.AssigneesCsv.Split(',')[0];
}

/// <summary>What the actor may decide on a document right now.</summary>
public sealed record ApprovalCheck(ApprovalInstance Instance, IReadOnlyList<ApprovalTask> Tasks, IReadOnlyList<ApprovalEngine.Actionable> Actionable)
{
    public bool CanDecide => Actionable.Count > 0;
}

public sealed record ApprovalOutcome(ApprovalInstanceStatus Status, IReadOnlyList<ApprovalTask> Decided, IReadOnlyList<ApprovalTask> Activated, IReadOnlyList<string> NotifyEmails)
{
    public bool IsComplete => Status == ApprovalInstanceStatus.Approved;
}

/// <summary>
/// The approval workflow engine. Modules call it inside their own unit of work (it only adds and changes tracked
/// entities; the module's SaveChanges commits both), at submit (<see cref="StartAsync"/>), at a decision
/// (<see cref="CheckAsync"/> then <see cref="DecideAsync"/>) and when a document is recalled or reopened
/// (<see cref="CancelAsync"/>). Modules keep their own states; the engine says who may decide and when it's done.
/// </summary>
public sealed class ApprovalEngine(TimeProvider clock)
{
    public sealed record Actionable(ApprovalTask Task, string? OnBehalfOf);

    private sealed record Delegator(string Email, OrgRoles Roles, string? DocumentType);

    private sealed record Actor(string Email, OrgRoles Roles, IReadOnlyList<Delegator> Delegators);

    public DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public DateOnly Today => DateOnly.FromDateTime(UtcNow);

    public static string ProjectScope(Guid projectId) => $"project:{projectId}";

    public static string OrgUnitScope(Guid orgUnitId) => $"orgunit:{orgUnitId}";

    public static string Normalize(string? email) => (email ?? "").Trim().ToLowerInvariant();

    // ---- Definitions ----

    /// <summary>The workflow and its current version for a document type, creating the built-in default (by "system") the first time.</summary>
    public async Task<(ApprovalWorkflow Workflow, ApprovalWorkflowVersion Version)> CurrentAsync(IWorkbenchDbContext db, string documentType, string actor, CancellationToken ct)
    {
        var workflow = db.Set<ApprovalWorkflow>().Local.FirstOrDefault(w => w.DocumentType == documentType)
            ?? await db.Set<ApprovalWorkflow>().FirstOrDefaultAsync(w => w.DocumentType == documentType, ct);
        if (workflow?.CurrentVersionId is { } versionId
            && (db.Set<ApprovalWorkflowVersion>().Local.FirstOrDefault(v => v.Id == versionId)
                ?? await db.Set<ApprovalWorkflowVersion>().FirstOrDefaultAsync(v => v.Id == versionId, ct)) is { } current)
        {
            return (workflow, current);
        }

        var now = UtcNow;
        if (workflow is null)
        {
            workflow = new ApprovalWorkflow { DocumentType = documentType, Name = ApprovalDefaults.Name(documentType), CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Set<ApprovalWorkflow>().Add(workflow);
        }

        var version = new ApprovalWorkflowVersion
        {
            WorkflowId = workflow.Id,
            Version = 1,
            DefinitionJson = ApprovalDefaults.Serialize(ApprovalDefaults.For(documentType)),
            CreatedBy = "system",
            CreatedAtUtc = now,
            Note = "Built-in default",
        };
        db.Set<ApprovalWorkflowVersion>().Add(version);
        workflow.CurrentVersionId = version.Id;
        return (workflow, version);
    }

    // ---- Submit ----

    /// <summary>
    /// Routes a submitted document: cancels any earlier pending trip, then creates the instance and a task per step and
    /// scope (resolving approvers, conditions and self-approval) and activates the first sequence. With
    /// <paramref name="persist"/> false nothing is added to the context (a preview).
    /// </summary>
    public async Task<ApprovalRoute> StartAsync(IWorkbenchDbContext db, ApprovalSubject subject, CancellationToken ct, bool persist = true)
    {
        var submitter = Normalize(subject.SubmitterEmail);
        if (persist)
        {
            await CancelAsync(db, subject.DocumentType, subject.DocumentId, submitter, "Resubmitted", ct);
        }

        var (_, version) = await CurrentAsync(db, subject.DocumentType, submitter, ct);
        var definition = ApprovalDefaults.Deserialize(version.DefinitionJson);
        var resolver = await Resolver.LoadAsync(db, subject, ct);
        var now = UtcNow;
        var instance = new ApprovalInstance
        {
            DocumentType = subject.DocumentType,
            DocumentId = subject.DocumentId,
            WorkflowVersionId = version.Id,
            SubmitterEmail = submitter,
            Title = Clip(subject.Title, 300)!,
            Summary = Clip(subject.Summary, 500),
            Amount = subject.Amount,
            Link = Clip(subject.Link, 300)!,
            StartedAtUtc = now,
        };

        var tasks = new List<ApprovalTask>();
        var eligible = new Dictionary<ApprovalTask, IReadOnlyList<string>>();
        for (var index = 0; index < definition.Steps.Count; index++)
        {
            var step = definition.Steps[index];
            if (!resolver.Matches(step))
            {
                continue;
            }

            foreach (var draft in await resolver.ResolveAsync(db, step, ct))
            {
                var task = new ApprovalTask
                {
                    InstanceId = instance.Id,
                    StepIndex = index,
                    Sequence = step.Sequence,
                    StepName = Clip(step.Name, 200)!,
                    ScopeKey = draft.ScopeKey,
                    ScopeLabel = Clip(draft.ScopeLabel, 300),
                    AssigneesCsv = string.Join(",", draft.Assignees),
                    AssigneeRole = draft.Role?.ToString(),
                    ExcludedCsv = string.Join(",", draft.Excluded),
                    AdminsMayAct = step.AdminsMayAct,
                    RemindAfterBusinessDays = step.RemindAfterBusinessDays,
                    EscalateToSupervisor = step.EscalateToApproverSupervisor,
                    SkipReason = Clip(draft.SkipReason, 300),
                    Status = draft.SkipReason is null ? ApprovalTaskStatus.Waiting : ApprovalTaskStatus.Skipped,
                };
                tasks.Add(task);
                eligible[task] = draft.Eligible;
            }
        }

        var live = tasks.Where(t => t.Status != ApprovalTaskStatus.Skipped).ToList();
        if (live.Count == 0)
        {
            throw new ArgumentException($"The {ApprovalDocumentTypes.Label(subject.DocumentType).ToLowerInvariant()} approval workflow has no step that applies here "
                + "(every approver is the submitter, or no condition matched). Ask an administrator to check Setup › Approval workflows.");
        }

        instance.CurrentSequence = live.Min(t => t.Sequence);
        foreach (var task in live.Where(t => t.Sequence == instance.CurrentSequence))
        {
            task.Status = ApprovalTaskStatus.Pending;
            task.ActivatedAtUtc = now;
        }

        var active = tasks.Where(t => t.Status == ApprovalTaskStatus.Pending).ToList();
        if (persist)
        {
            db.Set<ApprovalInstance>().Add(instance);
            db.Set<ApprovalTask>().AddRange(tasks);
            AddAction(db, instance, null, "Submitted", submitter, null, null);
            foreach (var skipped in tasks.Where(t => t.Status == ApprovalTaskStatus.Skipped))
            {
                AddAction(db, instance, skipped, "Skipped", "system", null, skipped.SkipReason);
            }
        }

        var notify = active.SelectMany(t => eligible[t]).Distinct().ToList();
        notify.AddRange(await DelegatesOfAsync(db, notify, subject.DocumentType, ct));
        return new ApprovalRoute(instance, tasks, notify.Distinct().Where(e => e != submitter).ToList(), active.Any(t => eligible[t].Count == 0));
    }

    // ---- Decide ----

    /// <summary>
    /// The pending trip of a document and what <paramref name="actorEmail"/> may decide on it. A document submitted
    /// before workflows existed is routed now from <paramref name="subject"/> (saved with the caller's changes).
    /// </summary>
    public async Task<ApprovalCheck> CheckAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string actorEmail,
        Func<CancellationToken, Task<ApprovalSubject>>? subject, CancellationToken ct)
    {
        var instance = await db.Set<ApprovalInstance>()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId && i.Status == ApprovalInstanceStatus.Pending)
            .OrderByDescending(i => i.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
        List<ApprovalTask> tasks;
        if (instance is null)
        {
            if (subject is null)
            {
                throw new InvalidOperationException("This document isn't waiting for approval.");
            }

            var route = await StartAsync(db, await subject(ct), ct);
            instance = route.Instance;
            tasks = route.Tasks.ToList();
        }
        else
        {
            tasks = await db.Set<ApprovalTask>().Where(t => t.InstanceId == instance.Id).ToListAsync(ct);
        }

        var actor = await ActorAsync(db, actorEmail, ct);
        var actionable = tasks.Where(t => t.Status == ApprovalTaskStatus.Pending)
            .Select(t => (Task: t, Can: CanAct(t, actor, documentType)))
            .Where(x => x.Can.Allowed)
            .Select(x => new Actionable(x.Task, x.Can.OnBehalfOf))
            .ToList();
        return new ApprovalCheck(instance, tasks.OrderBy(t => t.Sequence).ThenBy(t => t.StepIndex).ToList(), actionable);
    }

    /// <summary>
    /// Records the actor's decision on every task they may decide. Approving advances to the next sequence when the
    /// current one is done, and completes the instance after the last; rejecting or returning ends it.
    /// </summary>
    public async Task<ApprovalOutcome> DecideAsync(IWorkbenchDbContext db, ApprovalCheck check, ApprovalDecisionKind decision, string actorEmail, string? comment, CancellationToken ct)
    {
        if (!check.CanDecide)
        {
            throw new InvalidOperationException("Nothing on this document is waiting on you.");
        }

        var actor = Normalize(actorEmail);
        var now = UtcNow;
        var instance = check.Instance;
        var decided = new List<ApprovalTask>();
        var status = decision switch
        {
            ApprovalDecisionKind.Approve => ApprovalTaskStatus.Approved,
            ApprovalDecisionKind.Return => ApprovalTaskStatus.Returned,
            _ => ApprovalTaskStatus.Rejected,
        };
        foreach (var (task, onBehalfOf) in check.Actionable)
        {
            task.Status = status;
            task.DecidedBy = actor;
            task.OnBehalfOf = onBehalfOf;
            task.DecidedAtUtc = now;
            task.Comment = Clip(comment, 1000);
            decided.Add(task);
            AddAction(db, instance, task, status.ToString(), actor, onBehalfOf, comment);
        }

        var activated = new List<ApprovalTask>();
        if (decision != ApprovalDecisionKind.Approve)
        {
            foreach (var other in check.Tasks.Where(t => t.Status is ApprovalTaskStatus.Pending or ApprovalTaskStatus.Waiting))
            {
                other.Status = ApprovalTaskStatus.Cancelled;
            }

            instance.Status = decision == ApprovalDecisionKind.Return ? ApprovalInstanceStatus.Returned : ApprovalInstanceStatus.Rejected;
            instance.CompletedAtUtc = now;
            return new ApprovalOutcome(instance.Status, decided, activated, []);
        }

        if (!check.Tasks.Any(t => t.Status == ApprovalTaskStatus.Pending))
        {
            var next = check.Tasks.Where(t => t.Status == ApprovalTaskStatus.Waiting).Select(t => (int?)t.Sequence).Min();
            if (next is { } sequence)
            {
                instance.CurrentSequence = sequence;
                foreach (var task in check.Tasks.Where(t => t.Status == ApprovalTaskStatus.Waiting && t.Sequence == sequence))
                {
                    task.Status = ApprovalTaskStatus.Pending;
                    task.ActivatedAtUtc = now;
                    activated.Add(task);
                    AddAction(db, instance, task, "Activated", "system", null, null);
                }
            }
            else
            {
                instance.Status = ApprovalInstanceStatus.Approved;
                instance.CompletedAtUtc = now;
                AddAction(db, instance, null, "Completed", actor, null, null);
            }
        }

        var notify = new List<string>();
        foreach (var task in activated)
        {
            notify.AddRange(await EligibleAsync(db, task, ct));
        }

        notify.AddRange(await DelegatesOfAsync(db, notify, instance.DocumentType, ct));
        return new ApprovalOutcome(instance.Status, decided, activated, notify.Distinct().Where(e => e != actor && e != instance.SubmitterEmail).ToList());
    }

    /// <summary>Turns the actor's latest rejection on the document into "returned for changes".</summary>
    public async Task MarkReturnedAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string actorEmail, CancellationToken ct)
    {
        var actor = Normalize(actorEmail);
        var instance = await db.Set<ApprovalInstance>()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId && i.Status == ApprovalInstanceStatus.Rejected)
            .OrderByDescending(i => i.CompletedAtUtc).FirstOrDefaultAsync(ct);
        if (instance is null)
        {
            return;
        }

        instance.Status = ApprovalInstanceStatus.Returned;
        foreach (var task in await db.Set<ApprovalTask>().Where(t => t.InstanceId == instance.Id && t.Status == ApprovalTaskStatus.Rejected && t.DecidedBy == actor).ToListAsync(ct))
        {
            task.Status = ApprovalTaskStatus.Returned;
        }

        AddAction(db, instance, null, "Returned", actor, null, "Returned for changes");
    }

    /// <summary>Cancels the document's pending trip (recalled, reopened, voided or resubmitted).</summary>
    public async Task CancelAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string actorEmail, string? reason, CancellationToken ct)
    {
        var pending = await db.Set<ApprovalInstance>()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId && i.Status == ApprovalInstanceStatus.Pending)
            .ToListAsync(ct);
        foreach (var instance in pending)
        {
            instance.Status = ApprovalInstanceStatus.Cancelled;
            instance.CompletedAtUtc = UtcNow;
            foreach (var task in await db.Set<ApprovalTask>()
                .Where(t => t.InstanceId == instance.Id && (t.Status == ApprovalTaskStatus.Pending || t.Status == ApprovalTaskStatus.Waiting)).ToListAsync(ct))
            {
                task.Status = ApprovalTaskStatus.Cancelled;
            }

            AddAction(db, instance, null, "Cancelled", Normalize(actorEmail), null, reason);
        }
    }

    // ---- Queries ----

    /// <summary>Pending tasks the actor may decide, by document, for one document type (or all when null).</summary>
    public async Task<IReadOnlyList<(ApprovalInstance Instance, IReadOnlyList<Actionable> Tasks)>> ActionableAsync(
        IWorkbenchDbContext db, string? documentType, string actorEmail, CancellationToken ct)
    {
        var actor = await ActorAsync(db, actorEmail, ct);
        // Should two pending trips ever exist for a document (a race), only the latest counts, as in CheckAsync.
        var instances = (await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.Status == ApprovalInstanceStatus.Pending && (documentType == null || i.DocumentType == documentType))
            .ToListAsync(ct))
            .GroupBy(i => (i.DocumentType, i.DocumentId)).Select(g => g.MaxBy(i => i.StartedAtUtc)!).ToList();
        if (instances.Count == 0)
        {
            return [];
        }

        var ids = instances.Select(i => i.Id).ToList();
        var tasks = (await db.Set<ApprovalTask>().AsNoTracking()
            .Where(t => t.Status == ApprovalTaskStatus.Pending && ids.Contains(t.InstanceId)).ToListAsync(ct))
            .ToLookup(t => t.InstanceId);
        var result = new List<(ApprovalInstance, IReadOnlyList<Actionable>)>();
        foreach (var instance in instances)
        {
            var mine = tasks[instance.Id].Select(t => (Task: t, Can: CanAct(t, actor, instance.DocumentType)))
                .Where(x => x.Can.Allowed).Select(x => new Actionable(x.Task, x.Can.OnBehalfOf)).ToList();
            if (mine.Count > 0)
            {
                result.Add((instance, mine));
            }
        }

        return result;
    }

    /// <summary>
    /// For a document submitted before workflows existed (no trip yet): whether the actor could decide it, routed now
    /// in memory. Nothing is saved.
    /// </summary>
    public async Task<bool> PreviewCanDecideAsync(IWorkbenchDbContext db, ApprovalSubject subject, string actorEmail, CancellationToken ct)
    {
        ApprovalRoute route;
        try
        {
            route = await StartAsync(db, subject, ct, persist: false);
        }
        catch (ArgumentException)
        {
            return false;
        }

        var actor = await ActorAsync(db, actorEmail, ct);
        return route.Active.Any(t => CanAct(t, actor, subject.DocumentType).Allowed);
    }

    /// <summary>Read-only: whether the actor may decide the document now (its pending trip, or a preview for an unrouted one).</summary>
    public async Task<bool> CanDecideAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string actorEmail,
        Func<CancellationToken, Task<ApprovalSubject>>? subject, CancellationToken ct) =>
        (await ActionableForAsync(db, documentType, documentId, actorEmail, subject, ct)).Count > 0;

    /// <summary>Read-only: the pending tasks the actor may decide on one document (previewed for an unrouted one).</summary>
    public async Task<IReadOnlyList<ApprovalTask>> ActionableForAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string actorEmail,
        Func<CancellationToken, Task<ApprovalSubject>>? subject, CancellationToken ct)
    {
        var instance = await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId && i.Status == ApprovalInstanceStatus.Pending)
            .OrderByDescending(i => i.StartedAtUtc)
            .FirstOrDefaultAsync(ct);
        var actor = await ActorAsync(db, actorEmail, ct);
        IEnumerable<ApprovalTask> pending;
        if (instance is null)
        {
            if (subject is null)
            {
                return [];
            }

            try
            {
                pending = (await StartAsync(db, await subject(ct), ct, persist: false)).Active;
            }
            catch (ArgumentException)
            {
                return [];
            }
        }
        else
        {
            pending = await db.Set<ApprovalTask>().AsNoTracking().Where(t => t.InstanceId == instance.Id && t.Status == ApprovalTaskStatus.Pending).ToListAsync(ct);
        }

        return pending.Where(t => CanAct(t, actor, documentType).Allowed).ToList();
    }

    /// <summary>Document ids of a type with a pending trip (submitted through the engine).</summary>
    public async Task<HashSet<Guid>> RoutedDocumentIdsAsync(IWorkbenchDbContext db, string documentType, CancellationToken ct) =>
        (await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.DocumentType == documentType && i.Status == ApprovalInstanceStatus.Pending)
            .Select(i => i.DocumentId).ToListAsync(ct)).ToHashSet();

    /// <summary>True when the email was ever an approver on the document (assigned, delegated or decided).</summary>
    public async Task<bool> IsParticipantAsync(IWorkbenchDbContext db, string documentType, Guid documentId, string email, CancellationToken ct)
    {
        var me = Normalize(email);
        var instanceIds = await db.Set<ApprovalInstance>().AsNoTracking()
            .Where(i => i.DocumentType == documentType && i.DocumentId == documentId).Select(i => i.Id).ToListAsync(ct);
        if (instanceIds.Count == 0)
        {
            return false;
        }

        var tasks = await db.Set<ApprovalTask>().AsNoTracking().Where(t => instanceIds.Contains(t.InstanceId)).ToListAsync(ct);
        return tasks.Any(t => t.DecidedBy == me || Split(t.AssigneesCsv).Contains(me))
            || (await ActionableAsync(db, documentType, me, ct)).Any(x => x.Instance.DocumentId == documentId);
    }

    /// <summary>Who can decide a task now: its assignees and its role's members, less the excluded.</summary>
    public async Task<IReadOnlyList<string>> EligibleAsync(IWorkbenchDbContext db, ApprovalTask task, CancellationToken ct)
    {
        var excluded = Split(task.ExcludedCsv);
        var people = Split(task.AssigneesCsv).ToList();
        if (Enum.TryParse<ApprovalOrgRole>(task.AssigneeRole, out var role))
        {
            people.AddRange(await RoleMembersAsync(db, role, ct));
        }

        return people.Where(p => !excluded.Contains(p)).Distinct().ToList();
    }

    public static HashSet<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Normalize).ToHashSet();

    public static bool HasRole(OrgRoles roles, ApprovalOrgRole role) => role switch
    {
        ApprovalOrgRole.Finance => roles.IsFinance(),
        ApprovalOrgRole.Admin => roles.IsAdmin(),
        ApprovalOrgRole.ResourceManager => roles.IsResourceManager(),
        ApprovalOrgRole.ProjectManager => roles.IsProjectManager(),
        _ => roles.HasFlag(OrgRoles.Timekeeper),
    };

    public static async Task<IReadOnlyList<string>> RoleMembersAsync(IWorkbenchDbContext db, ApprovalOrgRole role, CancellationToken ct)
    {
        var users = db.Set<AppUser>().AsNoTracking().Where(u => u.IsActive);
        users = role switch
        {
            ApprovalOrgRole.Finance => users.Where(u => u.IsFinance || u.IsAdmin),
            ApprovalOrgRole.Admin => users.Where(u => u.IsAdmin),
            ApprovalOrgRole.ResourceManager => users.Where(u => u.IsResourceManager || u.IsAdmin),
            ApprovalOrgRole.ProjectManager => users.Where(u => u.IsProjectManager || u.IsAdmin),
            _ => users.Where(u => u.IsTimekeeper),
        };
        return (await users.Select(u => u.Email).ToListAsync(ct)).Select(Normalize).ToList();
    }

    public static async Task<OrgRoles> RolesOfAsync(IWorkbenchDbContext db, string email, CancellationToken ct)
    {
        var normalized = Normalize(email);
        var user = await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == normalized && u.IsActive)
            .Select(u => new { u.IsAdmin, u.IsFinance, u.IsTimekeeper, u.IsResourceManager, u.IsProjectManager }).FirstOrDefaultAsync(ct);
        return user is null ? OrgRoles.Employee : Identity.AccessControl.ToRoles(user.IsAdmin, user.IsFinance, user.IsTimekeeper, user.IsResourceManager, user.IsProjectManager);
    }

    public void AddAction(IWorkbenchDbContext db, ApprovalInstance instance, ApprovalTask? task, string action, string actor, string? onBehalfOf, string? comment) =>
        db.Set<ApprovalAction>().Add(new ApprovalAction
        {
            InstanceId = instance.Id,
            TaskId = task?.Id,
            Action = action,
            Actor = Clip(actor, 320)!,
            OnBehalfOf = onBehalfOf,
            Comment = Clip(comment, 1000),
            OccurredAtUtc = UtcNow,
        });

    // ---- Internals ----

    private async Task<Actor> ActorAsync(IWorkbenchDbContext db, string email, CancellationToken ct)
    {
        var me = Normalize(email);
        var today = Today;
        var delegations = await db.Set<ApprovalDelegation>().AsNoTracking()
            .Where(d => d.DelegateEmail == me && d.RevokedAtUtc == null && d.StartsOn <= today && d.EndsOn >= today)
            .ToListAsync(ct);
        var delegators = new List<Delegator>();
        foreach (var d in delegations)
        {
            delegators.Add(new Delegator(Normalize(d.DelegatorEmail), await RolesOfAsync(db, d.DelegatorEmail, ct), d.DocumentType));
        }

        return new Actor(me, await RolesOfAsync(db, me, ct), delegators);
    }

    private static (bool Allowed, string? OnBehalfOf) CanAct(ApprovalTask task, Actor actor, string documentType)
    {
        var excluded = Split(task.ExcludedCsv);
        if (excluded.Contains(actor.Email))
        {
            return (false, null);
        }

        var assignees = Split(task.AssigneesCsv);
        var hasRole = Enum.TryParse<ApprovalOrgRole>(task.AssigneeRole, out var role);
        if (assignees.Contains(actor.Email) || (hasRole && HasRole(actor.Roles, role)) || (task.AdminsMayAct && actor.Roles.IsAdmin()))
        {
            return (true, null);
        }

        foreach (var d in actor.Delegators.Where(d => d.DocumentType is null || d.DocumentType == documentType))
        {
            if (!excluded.Contains(d.Email) && (assignees.Contains(d.Email) || (hasRole && HasRole(d.Roles, role))))
            {
                return (true, d.Email);
            }
        }

        return (false, null);
    }

    /// <summary>Active delegates (today) of any of the emails, so they hear about work waiting on whoever they stand in for.</summary>
    public async Task<IReadOnlyList<string>> DelegatesOfAsync(IWorkbenchDbContext db, IReadOnlyCollection<string> emails, string documentType, CancellationToken ct)
    {
        if (emails.Count == 0)
        {
            return [];
        }

        var today = Today;
        var list = emails.ToList();
        return (await db.Set<ApprovalDelegation>().AsNoTracking()
            .Where(d => list.Contains(d.DelegatorEmail) && d.RevokedAtUtc == null && d.StartsOn <= today && d.EndsOn >= today
                && (d.DocumentType == null || d.DocumentType == documentType))
            .Select(d => d.DelegateEmail).ToListAsync(ct)).Select(Normalize).ToList();
    }

    private static string? Clip(string? value, int max) => value is null ? null : value.Length <= max ? value : value[..max];

    private sealed record Draft(string? ScopeKey, string? ScopeLabel, IReadOnlyList<string> Assignees, ApprovalOrgRole? Role,
        IReadOnlyCollection<string> Excluded, IReadOnlyList<string> Eligible, string? SkipReason);

    /// <summary>Resolves approver rules and conditions for one subject.</summary>
    private sealed class Resolver
    {
        private sealed record ProjectInfo(Guid Id, string Code, string Name, Guid? ContractId, string? ManagerEmail);

        private ApprovalSubject _subject = null!;
        private string _submitter = "";
        private Employee? _submitterEmployee;
        private string? _supervisorEmail;
        private Dictionary<Guid, ProjectInfo> _projects = [];
        private IReadOnlyList<ApprovalCharge> _charges = [];

        public static async Task<Resolver> LoadAsync(IWorkbenchDbContext db, ApprovalSubject subject, CancellationToken ct)
        {
            var r = new Resolver { _subject = subject, _submitter = Normalize(subject.SubmitterEmail), _charges = subject.Charges ?? [] };
            r._submitterEmployee = subject.SubmitterEmployeeId is { } employeeId
                ? await db.Set<Employee>().AsNoTracking().FirstOrDefaultAsync(e => e.Id == employeeId, ct)
                : await Identity.EmployeeLinks.FindForUserAsync(db, r._submitter, ct);
            if (r._submitterEmployee?.SupervisorId is { } supervisorId)
            {
                r._supervisorEmail = await db.Set<Employee>().AsNoTracking().Where(e => e.Id == supervisorId).Select(e => e.Email).FirstOrDefaultAsync(ct);
            }

            var projectIds = r._charges.Where(c => c.ProjectId is not null).Select(c => c.ProjectId!.Value).Distinct().ToList();
            var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Code, p.Name, p.ContractId, p.ProjectManagerId }).ToListAsync(ct);
            var managerIds = projects.Where(p => p.ProjectManagerId != null).Select(p => p.ProjectManagerId!.Value).Distinct().ToList();
            var managers = await db.Set<Employee>().AsNoTracking().Where(e => managerIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Email, ct);
            r._projects = projects.ToDictionary(p => p.Id, p => new ProjectInfo(p.Id, p.Code, p.Name, p.ContractId,
                p.ProjectManagerId is { } m && managers.TryGetValue(m, out var email) ? Normalize(email) : null));
            return r;
        }

        // A requester submitting for someone else (e.g. an expense report) is still "the submitter" for self-approval.
        private bool IsSelf(string email) =>
            email == _submitter || (_submitterEmployee is not null && email == Normalize(_submitterEmployee.Email));

        public bool Matches(ApprovalStepDefinition step)
        {
            var conditions = step.Conditions ?? [];
            if (conditions.Count == 0)
            {
                return true;
            }

            return step.Match == ConditionMatch.Any ? conditions.Any(Matches) : conditions.All(Matches);
        }

        private bool Matches(ApprovalCondition c)
        {
            var amount = _subject.Amount ?? _charges.Sum(x => x.Amount);
            var ids = c.Ids ?? [];
            return c.Kind switch
            {
                ApprovalConditionKind.AmountOver => c.Amount is { } over && amount > over,
                ApprovalConditionKind.AmountAtMost => c.Amount is { } most && amount <= most,
                ApprovalConditionKind.AmountOverPolicyThreshold => _subject.PolicyThreshold is { } t && amount > t,
                ApprovalConditionKind.ProjectIn => _charges.Any(x => x.ProjectId is { } p && ids.Contains(p)),
                ApprovalConditionKind.ContractIn => _charges.Any(x => x.ProjectId is { } p && _projects.GetValueOrDefault(p)?.ContractId is { } k && ids.Contains(k)),
                ApprovalConditionKind.OrgUnitIn => _charges.Any(x => x.OrgUnitId is { } o && ids.Contains(o))
                    || (_submitterEmployee?.OrgUnitId is { } own && ids.Contains(own)),
                ApprovalConditionKind.HasUnallowableCost => _charges.Any(x => x.IsUnallowable),
                ApprovalConditionKind.CostTypeIn => _charges.Any(x => x.CostType is { } t && (c.Values ?? []).Contains(t, StringComparer.OrdinalIgnoreCase)),
                ApprovalConditionKind.HasAccountCharges => _charges.Any(x => x.IsAccount),
                ApprovalConditionKind.HasUnroutedCharges => _charges.Any(x => x.IsAccount || x.ProjectId is not { } p
                    || _projects.GetValueOrDefault(p)?.ManagerEmail is not { } pm || IsSelf(pm)),
                _ => false,
            };
        }

        public async Task<IReadOnlyList<Draft>> ResolveAsync(IWorkbenchDbContext db, ApprovalStepDefinition step, CancellationToken ct)
        {
            var scopes = new List<(string? Key, string? Label, IReadOnlyList<string> People, ApprovalOrgRole? Role, IReadOnlyList<ApprovalCharge> Charges)>();
            var rule = step.Approver;
            switch (rule.Kind)
            {
                case ApproverRuleKind.ProjectManager:
                    foreach (var group in _charges.Where(c => !c.IsAccount && c.ProjectId is not null).GroupBy(c => c.ProjectId!.Value))
                    {
                        var project = _projects.GetValueOrDefault(group.Key);
                        scopes.Add((ProjectScope(group.Key), project is null ? null : $"{project.Code} {project.Name}",
                            project?.ManagerEmail is { } pm ? [pm] : [], null, group.ToList()));
                    }

                    break;
                case ApproverRuleKind.OrgUnitManager:
                    var units = _charges.Select(c => c.OrgUnitId ?? _submitterEmployee?.OrgUnitId).Where(u => u is not null).Select(u => u!.Value).Distinct().ToList();
                    if (units.Count == 0 && _submitterEmployee?.OrgUnitId is { } ownUnit)
                    {
                        units.Add(ownUnit);
                    }

                    foreach (var unitId in units)
                    {
                        var unit = await db.Set<OrgUnit>().AsNoTracking().FirstOrDefaultAsync(u => u.Id == unitId, ct);
                        var approver = await db.Set<OrgUnitApprover>().AsNoTracking().Where(a => a.OrgUnitId == unitId)
                            .Join(db.Set<Employee>(), a => a.EmployeeId, e => e.Id, (a, e) => e.Email).FirstOrDefaultAsync(ct);
                        scopes.Add((OrgUnitScope(unitId), unit is null ? null : $"{unit.Code} {unit.Name}",
                            approver is null ? [] : [Normalize(approver)],
                            null, _charges.Where(c => (c.OrgUnitId ?? _submitterEmployee?.OrgUnitId) == unitId).ToList()));
                    }

                    break;
                case ApproverRuleKind.SpecificPeople:
                    foreach (var person in (rule.People ?? []).Select(Normalize).Where(p => p.Length > 0).Distinct())
                    {
                        scopes.Add(($"person:{person}", person, [person], null, _charges));
                    }

                    break;
                default:
                    var (people, role) = WholeDocument(rule);
                    scopes.Add((null, null, people, role, _charges));
                    break;
            }

            var drafts = new List<Draft>();
            foreach (var scope in scopes)
            {
                var people = scope.People;
                var role = scope.Role;
                if (people.Count == 0 && role is null && step.Fallback is { } fallback)
                {
                    (people, role) = WholeDocument(fallback);
                }

                var members = role is { } r ? await RoleMembersAsync(db, r, ct) : [];
                var excluded = scope.Charges.SelectMany(c => c.ExcludedEmails ?? []).Select(Normalize).ToHashSet();
                if (step.WhenSubmitterIsApprover != SelfApprovalPolicy.Allow)
                {
                    excluded.Add(_submitter);
                    if (_submitterEmployee is not null)
                    {
                        excluded.Add(Normalize(_submitterEmployee.Email));
                    }
                }

                var candidates = people.Concat(members).Distinct().ToList();
                var eligible = candidates.Where(p => !excluded.Contains(p)).ToList();
                string? skip = null;
                if (eligible.Count == 0)
                {
                    var self = candidates.Any(IsSelf);
                    switch (step.WhenSubmitterIsApprover)
                    {
                        case SelfApprovalPolicy.Skip:
                            skip = self ? "The approver is the submitter" : "No approver found";
                            break;
                        case SelfApprovalPolicy.SubmitterSupervisor when self && _supervisorEmail is { } sup && !excluded.Contains(Normalize(sup)):
                            people = [Normalize(sup)];
                            role = null;
                            eligible = [Normalize(sup)];
                            break;
                    }
                }

                drafts.Add(new Draft(scope.Key, scope.Label, people.Where(p => !excluded.Contains(p)).ToList(), role, excluded, eligible, skip));
            }

            return drafts;
        }

        private (IReadOnlyList<string> People, ApprovalOrgRole? Role) WholeDocument(ApproverRule rule) => rule.Kind switch
        {
            ApproverRuleKind.Supervisor => (_supervisorEmail is { } s ? [Normalize(s)] : [], null),
            ApproverRuleKind.OrgRole => ([], rule.Role ?? ApprovalOrgRole.Admin),
            ApproverRuleKind.AnyOf or ApproverRuleKind.SpecificPeople => ((rule.People ?? []).Select(Normalize).Where(p => p.Length > 0).Distinct().ToList(), null),
            // A project or org-unit rule used as a fallback has no single scope to follow: nobody.
            _ => ([], null),
        };
    }
}

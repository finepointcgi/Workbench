using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Collaboration;
using Workbench.Application.Scheduling;
using Workbench.Application.Tasks;
using Workbench.Contracts.Collaboration;
using Workbench.Domain;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Integrations.GitLab;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Scheduling;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>What one sync pass did, for the run log and "Sync now".</summary>
public sealed record GitLabSyncReport(int Synced, int Created, int NewIssues, int Conflicts, int Failed, string? Error = null)
{
    public static GitLabSyncReport Empty { get; } = new(0, 0, 0, 0, 0);

    public GitLabSyncReport Add(GitLabSyncReport other) =>
        new(Synced + other.Synced, Created + other.Created, NewIssues + other.NewIssues, Conflicts + other.Conflicts, Failed + other.Failed, Error ?? other.Error);
}

/// <summary>
/// Syncs a linked project's plan with GitLab. Every pair (a task and its issue, epic or milestone) is synced the same way
/// whichever side changed: read both, compare each field with the value both sides last agreed on (the base), and apply the
/// ownership rules:
/// <list type="bullet">
/// <item>Workbench owns the schedule (dates, WBS code, hours, phase, dependencies): a GitLab edit is put back and noted.</item>
/// <item>GitLab owns work state (open/closed, % complete): a Workbench edit is put back and noted. A closed issue is 100%.</item>
/// <item>Title, description and assignees are shared: the side that changed wins; when both did, the later edit wins and the other
/// value is noted on the sync panel.</item>
/// </list>
/// Nothing is dropped silently: every change goes into the task's activity log and every override into a conflict note.
/// Issues added in GitLab under a linked epic or milestone wait for approval before they become tasks.
/// </summary>
public sealed class GitLabSyncEngine(
    IWorkbenchDbContextFactory dbFactory,
    GitLabClientFactory clients,
    GitLabPeople people,
    ISheetChangePublisher publisher,
    TimeProvider clock,
    ILogger<GitLabSyncEngine> logger)
{
    /// <summary>The actor of every change the sync makes in Workbench (activity, row stamps, live updates).</summary>
    public const string Actor = "GitLab";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ===================================================================== entry points

    /// <summary>Syncs one task (after it changed in Workbench, or was deleted): creates its issue, epic or milestone when needed.</summary>
    public async Task<GitLabSyncReport> SyncTaskAsync(Guid linkId, Guid itemId, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return GitLabSyncReport.Empty;
        }

        var report = await SyncItemAsync(ctx, itemId, markers: null, ct);
        ctx.Synced();
        await ctx.SaveAsync(ct);
        return report;
    }

    /// <summary>Syncs whatever task an issue is linked to, or notes the issue for approval when it's new under a linked phase.</summary>
    public async Task<GitLabSyncReport> SyncIssueAsync(Guid linkId, long iid, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return GitLabSyncReport.Empty;
        }

        var issue = await ctx.Client.GetIssueAsync(ctx.Link.GitLabProjectId, iid, ct);
        var report = await PullIssueAsync(ctx, issue, ct);
        ctx.Synced();
        await ctx.SaveAsync(ct);
        return report;
    }

    /// <summary>Syncs the phase linked to an epic (by iid) or milestone (by id).</summary>
    public async Task<GitLabSyncReport> SyncPhaseObjectAsync(Guid linkId, GitLabObjectKind kind, long idOrIid, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return GitLabSyncReport.Empty;
        }

        var link = kind == GitLabObjectKind.Epic
            ? ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == kind && l.Iid == idOrIid)
            : ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == kind && l.GitLabId == idOrIid);
        if (link is null || ctx.Items.GetValueOrDefault(link.ItemId) is not { } item)
        {
            return GitLabSyncReport.Empty;
        }

        var report = await SyncPairAsync(ctx, item, link, null, ct);
        ctx.Synced();
        await ctx.SaveAsync(ct);
        return report;
    }

    /// <summary>
    /// The backstop: reads what changed in GitLab since the last reconcile (issues, epics, milestones), then compares every
    /// task with what was last sent, creating and sending what's missing or changed. <paramref name="everything"/> also reads
    /// every GitLab issue (the first sync after linking, and "Sync now").
    /// </summary>
    public async Task<GitLabSyncReport> ReconcileAsync(Guid linkId, bool everything, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return GitLabSyncReport.Empty;
        }

        var report = GitLabSyncReport.Empty;
        var started = Now;
        try
        {
            var since = everything ? null : ctx.Link.ReconcileCursorUtc;
            var issues = await ctx.Client.ListIssuesAsync(ctx.Link.GitLabProjectId, since, ct);
            var markers = new Dictionary<string, GitLabIssue>(StringComparer.Ordinal);
            var latest = since ?? DateTime.MinValue;
            foreach (var issue in issues)
            {
                latest = issue.UpdatedAt > latest ? issue.UpdatedAt : latest;
                if (issue.Description is { } d && d.Contains("workbench-task:", StringComparison.Ordinal))
                {
                    foreach (var item in ctx.Items.Values)
                    {
                        if (d.Contains(GitLabRules.Marker(item.Id), StringComparison.Ordinal))
                        {
                            markers[GitLabRules.Marker(item.Id)] = issue;
                        }
                    }
                }

                var linked = ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == GitLabObjectKind.Issue && l.GitLabId == issue.Id);
                if (!everything && linked is not null && linked.GitLabUpdatedAtUtc is { } seen && issue.UpdatedAt <= seen)
                {
                    continue; // nothing new on GitLab's side; Workbench's side is checked below
                }

                report = report.Add(await GuardAsync(ctx, linked?.ItemId, () => PullIssueAsync(ctx, issue, ct)));
            }

            if (ctx.Link.PhaseMode == GitLabPhaseMode.Epics && ctx.Link.GitLabGroupId is { } groupId)
            {
                foreach (var epic in await ctx.Client.ListEpicsAsync(groupId, since, ct))
                {
                    var linked = ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == GitLabObjectKind.Epic && l.GitLabId == epic.Id);
                    if (linked is not null && ctx.Items.GetValueOrDefault(linked.ItemId) is { } item && !(linked.GitLabUpdatedAtUtc >= epic.UpdatedAt))
                    {
                        report = report.Add(await GuardAsync(ctx, item.Id, () => SyncPairAsync(ctx, item, linked, epic, ct)));
                    }
                }
            }
            else if (ctx.Link.PhaseMode == GitLabPhaseMode.Milestones)
            {
                foreach (var milestone in await ctx.Client.ListMilestonesAsync(ctx.Link.GitLabProjectId, ct))
                {
                    var linked = ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == GitLabObjectKind.Milestone && l.GitLabId == milestone.Id);
                    if (linked is not null && ctx.Items.GetValueOrDefault(linked.ItemId) is { } item && !(linked.GitLabUpdatedAtUtc >= milestone.UpdatedAt))
                    {
                        report = report.Add(await GuardAsync(ctx, item.Id, () => SyncPairAsync(ctx, item, linked, milestone, ct)));
                    }
                }
            }

            // Workbench's side: create what's missing, send what changed since the last sync, close what was deleted.
            foreach (var item in ctx.Shape.InCreateOrder().ToList())
            {
                var linked = ctx.ItemLinks.GetValueOrDefault(item.Id);
                if (linked is null || NeedsPush(ctx, item, linked))
                {
                    report = report.Add(await GuardAsync(ctx, item.Id, () => SyncItemAsync(ctx, item.Id, markers, ct)));
                }
            }

            foreach (var removed in ctx.ItemLinks.Values.Where(l => !l.IsRemoved && !ctx.Items.ContainsKey(l.ItemId)).ToList())
            {
                report = report.Add(await GuardAsync(ctx, null, () => SyncItemAsync(ctx, removed.ItemId, markers, ct)));
            }

            ctx.Link.ReconcileCursorUtc = latest == DateTime.MinValue ? started.AddMinutes(-1) : latest.AddMinutes(-1);
            ctx.Link.LastReconcileAtUtc = started;
            if (report.Failed == 0)
            {
                ctx.Link.Status = GitLabLinkStatus.Active;
                ctx.Link.LastError = null;
                ctx.Link.LastErrorHint = null;
                ctx.Link.LastSyncedAtUtc = Now;
            }
        }
        catch (GitLabApiException ex)
        {
            ctx.Link.LastError = GitLabRules.Clip(ex.Message, 1000);
            ctx.Link.LastErrorHint = GitLabRules.Clip(ex.Hint, 1000);
            ctx.Link.LastReconcileAtUtc = started;
            if (!ex.IsRetryable)
            {
                ctx.Link.Status = GitLabLinkStatus.NeedsAttention;
            }

            report = report with { Error = ex.Message, Failed = report.Failed + 1 };
        }

        await ctx.SaveAsync(ct);
        return report;
    }

    /// <summary>Makes an issue added in GitLab a task under its phase and links them (the approval on the sync panel).</summary>
    public async Task<Guid> ApprovePendingAsync(Guid linkId, Guid pendingId, string decidedBy, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct) ?? throw new ArgumentException("This project isn't linked to GitLab.");
        var pending = await ctx.Db.Set<GitLabPendingIssue>().FirstOrDefaultAsync(p => p.Id == pendingId && p.LinkId == linkId, ct)
            ?? throw Contracts.WorkbenchNotFoundException.For("New GitLab issue", pendingId);
        if (pending.Status != GitLabPendingStatus.Pending)
        {
            throw new ArgumentException("This issue was already decided.");
        }

        var issue = await ctx.Client.GetIssueAsync(ctx.Link.GitLabProjectId, pending.Iid, ct);
        if (ctx.ItemLinks.Values.Any(l => l.Kind == GitLabObjectKind.Issue && l.GitLabId == issue.Id))
        {
            throw new ArgumentException("This issue is already linked to a task.");
        }

        var parent = pending.PhaseItemId is { } p && ctx.Items.ContainsKey(p) ? p : (Guid?)null;
        var order = ctx.Items.Values.Where(i => i.ParentId == parent).Select(i => i.Order).DefaultIfEmpty(-1).Max() + 1;
        var now = Now;
        var item = new Item
        {
            SheetId = ctx.Sheet.Id,
            ParentId = parent,
            Order = order,
            Title = GitLabRules.Clip(issue.Title.Trim(), 500),
            Finish = issue.DueDate,
            Start = issue.DueDate,
            WbsCodeIsAuto = true,
            CreatedAtUtc = now,
            CreatedBy = decidedBy,
            ModifiedAtUtc = now,
            ModifiedBy = Actor,
        };
        if (ctx.NotesColumn is { } notes && GitLabRules.Strip(issue.Description) is { } text)
        {
            item.Values = new Dictionary<string, string?>(item.Values) { [notes.Id.ToString()] = text };
        }

        ctx.Db.Items.Add(item);
        ctx.Items[item.Id] = item;
        var link = new GitLabItemLink
        {
            LinkId = linkId, ItemId = item.Id, Kind = GitLabObjectKind.Issue, GitLabId = issue.Id, Iid = issue.Iid, WebUrl = issue.WebUrl,
            BaseTitle = item.Title, BaseDescription = GitLabRules.Strip(issue.Description), BaseState = issue.State,
            BaseAssignees = null, // assignees come across on the first sync below
            GitLabUpdatedAtUtc = null, WorkbenchModifiedAtUtc = item.ModifiedAtUtc,
        };
        ctx.Db.Set<GitLabItemLink>().Add(link);
        ctx.ItemLinks[item.Id] = link;
        pending.Status = GitLabPendingStatus.Approved;
        pending.DecidedBy = decidedBy;
        pending.DecidedAtUtc = now;
        pending.ItemId = item.Id;
        ctx.Activity(item.Id, ActivityKinds.ItemCreated, $"Added \"{item.Title}\" from GitLab issue #{issue.Iid} (approved by {decidedBy})");
        await ctx.SaveAsync(ct);

        // Number its WBS code and roll the plan up, then send the code and dates back to the issue.
        await PlanReflow.ReflowAndSaveAsync(ctx.Db, ctx.Sheet.Id, ct, clock);
        ctx.Shape = new GitLabRules.PlanShape(ctx.Items.Values, ctx.Link.PhaseLevels);
        await SyncPairAsync(ctx, item, link, issue, ct);
        await ctx.SaveAsync(ct);
        return item.Id;
    }

    /// <summary>
    /// Applies the chosen side's value of a note to both sides (the "Use Workbench's" / "Use GitLab's" buttons). The sync
    /// already put one value back when it wrote the note, so the chosen value comes from the note itself.
    /// </summary>
    public async Task ResolveAsync(Guid linkId, Guid conflictId, bool useWorkbench, string resolvedBy, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct) ?? throw new ArgumentException("This project isn't linked to GitLab.");
        var conflict = await ctx.Db.Set<GitLabSyncConflict>().FirstOrDefaultAsync(c => c.Id == conflictId && c.LinkId == linkId, ct)
            ?? throw Contracts.WorkbenchNotFoundException.For("Sync note", conflictId);
        if (conflict.ItemId is not { } itemId || ctx.Items.GetValueOrDefault(itemId) is not { } item || ctx.ItemLinks.GetValueOrDefault(itemId) is not { } link)
        {
            throw new ArgumentException("The task this note is about isn't linked any more; dismiss the note instead.");
        }

        if (conflict.Field == "sync" && link.LastError?.Contains("deleted in GitLab", StringComparison.Ordinal) == true)
        {
            // Create the deleted issue, epic or milestone again.
            ctx.Db.Set<GitLabItemLink>().Remove(link);
            ctx.ItemLinks.Remove(itemId);
            await ctx.SaveAsync(ct);
            await SyncItemAsync(ctx, itemId, null, ct);
        }
        else
        {
            var value = useWorkbench ? conflict.WorkbenchValue : conflict.GitLabValue;
            switch (conflict.Field)
            {
                case "title" when !string.IsNullOrWhiteSpace(value):
                    item.Title = GitLabRules.Clip(value.Trim(), 500);
                    ctx.Touch(item);
                    break;
                case "description" when ctx.NotesColumn is { } notes:
                    item.Values = new Dictionary<string, string?>(item.Values) { [notes.Id.ToString()] = GitLabRules.NormalizeNotes(value) };
                    ctx.Touch(item);
                    break;
                case "due" when !useWorkbench:
                    if (!DateOnly.TryParseExact(value, "MMM d, yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
                    {
                        throw new ArgumentException("GitLab's date couldn't be read; change the date on the plan instead.");
                    }

                    ctx.SetDates(item, item.Start is { } s && s > due ? due : item.Start, due);
                    ctx.Touch(item);
                    ctx.Activity(item.Id, "item.gitlab", $"Due date set to {due:MMM d, yyyy} from GitLab {GitLabRules.Label(link.Kind, link.Iid)} (chosen by {resolvedBy})");
                    break;
            }

            await SyncPairAsync(ctx, item, link, null, ct, new Override(conflict.Field, true, useWorkbench ? conflict.WorkbenchValue : null));
        }

        conflict.Status = GitLabConflictStatus.Resolved;
        conflict.ResolvedBy = resolvedBy;
        conflict.ResolvedAtUtc = Now;
        conflict.UpdatedAtUtc = Now;
        conflict.Resolution = useWorkbench ? "Used Workbench's value" : "Used GitLab's value";
        await ctx.SaveAsync(ct);
    }

    // ===================================================================== per item

    private async Task<GitLabSyncReport> SyncItemAsync(SyncContext ctx, Guid itemId, IReadOnlyDictionary<string, GitLabIssue>? markers, CancellationToken ct)
    {
        var link = ctx.ItemLinks.GetValueOrDefault(itemId);
        if (!ctx.Items.TryGetValue(itemId, out var item))
        {
            return link is { IsRemoved: false } ? await RemoveAsync(ctx, link, ct) : GitLabSyncReport.Empty;
        }

        var role = ctx.Shape.RoleOf(itemId);
        if (link is null && role == GitLabRules.Role.None)
        {
            return GitLabSyncReport.Empty;
        }

        var report = GitLabSyncReport.Empty;

        // Parents first: a task's phase (and, for epics, a phase's parent phases) must exist so it can be put under them.
        if (ctx.Shape.PhaseOf(itemId) is { } phase && !ctx.ItemLinks.ContainsKey(phase.Id))
        {
            report = report.Add(await SyncItemAsync(ctx, phase.Id, markers, ct));
        }

        if (link is null)
        {
            return report.Add(role == GitLabRules.Role.Phase
                ? await CreatePhaseAsync(ctx, item, ct)
                : await CreateIssueAsync(ctx, item, markers, ct));
        }

        return report.Add(await SyncPairAsync(ctx, item, link, null, ct));
    }

    /// <summary>True when Workbench's values differ from what was last sent (so a push is due without asking GitLab).</summary>
    private bool NeedsPush(SyncContext ctx, Item item, GitLabItemLink link)
    {
        if (link.IsRemoved || link.LastError is not null)
        {
            return link.LastError is not null && !link.LastError.Contains("deleted in GitLab", StringComparison.Ordinal);
        }

        if (item.ModifiedAtUtc is { } modified && link.WorkbenchModifiedAtUtc is { } seen && modified > seen.AddSeconds(1))
        {
            return true;
        }

        var wanted = ctx.Desired(item, link.Kind);
        return wanted.Title != (link.Kind == GitLabObjectKind.Milestone ? GitLabRules.TitleFromMilestone(link.BaseTitle ?? "", wanted.Wbs) : link.BaseTitle)
            || wanted.Notes != link.BaseDescription
            || (link.Kind == GitLabObjectKind.Issue && GitLabRules.Ids(wanted.AssigneeIds) != (link.BaseAssignees ?? ""))
            || wanted.Due != link.BaseDue
            || (link.Kind != GitLabObjectKind.Issue && wanted.Start != link.BaseStart)
            || wanted.Wbs != link.BaseWbsCode
            || (link.Kind == GitLabObjectKind.Issue && wanted.Estimate is not null && wanted.Estimate != link.BaseEstimateHours)
            || wanted.ParentId != link.BaseParentId
            || (link.Kind == GitLabObjectKind.Issue && GitLabRules.Ids(wanted.BlockedBy) != (link.BaseBlocks ?? ""));
    }

    private async Task<GitLabSyncReport> GuardAsync(SyncContext ctx, Guid? itemId, Func<Task<GitLabSyncReport>> work)
    {
        try
        {
            return await work();
        }
        catch (GitLabApiException ex) when (ex.StatusCode is not 401)
        {
            logger.LogInformation("GitLab sync of task {ItemId} failed: {Message}", itemId, ex.Message);
            if (itemId is { } id && ctx.ItemLinks.GetValueOrDefault(id) is { } link)
            {
                link.LastError = GitLabRules.Clip(ex.Message, 1000);
            }

            if (!ex.IsRetryable)
            {
                ctx.Note(itemId, null, GitLabConflictKind.SyncFailed, "sync", null, null, ex.Message, ex.Hint);
            }

            ctx.Link.LastError = GitLabRules.Clip(ex.Message, 1000);
            ctx.Link.LastErrorHint = GitLabRules.Clip(ex.Hint, 1000);
            return new GitLabSyncReport(0, 0, 0, 0, 1, ex.Message);
        }
    }

    // ===================================================================== creating

    private async Task<GitLabSyncReport> CreateIssueAsync(SyncContext ctx, Item item, IReadOnlyDictionary<string, GitLabIssue>? markers, CancellationToken ct)
    {
        await ctx.MatchPeopleAsync(ctx.PeopleOf(item.Id), [], ct);
        var wanted = ctx.Desired(item, GitLabObjectKind.Issue);
        var marker = GitLabRules.Marker(item.Id);
        var existing = markers is not null
            ? markers.GetValueOrDefault(marker)
            : await ctx.Client.FindIssueByMarkerAsync(ctx.Link.GitLabProjectId, marker, ct);
        if (existing is not null && ctx.ItemLinks.Values.Any(l => l.Kind == GitLabObjectKind.Issue && l.GitLabId == existing.Id))
        {
            existing = null; // already linked to another task (a copied task kept its marker): make a new one
        }

        GitLabIssue issue;
        var created = existing is null;
        if (existing is null)
        {
            var fields = new Dictionary<string, object?>
            {
                ["title"] = wanted.Title,
                ["description"] = ctx.Description(item, wanted),
                ["assignee_ids"] = wanted.AssigneeIds,
                ["labels"] = wanted.Wbs is { } code ? GitLabRules.WbsLabel(code) : null,
            };
            if (wanted.Due is { } due)
            {
                fields["due_date"] = Date(due);
            }

            ParentField(ctx, fields, wanted.ParentId);
            issue = await ctx.Client.CreateIssueAsync(ctx.Link.GitLabProjectId, fields, ct);
        }
        else
        {
            issue = existing;
        }

        var link = new GitLabItemLink
        {
            LinkId = ctx.Link.Id, ItemId = item.Id, Kind = GitLabObjectKind.Issue, GitLabId = issue.Id, Iid = issue.Iid, WebUrl = issue.WebUrl,
        };
        if (created)
        {
            SetIssueBase(link, issue, wanted, ctx);
            link.BaseEstimateHours = null; // set just below
        }

        ctx.Db.Set<GitLabItemLink>().Add(link);
        ctx.ItemLinks[item.Id] = link;
        if (!await ctx.TrySaveLinkAsync(link, ct))
        {
            // Another sync linked this task first: close the duplicate this one made.
            if (created)
            {
                await ctx.Client.UpdateIssueAsync(ctx.Link.GitLabProjectId, issue.Iid, new Dictionary<string, object?> { ["state_event"] = "close" }, ct);
                await ctx.Client.AddIssueNoteAsync(ctx.Link.GitLabProjectId, issue.Iid, "Closed by Workbench: a duplicate of the task's other issue.", ct);
            }

            return GitLabSyncReport.Empty;
        }

        ctx.Activity(item.Id, "item.gitlab", created ? $"Created GitLab issue #{issue.Iid}" : $"Linked to GitLab issue #{issue.Iid} (found by its Workbench marker)");

        // A task already done when it's first sent starts out closed in GitLab (from then on GitLab owns open and closed).
        if (created && ctx.IsDone(item) && issue.State == "opened")
        {
            issue = await ctx.Client.UpdateIssueAsync(ctx.Link.GitLabProjectId, issue.Iid, new Dictionary<string, object?> { ["state_event"] = "close" }, ct);
            link.BaseState = issue.State;
            link.GitLabUpdatedAtUtc = issue.UpdatedAt;
            ctx.Activity(item.Id, "item.gitlab", $"Closed GitLab issue #{issue.Iid}: the task was already done");
        }

        // Estimate, dependencies and anything the create couldn't carry, through the normal pair sync.
        await SyncPairAsync(ctx, item, link, issue, ct);

        // Successors created before this task couldn't point at it yet: link them now.
        foreach (var successorId in ctx.SuccessorsOf(item.Id))
        {
            if (ctx.ItemLinks.GetValueOrDefault(successorId) is { Kind: GitLabObjectKind.Issue, IsRemoved: false } successorLink && ctx.Items.GetValueOrDefault(successorId) is { } successor)
            {
                var sent = new List<string>();
                await SyncBlocksAsync(ctx, successor, successorLink, ctx.Desired(successor, GitLabObjectKind.Issue), GitLabRules.Label(GitLabObjectKind.Issue, successorLink.Iid), sent, ct);
                if (sent.Count > 0)
                {
                    ctx.Activity(successorId, "item.gitlab", $"Sent to GitLab {GitLabRules.Label(GitLabObjectKind.Issue, successorLink.Iid)}: dependencies");
                }
            }
        }

        return new GitLabSyncReport(1, created ? 1 : 0, 0, 0, 0);
    }

    private async Task<GitLabSyncReport> CreatePhaseAsync(SyncContext ctx, Item item, CancellationToken ct)
    {
        var kind = ctx.Link.PhaseMode == GitLabPhaseMode.Epics ? GitLabObjectKind.Epic : GitLabObjectKind.Milestone;
        var wanted = ctx.Desired(item, kind);
        GitLabItemLink link;
        if (kind == GitLabObjectKind.Epic)
        {
            var groupId = ctx.Link.GitLabGroupId ?? throw new GitLabApiException(400, "Epics need a GitLab group.", "Link the project again and choose milestones.");
            var fields = new Dictionary<string, object?>
            {
                ["title"] = wanted.Title,
                ["description"] = ctx.Description(item, wanted),
                ["labels"] = wanted.Wbs is { } code ? GitLabRules.WbsLabel(code) : null,
                ["start_date_is_fixed"] = wanted.Start is not null,
                ["start_date_fixed"] = wanted.Start is { } s ? Date(s) : null,
                ["due_date_is_fixed"] = wanted.Due is not null,
                ["due_date_fixed"] = wanted.Due is { } d ? Date(d) : null,
                ["parent_id"] = wanted.ParentId,
            };
            var epic = await ctx.Client.CreateEpicAsync(groupId, fields, ct);
            link = new GitLabItemLink { LinkId = ctx.Link.Id, ItemId = item.Id, Kind = kind, GitLabId = epic.Id, Iid = epic.Iid, WebUrl = epic.WebUrl };
            SetEpicBase(link, epic);
        }
        else
        {
            var title = GitLabRules.MilestoneTitle(wanted.Title, wanted.Wbs);
            var fields = new Dictionary<string, object?>
            {
                ["title"] = title,
                ["description"] = ctx.Description(item, wanted),
                ["start_date"] = wanted.Start is { } s ? Date(s) : null,
                ["due_date"] = wanted.Due is { } d ? Date(d) : null,
            };
            GitLabMilestone milestone;
            try
            {
                milestone = await ctx.Client.CreateMilestoneAsync(ctx.Link.GitLabProjectId, fields, ct);
            }
            catch (GitLabApiException ex) when (ex.StatusCode is 400 or 409)
            {
                // Titles are unique per project: a milestone left from an earlier link (or a retried create) is reused.
                milestone = (await ctx.Client.ListMilestonesAsync(ctx.Link.GitLabProjectId, ct)).FirstOrDefault(m => m.Title == title) ?? throw new GitLabApiException(ex.StatusCode, ex.Message, ex.Hint);
            }

            link = new GitLabItemLink { LinkId = ctx.Link.Id, ItemId = item.Id, Kind = kind, GitLabId = milestone.Id, Iid = milestone.Iid, WebUrl = milestone.WebUrl };
            SetMilestoneBase(link, milestone, wanted.Wbs);
        }

        link.WorkbenchModifiedAtUtc = item.ModifiedAtUtc;
        link.LastSyncedAtUtc = Now;
        ctx.Db.Set<GitLabItemLink>().Add(link);
        ctx.ItemLinks[item.Id] = link;
        if (!await ctx.TrySaveLinkAsync(link, ct))
        {
            return GitLabSyncReport.Empty;
        }

        ctx.Activity(item.Id, "item.gitlab", $"Created GitLab {GitLabRules.Label(kind, link.Iid)}");
        return new GitLabSyncReport(1, 1, 0, 0, 0);
    }

    private static void ParentField(SyncContext ctx, Dictionary<string, object?> fields, long? parentId)
    {
        if (ctx.Link.PhaseMode == GitLabPhaseMode.Epics)
        {
            fields["epic_id"] = parentId;
        }
        else
        {
            fields["milestone_id"] = parentId;
        }
    }

    // ===================================================================== removing

    private async Task<GitLabSyncReport> RemoveAsync(SyncContext ctx, GitLabItemLink link, CancellationToken ct)
    {
        const string Note = "Closed by Workbench: this task was deleted from the project's plan.";
        switch (link.Kind)
        {
            case GitLabObjectKind.Issue:
                await ctx.Client.UpdateIssueAsync(ctx.Link.GitLabProjectId, link.Iid, new Dictionary<string, object?> { ["state_event"] = "close" }, ct);
                await ctx.Client.AddIssueNoteAsync(ctx.Link.GitLabProjectId, link.Iid, Note, ct);
                break;
            case GitLabObjectKind.Epic when ctx.Link.GitLabGroupId is { } groupId:
                await ctx.Client.UpdateEpicAsync(groupId, link.Iid, new Dictionary<string, object?> { ["state_event"] = "close" }, ct);
                break;
            case GitLabObjectKind.Milestone:
                await ctx.Client.UpdateMilestoneAsync(ctx.Link.GitLabProjectId, link.GitLabId, new Dictionary<string, object?> { ["state_event"] = "close" }, ct);
                break;
        }

        link.IsRemoved = true;
        link.LastSyncedAtUtc = Now;
        link.Version = Guid.NewGuid();
        ctx.Activity(link.ItemId, "item.gitlab", $"Closed GitLab {GitLabRules.Label(link.Kind, link.Iid)}: the task was deleted");
        return new GitLabSyncReport(1, 0, 0, 0, 0);
    }

    // ===================================================================== pulling an issue

    private async Task<GitLabSyncReport> PullIssueAsync(SyncContext ctx, GitLabIssue issue, CancellationToken ct)
    {
        var link = ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == GitLabObjectKind.Issue && l.GitLabId == issue.Id);
        if (link is not null)
        {
            if (link.IsRemoved || ctx.Items.GetValueOrDefault(link.ItemId) is not { } item)
            {
                return GitLabSyncReport.Empty;
            }

            return await SyncPairAsync(ctx, item, link, issue, ct);
        }

        // Not linked: an issue added under a linked epic or milestone waits for approval.
        var parentId = ctx.Link.PhaseMode == GitLabPhaseMode.Epics ? issue.Epic?.Id : issue.Milestone?.Id;
        var phaseKind = ctx.Link.PhaseMode == GitLabPhaseMode.Epics ? GitLabObjectKind.Epic : GitLabObjectKind.Milestone;
        var phase = parentId is { } pid ? ctx.ItemLinks.Values.FirstOrDefault(l => l.Kind == phaseKind && l.GitLabId == pid && !l.IsRemoved) : null;
        if (phase is null || issue.State != "opened")
        {
            return GitLabSyncReport.Empty;
        }

        var known = await ctx.Db.Set<GitLabPendingIssue>().AnyAsync(p => p.LinkId == ctx.Link.Id && p.IssueId == issue.Id, ct);
        if (known)
        {
            return GitLabSyncReport.Empty;
        }

        ctx.Db.Set<GitLabPendingIssue>().Add(new GitLabPendingIssue
        {
            LinkId = ctx.Link.Id, IssueId = issue.Id, Iid = issue.Iid, Title = GitLabRules.Clip(issue.Title, 500), WebUrl = issue.WebUrl,
            PhaseItemId = phase.ItemId, Author = issue.Author?.Username, SeenAtUtc = Now,
        });
        return new GitLabSyncReport(0, 0, 1, 0, 0);
    }

    // ===================================================================== the pair sync

    /// <summary>A person's choice on a note: Workbench's value of this field goes to GitLab once, whatever the ownership rule says.</summary>
    private sealed record Override(string Field, bool UseWorkbench, string? Value = null);

    private async Task<GitLabSyncReport> SyncPairAsync(SyncContext ctx, Item item, GitLabItemLink link, object? fetched, CancellationToken ct, Override? force = null)
    {
        if (link.IsRemoved)
        {
            return GitLabSyncReport.Empty;
        }

        try
        {
            var report = link.Kind switch
            {
                GitLabObjectKind.Issue => await SyncIssuePairAsync(ctx, item, link, fetched as GitLabIssue, force, ct),
                GitLabObjectKind.Epic => await SyncEpicPairAsync(ctx, item, link, fetched as GitLabEpic, force, ct),
                _ => await SyncMilestonePairAsync(ctx, item, link, fetched as GitLabMilestone, force, ct),
            };
            link.LastError = null;
            link.LastSyncedAtUtc = Now;
            link.Version = Guid.NewGuid();
            return report;
        }
        catch (GitLabApiException ex) when (ex.StatusCode == 404)
        {
            link.LastError = $"The {GitLabRules.Label(link.Kind, link.Iid)} was deleted in GitLab.";
            link.Version = Guid.NewGuid();
            ctx.Note(item.Id, link, GitLabConflictKind.SyncFailed, "sync", item.Title, null, link.LastError,
                "Choose \"Use Workbench's\" to create it again, or dismiss this and unlink the task by deleting it from the plan.");
            return new GitLabSyncReport(0, 0, 0, 1, 0);
        }
    }

    private async Task<GitLabSyncReport> SyncIssuePairAsync(SyncContext ctx, Item item, GitLabItemLink link, GitLabIssue? issue, Override? force, CancellationToken ct)
    {
        issue ??= await ctx.Client.GetIssueAsync(ctx.Link.GitLabProjectId, link.Iid, ct);
        var label = GitLabRules.Label(GitLabObjectKind.Issue, issue.Iid);
        await ctx.MatchPeopleAsync(ctx.PeopleOf(item.Id), issue.Assignees ?? [], ct);
        var wanted = ctx.Desired(item, GitLabObjectKind.Issue);
        var glAssignees = (issue.Assignees ?? []).Select(a => a.Id).ToList();
        var push = new Dictionary<string, object?>();
        var changes = new List<string>();
        var sent = new List<string>();
        var conflicts = 0;
        var wbModified = item.ModifiedAtUtc ?? item.CreatedAtUtc ?? DateTime.MinValue;
        bool GitLabIsLater() => issue.UpdatedAt > wbModified;

        // ---- Shared: title
        var title = Shared("title", wanted.Title, issue.Title.Trim(), link.BaseTitle, force, GitLabIsLater, ctx, item, link, ref conflicts);
        if (title.ToWorkbench is { } newTitle)
        {
            item.Title = GitLabRules.Clip(newTitle, 500);
            changes.Add($"title changed to \"{item.Title}\"");
        }

        if (title.ToGitLab is { } sendTitle)
        {
            push["title"] = sendTitle;
            sent.Add("title");
        }

        link.BaseTitle = title.Agreed;

        // ---- Shared: description (the notes; Workbench's block is owned by Workbench and rewritten below)
        var glNotes = GitLabRules.Strip(issue.Description);
        var notes = Shared("description", ctx.NotesColumn is null ? glNotes : wanted.Notes, glNotes, link.BaseDescription, force, GitLabIsLater, ctx, item, link, ref conflicts);
        if (notes.ToWorkbench is not null && ctx.NotesColumn is { } column)
        {
            item.Values = new Dictionary<string, string?>(item.Values) { [column.Id.ToString()] = notes.Agreed };
            changes.Add("description changed");
        }

        link.BaseDescription = notes.Agreed;
        var description = ctx.Description(item, wanted with { Notes = notes.Agreed });
        if (!string.Equals(Normalize(issue.Description), Normalize(description), StringComparison.Ordinal))
        {
            push["description"] = description;
            if (notes.ToGitLab is not null)
            {
                sent.Add("description");
            }
        }

        // ---- Shared: assignees (people staffed on the task in Assign people are Workbench's: they can't be removed from GitLab)
        var assignees = await SyncAssigneesAsync(ctx, item, link, wanted, glAssignees, force, GitLabIsLater, label, changes, ct);
        conflicts += assignees.Conflicts;
        if (assignees.ToGitLab is { } ids)
        {
            push["assignee_ids"] = ids;
            sent.Add("assignees");
        }

        // ---- Workbench owns: due date, WBS label, phase
        if (Owned("due", wanted.Due, issue.DueDate, link.BaseDue, force, ctx, item, link, label, d => d?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture), ref conflicts) is { } dueChange)
        {
            if (dueChange.ToWorkbench)
            {
                ctx.SetDates(item, item.Start is { } s && issue.DueDate is { } f && s > f ? f : item.Start, issue.DueDate);
                changes.Add($"due date set from GitLab to {issue.DueDate:MMM d, yyyy}");
            }
            else if (wanted.Due is { } due)
            {
                push["due_date"] = Date(due);
                sent.Add("due date");
            }
        }

        link.BaseDue = item.Finish ?? issue.DueDate;

        var glCode = GitLabRules.WbsCodeOf(issue.Labels);
        if (Owned("wbs", wanted.Wbs, glCode, link.BaseWbsCode, force, ctx, item, link, label, c => c, ref conflicts) is not null)
        {
            var remove = (issue.Labels ?? []).Where(GitLabRules.IsWbsLabel).Where(l => wanted.Wbs is null || l != GitLabRules.WbsLabel(wanted.Wbs)).ToList();
            if (remove.Count > 0)
            {
                push["remove_labels"] = string.Join(",", remove);
            }

            if (wanted.Wbs is { } code)
            {
                push["add_labels"] = GitLabRules.WbsLabel(code);
            }

            sent.Add("WBS code");
        }

        link.BaseWbsCode = wanted.Wbs;

        var glParent = ctx.Link.PhaseMode == GitLabPhaseMode.Epics ? issue.Epic?.Id : issue.Milestone?.Id;
        if (Owned("parent", wanted.ParentId, glParent, link.BaseParentId, force, ctx, item, link, label, id => ctx.PhaseName(id), ref conflicts) is not null)
        {
            ParentField(ctx, push, wanted.ParentId);
            sent.Add("phase");
        }

        link.BaseParentId = wanted.ParentId;

        // ---- GitLab owns: open/closed and % complete
        var closed = issue.State == "closed";
        var wasClosed = link.BaseState == "closed";
        var workbenchDone = ctx.IsDone(item);
        var stateFromWorkbench = false;
        if (force is { Field: "state", UseWorkbench: true })
        {
            var wantClosed = force.Value is null ? workbenchDone : force.Value == "Done";
            if (wantClosed != closed)
            {
                push["state_event"] = wantClosed ? "close" : "reopen";
                sent.Add(wantClosed ? "closed" : "reopened");
                closed = wantClosed;
                stateFromWorkbench = true;
            }
        }
        else if (link.BaseState is not null && workbenchDone != closed && workbenchDone != wasClosed && closed == wasClosed && ctx.StatusColumn is not null)
        {
            // Someone marked it done (or not done) in Workbench: GitLab owns that, so it goes back.
            conflicts++;
            ctx.Note(item.Id, link, GitLabConflictKind.GitLabOwns, "state", workbenchDone ? "Done" : "Not done", closed ? "Closed" : "Open",
                $"Marked {(workbenchDone ? "done" : "not done")} in Workbench, but {label} is {(closed ? "closed" : "open")} in GitLab, which owns open and closed. Workbench put it back.",
                $"Close or reopen {label} in GitLab, or choose \"Use Workbench's\" to {(workbenchDone ? "close" : "reopen")} it from here.");
        }

        var children = closed ? null : await ctx.Client.GetChildTasksAsync(issue.Id, ct);
        var progress = GitLabRules.Progress(closed, children, issue.TaskCompletionStatus);
        var current = ctx.PercentOf(item);
        if (closed && !wasClosed && link.BaseState is not null)
        {
            link.PercentBeforeClose = current is < 100 ? current : link.PercentBeforeClose;
            changes.Add(stateFromWorkbench ? "closed from Workbench, so 100% complete" : $"{label} closed in GitLab, so 100% complete");
        }
        else if (!closed && wasClosed)
        {
            progress ??= link.PercentBeforeClose ?? 0;
            changes.Add(stateFromWorkbench ? $"reopened from Workbench, so {GitLabRules.Percent(progress.Value)}% complete"
                : $"{label} reopened in GitLab, so {GitLabRules.Percent(progress.Value)}% complete");
        }

        if (progress is { } p)
        {
            if (current != p)
            {
                if (link.BasePercent is { } basePercent && current != basePercent && current is not null && force is not { Field: "percent" })
                {
                    conflicts++;
                    ctx.Note(item.Id, link, GitLabConflictKind.GitLabOwns, "percent", GitLabRules.Percent(current.Value), GitLabRules.Percent(p),
                        $"% complete was changed in Workbench to {GitLabRules.Percent(current.Value)}, but GitLab owns progress, so it's {GitLabRules.Percent(p)} again (from {label}).",
                        "Update progress in GitLab: close the issue, or tick its tasks or checklist.");
                }

                if (ctx.SetPercent(item, p) && !changes.Any(c => c.Contains("% complete", StringComparison.Ordinal)))
                {
                    changes.Add($"% complete set to {GitLabRules.Percent(p)} from GitLab");
                }
            }

            link.BasePercent = p;
        }
        else
        {
            link.BasePercent = current;
        }

        ctx.SetDone(item, closed);
        link.BaseState = closed ? "closed" : "opened";

        // ---- Send
        GitLabIssue after = issue;
        if (push.Count > 0)
        {
            if (!ctx.Link.MultipleAssignees && push.TryGetValue("assignee_ids", out var a) && a is List<long> { Count: > 1 } many)
            {
                push["assignee_ids"] = many.Take(1).ToList();
            }

            after = await ctx.Client.UpdateIssueAsync(ctx.Link.GitLabProjectId, issue.Iid, push, ct);
        }

        if (wanted.Estimate is { } hours && hours != link.BaseEstimateHours)
        {
            var glHours = issue.TimeStats is { } stats ? Math.Round(stats.TimeEstimate / 3600m, 2) : (decimal?)null;
            if (glHours != hours)
            {
                if (link.BaseEstimateHours is { } baseHours && glHours != baseHours)
                {
                    conflicts++;
                    ctx.Note(item.Id, link, GitLabConflictKind.WorkbenchOwns, "estimate", Hours(hours), Hours(glHours),
                        $"The estimate of {label} was changed in GitLab to {Hours(glHours)}. Workbench owns hours (from Assign people), so it's {Hours(hours)} again.",
                        "Change the hours in Budget & staffing › Assign people.");
                }

                await ctx.Client.SetTimeEstimateAsync(ctx.Link.GitLabProjectId, issue.Iid, hours, ct);
                sent.Add("hours");
            }

            link.BaseEstimateHours = hours;
        }

        conflicts += await SyncBlocksAsync(ctx, item, link, wanted, label, sent, ct);

        // ---- Record
        link.BaseAssignees = GitLabRules.Ids(assignees.Agreed);
        link.GitLabUpdatedAtUtc = after.UpdatedAt;
        if (changes.Count > 0)
        {
            ctx.Touch(item);
            ctx.Activity(item.Id, "item.gitlab", $"From GitLab {label}: {string.Join("; ", changes)}");
        }

        if (sent.Count > 0)
        {
            ctx.Activity(item.Id, "item.gitlab", $"Sent to GitLab {label}: {string.Join(", ", sent.Distinct())}");
        }

        link.WorkbenchModifiedAtUtc = item.ModifiedAtUtc;
        return new GitLabSyncReport(changes.Count + sent.Count > 0 ? 1 : 0, 0, 0, conflicts, 0);
    }

    private async Task<GitLabSyncReport> SyncEpicPairAsync(SyncContext ctx, Item item, GitLabItemLink link, GitLabEpic? epic, Override? force, CancellationToken ct)
    {
        var groupId = ctx.Link.GitLabGroupId ?? throw new GitLabApiException(400, "Epics need a GitLab group.", "Unlink the project and link it again with milestones.");
        epic ??= await ctx.Client.GetEpicAsync(groupId, link.Iid, ct);
        var label = GitLabRules.Label(GitLabObjectKind.Epic, epic.Iid);
        var wanted = ctx.Desired(item, GitLabObjectKind.Epic);
        var push = new Dictionary<string, object?>();
        var changes = new List<string>();
        var sent = new List<string>();
        var conflicts = 0;
        var wbModified = item.ModifiedAtUtc ?? item.CreatedAtUtc ?? DateTime.MinValue;
        bool GitLabIsLater() => epic.UpdatedAt > wbModified;

        var title = Shared("title", wanted.Title, epic.Title.Trim(), link.BaseTitle, force, GitLabIsLater, ctx, item, link, ref conflicts);
        if (title.ToWorkbench is { } newTitle)
        {
            item.Title = GitLabRules.Clip(newTitle, 500);
            changes.Add($"title changed to \"{item.Title}\"");
        }

        if (title.ToGitLab is { } sendTitle)
        {
            push["title"] = sendTitle;
            sent.Add("title");
        }

        link.BaseTitle = title.Agreed;
        var notes = SyncPhaseNotes(ctx, item, link, wanted, epic.Description, force, GitLabIsLater, push, changes, sent, ref conflicts);

        if (Owned("start", wanted.Start, epic.StartDateFixed, link.BaseStart, force, ctx, item, link, label, DateLabel, ref conflicts) is not null)
        {
            push["start_date_is_fixed"] = wanted.Start is not null;
            push["start_date_fixed"] = wanted.Start is { } s ? Date(s) : null;
            sent.Add("start date");
        }

        if (Owned("due", wanted.Due, epic.DueDateFixed, link.BaseDue, force, ctx, item, link, label, DateLabel, ref conflicts) is not null)
        {
            push["due_date_is_fixed"] = wanted.Due is not null;
            push["due_date_fixed"] = wanted.Due is { } d ? Date(d) : null;
            sent.Add("due date");
        }

        if (Owned("wbs", wanted.Wbs, GitLabRules.WbsCodeOf(epic.Labels), link.BaseWbsCode, force, ctx, item, link, label, c => c, ref conflicts) is not null)
        {
            var remove = (epic.Labels ?? []).Where(GitLabRules.IsWbsLabel).Where(l => wanted.Wbs is null || l != GitLabRules.WbsLabel(wanted.Wbs)).ToList();
            if (remove.Count > 0)
            {
                push["remove_labels"] = string.Join(",", remove);
            }

            if (wanted.Wbs is { } code)
            {
                push["add_labels"] = GitLabRules.WbsLabel(code);
            }

            sent.Add("WBS code");
        }

        if (Owned("parent", wanted.ParentId, epic.ParentId, link.BaseParentId, force, ctx, item, link, label, id => ctx.PhaseName(id), ref conflicts) is not null)
        {
            push["parent_id"] = wanted.ParentId;
            sent.Add("parent epic");
        }

        var after = epic;
        if (push.Count > 0)
        {
            after = await ctx.Client.UpdateEpicAsync(groupId, epic.Iid, push, ct);
        }

        link.BaseDescription = notes;
        link.BaseStart = wanted.Start;
        link.BaseDue = wanted.Due;
        link.BaseWbsCode = wanted.Wbs;
        link.BaseParentId = wanted.ParentId;
        link.BaseState = after.State;
        link.GitLabUpdatedAtUtc = after.UpdatedAt;
        Finish(ctx, item, link, label, changes, sent);
        return new GitLabSyncReport(changes.Count + sent.Count > 0 ? 1 : 0, 0, 0, conflicts, 0);
    }

    private async Task<GitLabSyncReport> SyncMilestonePairAsync(SyncContext ctx, Item item, GitLabItemLink link, GitLabMilestone? milestone, Override? force, CancellationToken ct)
    {
        milestone ??= await ctx.Client.GetMilestoneAsync(ctx.Link.GitLabProjectId, link.GitLabId, ct);
        var label = GitLabRules.Label(GitLabObjectKind.Milestone, milestone.Iid);
        var wanted = ctx.Desired(item, GitLabObjectKind.Milestone);
        var push = new Dictionary<string, object?>();
        var changes = new List<string>();
        var sent = new List<string>();
        var conflicts = 0;
        var wbModified = item.ModifiedAtUtc ?? item.CreatedAtUtc ?? DateTime.MinValue;
        bool GitLabIsLater() => milestone.UpdatedAt > wbModified;

        // The WBS code leads a milestone's title (titles are unique per project); it's Workbench's, the rest is shared.
        var glTitle = GitLabRules.TitleFromMilestone(milestone.Title, link.BaseWbsCode ?? wanted.Wbs);
        var baseTitle = link.BaseTitle is null ? null : GitLabRules.TitleFromMilestone(link.BaseTitle, link.BaseWbsCode ?? wanted.Wbs);
        var title = Shared("title", wanted.Title, glTitle, baseTitle, force, GitLabIsLater, ctx, item, link, ref conflicts);
        if (title.ToWorkbench is { } newTitle)
        {
            item.Title = GitLabRules.Clip(newTitle, 500);
            changes.Add($"title changed to \"{item.Title}\"");
        }

        var fullTitle = GitLabRules.MilestoneTitle(title.Agreed ?? wanted.Title, wanted.Wbs);
        if (fullTitle != milestone.Title)
        {
            push["title"] = fullTitle;
            sent.Add(title.ToGitLab is not null ? "title" : "WBS code");
        }

        link.BaseTitle = fullTitle;
        var notes = SyncPhaseNotes(ctx, item, link, wanted, milestone.Description, force, GitLabIsLater, push, changes, sent, ref conflicts);

        if (Owned("start", wanted.Start, milestone.StartDate, link.BaseStart, force, ctx, item, link, label, DateLabel, ref conflicts) is not null)
        {
            push["start_date"] = wanted.Start is { } s ? Date(s) : null;
            sent.Add("start date");
        }

        if (Owned("due", wanted.Due, milestone.DueDate, link.BaseDue, force, ctx, item, link, label, DateLabel, ref conflicts) is not null)
        {
            push["due_date"] = wanted.Due is { } d ? Date(d) : null;
            sent.Add("due date");
        }

        var after = milestone;
        if (push.Count > 0)
        {
            after = await ctx.Client.UpdateMilestoneAsync(ctx.Link.GitLabProjectId, milestone.Id, push, ct);
        }

        link.BaseDescription = notes;
        link.BaseStart = wanted.Start;
        link.BaseDue = wanted.Due;
        link.BaseWbsCode = wanted.Wbs;
        link.BaseState = after.State;
        link.GitLabUpdatedAtUtc = after.UpdatedAt;
        Finish(ctx, item, link, label, changes, sent);
        return new GitLabSyncReport(changes.Count + sent.Count > 0 ? 1 : 0, 0, 0, conflicts, 0);
    }

    private static string? SyncPhaseNotes(
        SyncContext ctx, Item item, GitLabItemLink link, Desired wanted, string? glDescription, Override? force, Func<bool> gitLabIsLater,
        Dictionary<string, object?> push, List<string> changes, List<string> sent, ref int conflicts)
    {
        var glNotes = GitLabRules.Strip(glDescription);
        var notes = Shared("description", ctx.NotesColumn is null ? glNotes : wanted.Notes, glNotes, link.BaseDescription, force, gitLabIsLater, ctx, item, link, ref conflicts);
        if (notes.ToWorkbench is not null && ctx.NotesColumn is { } column)
        {
            item.Values = new Dictionary<string, string?>(item.Values) { [column.Id.ToString()] = notes.Agreed };
            changes.Add("description changed");
        }

        var description = ctx.Description(item, wanted with { Notes = notes.Agreed });
        if (!string.Equals(Normalize(glDescription), Normalize(description), StringComparison.Ordinal))
        {
            push["description"] = description;
            if (notes.ToGitLab is not null)
            {
                sent.Add("description");
            }
        }

        return notes.Agreed;
    }

    private static void Finish(SyncContext ctx, Item item, GitLabItemLink link, string label, List<string> changes, List<string> sent)
    {
        if (changes.Count > 0)
        {
            ctx.Touch(item);
            ctx.Activity(item.Id, "item.gitlab", $"From GitLab {label}: {string.Join("; ", changes)}");
        }

        if (sent.Count > 0)
        {
            ctx.Activity(item.Id, "item.gitlab", $"Sent to GitLab {label}: {string.Join(", ", sent.Distinct())}");
        }

        link.WorkbenchModifiedAtUtc = item.ModifiedAtUtc;
    }

    // ---- assignees

    private sealed record AssigneeOutcome(IReadOnlyList<long> Agreed, List<long>? ToGitLab, int Conflicts);

    /// <summary>
    /// Assignees are shared, by GitLab user: the side that changed wins (the later edit when both did). People staffed on the
    /// task in Assign people are Workbench's and can't be unassigned from GitLab. GitLab assignees with no Workbench match are
    /// left on the issue and noted.
    /// </summary>
    private async Task<AssigneeOutcome> SyncAssigneesAsync(
        SyncContext ctx, Item item, GitLabItemLink link, Desired wanted, List<long> glIds, Override? force, Func<bool> gitLabIsLater, string label,
        List<string> changes, CancellationToken ct)
    {
        var conflicts = 0;
        var single = !ctx.Link.MultipleAssignees;
        var wb = single ? wanted.AssigneeIds.Take(1).ToList() : wanted.AssigneeIds.ToList();
        var glMapped = glIds.Where(id => ctx.EmailOf(id) is not null).ToList();
        var glUnmapped = glIds.Where(id => ctx.EmailOf(id) is null).ToList();
        foreach (var id in glUnmapped)
        {
            var user = await ctx.GitLabUserAsync(id, ct);
            var who = $"@{user?.Username ?? id.ToString(CultureInfo.InvariantCulture)}";
            ctx.Note(item.Id, link, GitLabConflictKind.PersonNotMatched, "person", null, who,
                $"{who} is assigned to {label} in GitLab but isn't matched to anyone in Workbench, so the task doesn't show them.",
                "Match them under \"People to match\" on this panel.");
        }

        var wbSet = wb.ToHashSet();
        var glSet = glMapped.ToHashSet();
        var firstFromGitLab = link.BaseAssignees is null;
        var baseIds = GitLabRules.ParseIds(link.BaseAssignees);
        var wbChanged = !firstFromGitLab && !wbSet.SetEquals(baseIds);
        var glChanged = firstFromGitLab ? glSet.Count > 0 : !glSet.SetEquals(baseIds);
        bool? takeGitLab = force?.Field == "assignees" ? !force.UseWorkbench
            : wbSet.SetEquals(glSet) ? null
            : glChanged && !wbChanged ? true
            : wbChanged && !glChanged ? false
            : glChanged && wbChanged ? gitLabIsLater()
            : false; // neither changed but they differ (a match was just made): Workbench's people are the plan

        if (glChanged && wbChanged && !wbSet.SetEquals(glSet) && force is null)
        {
            conflicts++;
            ctx.Note(item.Id, link, GitLabConflictKind.BothChanged, "assignees", await ctx.PeopleTextAsync(wb, ct), await ctx.PeopleTextAsync(glMapped, ct),
                $"Assignees changed in both Workbench and GitLab since the last sync. {(takeGitLab == true ? "GitLab's" : "Workbench's")} edit was later, so it was kept.",
                "If that's not right, change the assignees in either place; the next sync carries it over.");
        }

        if (takeGitLab == true)
        {
            var emails = glMapped.Select(id => ctx.EmailOf(id)!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var staffed = ctx.StaffedEmails(item.Id);
            var assigned = ctx.Assignees(item.Id);
            foreach (var email in emails.Where(e => !staffed.Contains(e) && !assigned.Contains(e)))
            {
                ctx.AddAssignee(item, email);
                changes.Add($"{ctx.NameOf(email)} assigned in GitLab");
            }

            foreach (var email in assigned.Where(e => !emails.Contains(e) && ctx.IdOf(e) is not null).ToList())
            {
                ctx.RemoveAssignee(item, email);
                changes.Add($"{ctx.NameOf(email)} unassigned in GitLab");
            }

            foreach (var email in staffed.Where(e => !emails.Contains(e) && ctx.IdOf(e) is not null))
            {
                conflicts++;
                ctx.Note(item.Id, link, GitLabConflictKind.WorkbenchOwns, "assignees", ctx.NameOf(email), null,
                    $"{ctx.NameOf(email)} was unassigned from {label} in GitLab, but they're staffed on this task in Assign people, so Workbench assigned them again.",
                    "Remove them from the task in Budget & staffing › Assign people.");
            }

            var desired = ctx.Desired(item, GitLabObjectKind.Issue).AssigneeIds;
            wb = single ? desired.Take(1).ToList() : desired.ToList();
        }

        // Send Workbench's people plus the GitLab assignees nobody matched (never drop someone Workbench can't see).
        var send = wb.Concat(glUnmapped.Where(id => !wb.Contains(id))).ToList();
        if (single && send.Count > 1)
        {
            send = send.Take(1).ToList();
        }

        return new AssigneeOutcome(wb, send.ToHashSet().SetEquals(glIds) ? null : send, conflicts);
    }

    // ---- dependencies

    private static async Task<int> SyncBlocksAsync(SyncContext ctx, Item item, GitLabItemLink link, Desired wanted, string label, List<string> sent, CancellationToken ct)
    {
        var desired = wanted.BlockedBy.ToHashSet();
        var baseSet = GitLabRules.ParseIds(link.BaseBlocks);
        if (desired.SetEquals(baseSet))
        {
            return 0;
        }

        var linkType = ctx.Link.BlockingLinks ? "blocks" : "relates_to";
        var existing = await ctx.Client.ListIssueLinksAsync(ctx.Link.GitLabProjectId, link.Iid, ct);
        var current = existing.Where(e => e.ProjectId == ctx.Link.GitLabProjectId).ToList();
        foreach (var predIid in desired.Where(p => !current.Any(c => c.Iid == p)))
        {
            // The predecessor blocks this task.
            await ctx.Client.CreateIssueLinkAsync(ctx.Link.GitLabProjectId, predIid, ctx.Link.GitLabProjectId, link.Iid, linkType, ct);
        }

        foreach (var stale in current.Where(c => baseSet.Contains(c.Iid) && !desired.Contains(c.Iid)))
        {
            await ctx.Client.DeleteIssueLinkAsync(ctx.Link.GitLabProjectId, link.Iid, stale.IssueLinkId, ct);
        }

        link.BaseBlocks = GitLabRules.Ids(desired);
        sent.Add("dependencies");
        return 0;
    }

    // ---- field rules

    private readonly record struct SharedOutcome<T>(T? Agreed, T? ToWorkbench, T? ToGitLab);

    /// <summary>Last write wins for a shared field; a change on both sides is noted.</summary>
    private static SharedOutcome<string> Shared(
        string field, string? wb, string? gl, string? baseValue, Override? force, Func<bool> gitLabIsLater, SyncContext ctx, Item item, GitLabItemLink link, ref int conflicts)
    {
        if (force?.Field == field)
        {
            return force.UseWorkbench ? new(wb, null, wb == gl ? null : wb ?? "") : new(gl, wb == gl ? null : gl ?? "", null);
        }

        var wbChanged = wb != baseValue;
        var glChanged = gl != baseValue;
        if (wb == gl)
        {
            return new(wb, null, null);
        }

        if (glChanged && !wbChanged)
        {
            return new(gl, gl ?? "", null);
        }

        if (wbChanged && !glChanged)
        {
            return new(wb, null, wb ?? "");
        }

        // Both changed to different values: the later edit wins, the other is noted.
        var gitLabWins = gitLabIsLater();
        conflicts++;
        ctx.Note(item.Id, link, GitLabConflictKind.BothChanged, field, wb, gl,
            $"{GitLabRules.FieldLabel(field)} changed in both Workbench and GitLab since the last sync. {(gitLabWins ? "GitLab's" : "Workbench's")} edit was later, so it was kept.",
            null);
        return gitLabWins ? new(gl, gl ?? "", null) : new(wb, null, wb ?? "");
    }

    private sealed record OwnedChange(bool ToWorkbench);

    /// <summary>
    /// A field Workbench owns: when GitLab's value differs, Workbench's is sent (and a GitLab edit noted). Returns null when
    /// nothing needs doing. "Use GitLab's" on a note applies GitLab's value to Workbench instead.
    /// </summary>
    private static OwnedChange? Owned<T>(
        string field, T? wb, T? gl, T? baseValue, Override? force, SyncContext ctx, Item item, GitLabItemLink link, string label, Func<T?, string?> text,
        ref int conflicts)
    {
        if (EqualityComparer<T?>.Default.Equals(wb, gl))
        {
            return null;
        }

        if (wb is null && force?.Field != field)
        {
            return null; // nothing planned in Workbench yet: leave GitLab's value
        }

        var editedInGitLab = link.LastSyncedAtUtc is not null && !EqualityComparer<T?>.Default.Equals(gl, baseValue);
        if (editedInGitLab && force?.Field != field)
        {
            conflicts++;
            ctx.Note(item.Id, link, GitLabConflictKind.WorkbenchOwns, field, text(wb), text(gl),
                $"{GitLabRules.FieldLabel(field)} of {label} was changed in GitLab to {text(gl) ?? "nothing"}. Workbench owns the schedule, so it's {text(wb) ?? "nothing"} again.",
                field == "due" && link.Kind == GitLabObjectKind.Issue ? "Change the date on the project's plan, or choose \"Use GitLab's\" to move the task."
                : field is "due" or "start" ? "Change the date on the project's plan." : "Change it on the project's plan.");
        }

        return new OwnedChange(false);
    }

    private void SetIssueBase(GitLabItemLink link, GitLabIssue issue, Desired wanted, SyncContext ctx)
    {
        link.BaseTitle = issue.Title.Trim();
        link.BaseDescription = GitLabRules.Strip(issue.Description);
        link.BaseAssignees = GitLabRules.Ids((issue.Assignees ?? []).Select(a => a.Id));
        link.BaseDue = issue.DueDate;
        link.BaseState = issue.State;
        link.BaseWbsCode = GitLabRules.WbsCodeOf(issue.Labels);
        link.BaseParentId = ctx.Link.PhaseMode == GitLabPhaseMode.Epics ? issue.Epic?.Id : issue.Milestone?.Id;
        link.BasePercent = null;
        link.GitLabUpdatedAtUtc = issue.UpdatedAt;
        link.LastSyncedAtUtc = Now;
        _ = wanted;
    }

    private static void SetEpicBase(GitLabItemLink link, GitLabEpic epic)
    {
        link.BaseTitle = epic.Title.Trim();
        link.BaseDescription = GitLabRules.Strip(epic.Description);
        link.BaseStart = epic.StartDateFixed;
        link.BaseDue = epic.DueDateFixed;
        link.BaseState = epic.State;
        link.BaseWbsCode = GitLabRules.WbsCodeOf(epic.Labels);
        link.BaseParentId = epic.ParentId;
        link.GitLabUpdatedAtUtc = epic.UpdatedAt;
    }

    private static void SetMilestoneBase(GitLabItemLink link, GitLabMilestone milestone, string? wbs)
    {
        link.BaseTitle = milestone.Title.Trim();
        link.BaseDescription = GitLabRules.Strip(milestone.Description);
        link.BaseStart = milestone.StartDate;
        link.BaseDue = milestone.DueDate;
        link.BaseState = milestone.State;
        link.BaseWbsCode = wbs;
        link.GitLabUpdatedAtUtc = milestone.UpdatedAt;
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? DateLabel(DateOnly? date) => date?.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    private static string Hours(decimal? hours) => hours is { } h ? $"{h.ToString("0.##", CultureInfo.InvariantCulture)} h" : "none";

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n", StringComparison.Ordinal).Trim();

    // ===================================================================== loading

    private async Task<SyncContext?> LoadAsync(Guid linkId, CancellationToken ct)
    {
        var db = await dbFactory.CreateAsync(ct);
        try
        {
            var link = await db.Set<GitLabProjectLink>().FirstOrDefaultAsync(l => l.Id == linkId, ct);
            var connection = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(ct);
            if (link is null || connection is not { IsActive: true })
            {
                await db.DisposeAsync();
                return null;
            }

            var ctx = new SyncContext(db, link, clients.For(connection), people, publisher, clock);
            await ctx.LoadAsync(ct);
            return ctx;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>What Workbench wants GitLab to show for a task.</summary>
    internal sealed record Desired(
        string Title,
        string? Notes,
        IReadOnlyList<long> AssigneeIds,
        DateOnly? Start,
        DateOnly? Due,
        string? Wbs,
        decimal? Estimate,
        long? ParentId,
        IReadOnlyList<long> BlockedBy);

    /// <summary>One sync pass's view of a linked project: the plan, its links and people, and the changes to save.</summary>
    internal sealed class SyncContext(
        IWorkbenchDbContext db, GitLabProjectLink link, GitLabClient client, GitLabPeople people, ISheetChangePublisher publisher, TimeProvider clock) : IAsyncDisposable
    {
        private readonly List<ActivityEvent> _activity = [];
        private readonly HashSet<Guid> _changedItems = [];
        private readonly Dictionary<long, GitLabUser?> _users = [];
        private Dictionary<Guid, (decimal Hours, List<string> Emails)> _staffing = [];
        private List<ItemAssignee> _assignees = [];
        private List<Dependency> _dependencies = [];
        private Dictionary<string, (long Id, string Username)> _byEmail = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<long, string> _byId = [];
        private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private List<GitLabSyncConflict> _openConflicts = [];

        public IWorkbenchDbContext Db => db;
        public GitLabProjectLink Link => link;
        public GitLabClient Client => client;
        public Sheet Sheet { get; private set; } = null!;
        public Dictionary<Guid, Item> Items { get; private set; } = [];
        public Dictionary<Guid, GitLabItemLink> ItemLinks { get; private set; } = [];
        public GitLabRules.PlanShape Shape { get; set; } = null!;
        public Column? PercentColumn { get; private set; }
        public Column? StatusColumn { get; private set; }
        public Column? NotesColumn { get; private set; }

        private DateTime Now => clock.GetUtcNow().UtcDateTime;

        public async Task LoadAsync(CancellationToken ct)
        {
            var sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == link.ProjectId).Select(p => p.SheetId).FirstOrDefaultAsync(ct) ?? link.SheetId;
            Sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, ct);
            Items = await db.Items.Where(i => i.SheetId == sheetId).ToDictionaryAsync(i => i.Id, ct);
            var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct);
            PercentColumn = GitLabColumns.Percent(columns);
            StatusColumn = TaskRules.StatusColumn(columns);
            NotesColumn = GitLabColumns.Notes(columns);
            ItemLinks = (await db.Set<GitLabItemLink>().Where(l => l.LinkId == link.Id).ToListAsync(ct)).ToDictionary(l => l.ItemId);
            Shape = new GitLabRules.PlanShape(Items.Values, link.PhaseLevels);

            var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == link.ProjectId && w.ItemId != null)
                .Select(w => new { w.Id, ItemId = w.ItemId!.Value }).ToListAsync(ct);
            var elementIds = elements.Select(e => e.Id).ToList();
            var staffing = await db.Set<StaffingAssignment>().AsNoTracking()
                .Where(a => a.ProjectId == link.ProjectId && a.WbsElementId != null && elementIds.Contains(a.WbsElementId!.Value))
                .Join(db.Set<Employee>(), a => a.EmployeeId, e => e.Id, (a, e) => new { a.WbsElementId, a.TotalHours, e.Email, e.DisplayName })
                .ToListAsync(ct);
            _staffing = elements.ToDictionary(
                e => e.ItemId,
                e =>
                {
                    var rows = staffing.Where(s => s.WbsElementId == e.Id).ToList();
                    return (rows.Sum(r => r.TotalHours),
                        rows.GroupBy(r => r.Email.ToLowerInvariant()).OrderByDescending(g => g.Sum(r => r.TotalHours)).ThenBy(g => g.Key).Select(g => g.Key).ToList());
                });
            foreach (var s in staffing)
            {
                _names[s.Email] = s.DisplayName;
            }

            _assignees = await db.Set<ItemAssignee>().Where(a => a.SheetId == sheetId).ToListAsync(ct);
            _dependencies = await db.Set<Dependency>().AsNoTracking().Where(d => d.SheetId == sheetId).ToListAsync(ct);
            await ReloadPeopleAsync(ct);
            foreach (var u in await db.Set<Domain.Identity.AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(ct))
            {
                _names.TryAdd(u.Email, u.DisplayName);
            }

            _openConflicts = await db.Set<GitLabSyncConflict>().Where(c => c.LinkId == link.Id && c.Status == GitLabConflictStatus.Open).ToListAsync(ct);
        }

        private async Task ReloadPeopleAsync(CancellationToken ct)
        {
            var maps = await db.Set<GitLabUserMap>().AsNoTracking().ToListAsync(ct);
            _byEmail = maps.ToDictionary(m => m.Email, m => (m.GitLabUserId, m.GitLabUsername), StringComparer.OrdinalIgnoreCase);
            _byId = maps.ToDictionary(m => m.GitLabUserId, m => m.Email);
        }

        /// <summary>Matches people not matched yet (Workbench emails, GitLab users), then reloads the matches.</summary>
        public async Task MatchPeopleAsync(IEnumerable<string> emails, IEnumerable<GitLabUser> gitLabUsers, CancellationToken ct)
        {
            var newEmails = emails.Where(e => IdOf(e) is null).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var newUsers = gitLabUsers.Where(u => EmailOf(u.Id) is null).ToList();
            foreach (var u in gitLabUsers)
            {
                _users[u.Id] = u;
            }

            if (newEmails.Count + newUsers.Count > 0 && await people.MatchAsync(client, newEmails, newUsers, _names, ct) > 0)
            {
                await ReloadPeopleAsync(ct);
            }
        }

        public long? IdOf(string email) => _byEmail.TryGetValue(email, out var m) ? m.Id : null;

        public string? EmailOf(long gitLabUserId) => _byId.GetValueOrDefault(gitLabUserId);

        public string NameOf(string email) => _names.GetValueOrDefault(email) ?? email;

        public async Task<GitLabUser?> GitLabUserAsync(long id, CancellationToken ct)
        {
            if (!_users.TryGetValue(id, out var user))
            {
                try
                {
                    user = await client.GetUserAsync(id, ct);
                }
                catch (GitLabApiException)
                {
                    user = null;
                }

                _users[id] = user;
            }

            return user;
        }

        public async Task<string> PeopleTextAsync(IEnumerable<long> ids, CancellationToken ct)
        {
            var names = new List<string>();
            foreach (var id in ids)
            {
                names.Add(EmailOf(id) is { } email ? NameOf(email) : $"@{(await GitLabUserAsync(id, ct))?.Username ?? id.ToString(CultureInfo.InvariantCulture)}");
            }

            return names.Count == 0 ? "nobody" : string.Join(", ", names);
        }

        public IEnumerable<Guid> SuccessorsOf(Guid itemId) => _dependencies.Where(d => d.PredecessorId == itemId).Select(d => d.SuccessorId).Distinct();

        public HashSet<string> StaffedEmails(Guid itemId) =>
            _staffing.TryGetValue(itemId, out var s) ? s.Emails.ToHashSet(StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> Assignees(Guid itemId) =>
            _assignees.Where(a => a.ItemId == itemId).Select(a => a.Email).ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>Everyone on the task: staffed people (most hours first), then its other assignees.</summary>
        public IReadOnlyList<string> PeopleOf(Guid itemId)
        {
            var list = _staffing.TryGetValue(itemId, out var s) ? s.Emails.ToList() : [];
            foreach (var email in _assignees.Where(a => a.ItemId == itemId).OrderBy(a => a.AssignedAtUtc).Select(a => a.Email))
            {
                if (!list.Contains(email, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(email);
                }
            }

            return list;
        }

        /// <summary>People on linked tasks with no GitLab match, and how many tasks each is on.</summary>
        public IEnumerable<(string Email, int Tasks)> Unmatched() =>
            ItemLinks.Values.Where(l => l.Kind == GitLabObjectKind.Issue && !l.IsRemoved && Items.ContainsKey(l.ItemId))
                .SelectMany(l => PeopleOf(l.ItemId)).Where(e => IdOf(e) is null)
                .GroupBy(e => e, StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count()));

        public void AddAssignee(Item item, string email)
        {
            var row = new ItemAssignee { SheetId = item.SheetId, ItemId = item.Id, Email = email.ToLowerInvariant(), AssignedBy = Actor, AssignedAtUtc = Now };
            db.Set<ItemAssignee>().Add(row);
            _assignees.Add(row);
        }

        public void RemoveAssignee(Item item, string email)
        {
            foreach (var row in _assignees.Where(a => a.ItemId == item.Id && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                db.Set<ItemAssignee>().Remove(row);
                _assignees.Remove(row);
            }
        }

        public Desired Desired(Item item, GitLabObjectKind kind)
        {
            var assignees = new List<long>();
            if (kind == GitLabObjectKind.Issue)
            {
                foreach (var email in PeopleOf(item.Id))
                {
                    if (IdOf(email) is { } id && !assignees.Contains(id))
                    {
                        assignees.Add(id);
                    }
                }
            }

            long? parent = null;
            if (kind == GitLabObjectKind.Issue || kind == GitLabObjectKind.Epic)
            {
                var phase = Shape.PhaseOf(item.Id);
                if (phase is not null && ItemLinks.GetValueOrDefault(phase.Id) is { IsRemoved: false } phaseLink
                    && phaseLink.Kind == (link.PhaseMode == GitLabPhaseMode.Epics ? GitLabObjectKind.Epic : GitLabObjectKind.Milestone))
                {
                    parent = phaseLink.GitLabId;
                }
            }

            var blockedBy = new List<long>();
            if (kind == GitLabObjectKind.Issue)
            {
                foreach (var d in _dependencies.Where(d => d.SuccessorId == item.Id))
                {
                    if (ItemLinks.GetValueOrDefault(d.PredecessorId) is { Kind: GitLabObjectKind.Issue, IsRemoved: false } pred && Items.ContainsKey(d.PredecessorId))
                    {
                        blockedBy.Add(pred.Iid);
                    }
                }
            }

            var hours = _staffing.TryGetValue(item.Id, out var s) && s.Hours > 0 ? s.Hours : (decimal?)null;
            return new Desired(
                item.Title.Trim(),
                NotesColumn is { } notes ? GitLabRules.NormalizeNotes(item.Values.GetValueOrDefault(notes.Id.ToString())) : null,
                assignees,
                item.Start,
                item.Finish,
                string.IsNullOrWhiteSpace(item.CostpointWbsId) ? null : item.CostpointWbsId.Trim(),
                kind == GitLabObjectKind.Issue ? hours : null,
                parent,
                blockedBy.Distinct().Order().ToList());
        }

        public string Description(Item item, Desired wanted) =>
            GitLabRules.Compose(wanted.Notes, item.Id, wanted.Wbs, wanted.Start, wanted.Due, _staffing.TryGetValue(item.Id, out var s) && s.Hours > 0 ? s.Hours : null, null);

        public string? PhaseName(long? gitLabId) =>
            gitLabId is { } id && ItemLinks.Values.FirstOrDefault(l => l.GitLabId == id && l.Kind != GitLabObjectKind.Issue) is { } l && Items.GetValueOrDefault(l.ItemId) is { } i
                ? $"{GitLabRules.Label(l.Kind, l.Iid)} {i.Title}"
                : gitLabId?.ToString(CultureInfo.InvariantCulture);

        public decimal? PercentOf(Item item) =>
            PercentColumn is { } c ? ScheduleEngine.Percent(item.Values.GetValueOrDefault(c.Id.ToString())) : null;

        public bool SetPercent(Item item, decimal percent)
        {
            if (PercentColumn is not { } c || PercentOf(item) == percent)
            {
                return false;
            }

            item.Values = new Dictionary<string, string?>(item.Values) { [c.Id.ToString()] = GitLabRules.Percent(percent) };
            _changedItems.Add(item.Id);
            return true;
        }

        public bool IsDone(Item item) =>
            StatusColumn is { } c ? TaskRules.IsDone(item.Values.GetValueOrDefault(c.Id.ToString())) : PercentOf(item) >= 100;

        /// <summary>Sets the Status column to match open/closed (when the plan has one): a done option when closed.</summary>
        public void SetDone(Item item, bool closed)
        {
            if (StatusColumn is not { } c)
            {
                return;
            }

            var key = c.Id.ToString();
            var value = item.Values.GetValueOrDefault(key);
            if (TaskRules.IsDone(value) == closed)
            {
                return;
            }

            var option = closed
                ? c.Options.FirstOrDefault(TaskRules.IsDone)
                : c.Options.FirstOrDefault(o => o.Contains("progress", StringComparison.OrdinalIgnoreCase)) ?? c.Options.FirstOrDefault(o => !TaskRules.IsDone(o));
            if (option is null && closed)
            {
                return;
            }

            item.Values = new Dictionary<string, string?>(item.Values) { [key] = option };
            _changedItems.Add(item.Id);
        }

        public void SetDates(Item item, DateOnly? start, DateOnly? finish)
        {
            item.Start = start;
            item.Finish = finish;
            _changedItems.Add(item.Id);
        }

        /// <summary>A sync finished without a GitLab error: the link is healthy again.</summary>
        public void Synced()
        {
            link.LastSyncedAtUtc = Now;
            link.Status = GitLabLinkStatus.Active;
            link.LastError = null;
            link.LastErrorHint = null;
        }

        public void Touch(Item item)
        {
            item.ModifiedAtUtc = Now;
            item.ModifiedBy = Actor;
            _changedItems.Add(item.Id);
        }

        public void Activity(Guid itemId, string kind, string summary) => _activity.Add(new ActivityEvent
        {
            SheetId = Sheet.Id,
            ItemId = itemId,
            Actor = Actor,
            Kind = kind,
            Summary = summary.Length > 2_000 ? summary[..1_999] + "…" : summary,
            OccurredAtUtc = Now,
        });

        /// <summary>Records a sync decision on the panel. An open note about the same task and field is updated rather than repeated.</summary>
        public void Note(Guid? itemId, GitLabItemLink? itemLink, GitLabConflictKind kind, string field, string? workbenchValue, string? gitLabValue, string outcome, string? hint)
        {
            var now = Now;
            var existing = _openConflicts.FirstOrDefault(c => c.ItemId == itemId && c.Field == field && c.Kind == kind
                && (kind != GitLabConflictKind.PersonNotMatched || c.GitLabValue == gitLabValue));
            if (existing is null)
            {
                existing = new GitLabSyncConflict { LinkId = link.Id, ItemId = itemId, Field = field, Outcome = "", Kind = kind, CreatedAtUtc = now };
                db.Set<GitLabSyncConflict>().Add(existing);
                _openConflicts.Add(existing);
            }

            existing.Iid = itemLink?.Iid;
            existing.ObjectKind = itemLink?.Kind;
            existing.WorkbenchValue = workbenchValue is null ? null : GitLabRules.Clip(workbenchValue, 2000);
            existing.GitLabValue = gitLabValue is null ? null : GitLabRules.Clip(gitLabValue, 2000);
            existing.Outcome = GitLabRules.Clip(outcome, 1000);
            existing.Hint = hint is null ? null : GitLabRules.Clip(hint, 1000);
            existing.UpdatedAtUtc = now;
            if (itemId is { } id && kind != GitLabConflictKind.PersonNotMatched)
            {
                Activity(id, "item.gitlab", outcome);
            }
        }

        /// <summary>Saves a new item link on its own; false when another sync linked the task first (the unique key).</summary>
        public async Task<bool> TrySaveLinkAsync(GitLabItemLink itemLink, CancellationToken ct)
        {
            try
            {
                await db.SaveChangesAsync(ct);
                return true;
            }
            catch (DbUpdateException)
            {
                if (db is DbContext context)
                {
                    context.Entry(itemLink).State = EntityState.Detached;
                }

                ItemLinks.Remove(itemLink.ItemId);
                return false;
            }
        }

        public async Task SaveAsync(CancellationToken ct)
        {
            if (_activity.Count > 0)
            {
                db.Set<ActivityEvent>().AddRange(_activity);
            }

            link.UpdatedAtUtc = Now;
            await db.SaveChangesAsync(ct);
            _activity.Clear();
            if (_changedItems.Count > 0)
            {
                // Roll summaries up (% complete) and renumber; then tell open plans.
                await PlanReflow.ReflowAndSaveAsync(db, Sheet.Id, ct, clock);
                var changed = _changedItems.ToList();
                _changedItems.Clear();

                // Saving stamped the rows (SheetRowStamps): remember the stamp, so the next sync doesn't take its own change for a Workbench edit.
                foreach (var id in changed)
                {
                    if (ItemLinks.GetValueOrDefault(id) is { } itemLink && Items.GetValueOrDefault(id) is { } item)
                    {
                        itemLink.WorkbenchModifiedAtUtc = item.ModifiedAtUtc;
                    }
                }

                await db.SaveChangesAsync(ct);
                foreach (var id in changed)
                {
                    await publisher.PublishAsync(new SheetChange(Sheet.Id, SheetChangeKind.ItemUpdated, id, null, Actor, Now));
                }
            }
        }

        public ValueTask DisposeAsync()
        {
            client.Dispose();
            return db.DisposeAsync();
        }
    }
}

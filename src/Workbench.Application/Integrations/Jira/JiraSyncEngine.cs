using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Collaboration;
using Workbench.Application.Scheduling;
using Workbench.Application.Tasks;
using Workbench.Contracts.Collaboration;
using Workbench.Domain;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Integrations.Jira;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using Workbench.Domain.Tasks;

namespace Workbench.Application.Integrations.Jira;

/// <summary>What one sync pass did, for the run log and "Sync now".</summary>
public sealed record JiraSyncReport(int Synced, int Created, int Failed, string? Error = null)
{
    public static JiraSyncReport Empty { get; } = new(0, 0, 0);

    public JiraSyncReport Add(JiraSyncReport other) => new(Synced + other.Synced, Created + other.Created, Failed + other.Failed, Error ?? other.Error);
}

/// <summary>
/// Syncs a linked project's plan with its Jira issues. The rules, in plain words (docs/integrations/jira.md):
/// <list type="bullet">
/// <item>Jira owns the title and the assignee: Workbench takes Jira's whenever the issue is synced.</item>
/// <item>Status flows both ways: an issue in Jira's Done category makes the task Done; marking a task Done moves the issue to Done.
/// Reopening a task is not sent to Jira.</item>
/// <item>The due date flows both ways; when both sides changed it since the last sync, the link's setting decides (Workbench's by default).</item>
/// <item>Jira's original estimate is shown on the task; time itself never comes across (timesheets are the cost record).</item>
/// </list>
/// Each pair compares both sides with the values they had at the last sync (the base), so a change is told from an echo.
/// Nothing is dropped silently: changes go to the task's activity log, and what the sync couldn't do goes on the project's panel.
/// </summary>
public sealed class JiraSyncEngine(
    IWorkbenchDbContextFactory dbFactory,
    JiraClientFactory clients,
    ISheetChangePublisher publisher,
    IOptions<JiraOptions> options,
    TimeProvider clock,
    ILogger<JiraSyncEngine> logger)
{
    /// <summary>The actor of every change the sync makes in Workbench (activity, row stamps, live updates).</summary>
    public const string Actor = "Jira";

    private static readonly string[] EstimateColumnNames = ["Estimate (hours)", "Estimated hours", "Estimate", "Hours"];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ===================================================================== entry points

    /// <summary>Syncs one task after it changed in Workbench: sends Done and a changed due date, or creates its issue when the link says to.</summary>
    public async Task<JiraSyncReport> SyncTaskAsync(Guid linkId, Guid itemId, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return JiraSyncReport.Empty;
        }

        var report = JiraSyncReport.Empty;
        try
        {
            if (!ctx.Items.TryGetValue(itemId, out var item))
            {
                if (ctx.ItemLinks.GetValueOrDefault(itemId) is { IsRemoved: false } gone)
                {
                    gone.IsRemoved = true; // the issue stays in Jira
                    gone.LastSyncedAtUtc = Now;
                }

                await ctx.SaveAsync(ct);
                return JiraSyncReport.Empty;
            }

            if (!ctx.ItemLinks.TryGetValue(itemId, out var itemLink))
            {
                if (ctx.WantsIssueFor(item))
                {
                    report = await CreateIssueForAsync(ctx, item, ct);
                }
            }
            else if (!itemLink.IsRemoved && ctx.WorkbenchChanged(item, itemLink))
            {
                var issue = await ctx.Client.GetIssueAsync(itemLink.IssueKey, ct);
                report = issue is null ? await MarkDeletedAsync(ctx, itemLink, ct) : await SyncPairAsync(ctx, item, itemLink, issue, created: false, ct);
            }

            ctx.Synced();
        }
        catch (JiraApiException ex)
        {
            ctx.Failed(ex, itemId);
            report = report with { Failed = report.Failed + 1, Error = ex.Message };
            await ctx.SaveAsync(ct);
            throw;
        }

        await ctx.SaveAsync(ct);
        return report;
    }

    /// <summary>Syncs one issue (a webhook named it): creates its task or updates the task it is linked to. An issue outside the link's filter is left alone.</summary>
    public async Task<JiraSyncReport> SyncIssueAsync(Guid linkId, string issueKey, bool deleted, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return JiraSyncReport.Empty;
        }

        JiraSyncReport report;
        try
        {
            if (deleted)
            {
                report = ctx.ItemLinks.Values.FirstOrDefault(l => string.Equals(l.IssueKey, issueKey, StringComparison.OrdinalIgnoreCase)) is { } link
                    ? await MarkDeletedAsync(ctx, link, ct)
                    : JiraSyncReport.Empty;
            }
            else
            {
                var found = await ctx.Client.SearchIssuesAsync($"({ctx.Scope}) AND key = {JiraUrls.Quote(issueKey)}", 1, ct);
                report = found.Count == 0 ? JiraSyncReport.Empty : await PullIssueAsync(ctx, found[0], ct);
            }

            ctx.Synced();
        }
        catch (JiraApiException ex)
        {
            ctx.Failed(ex, null);
            await ctx.SaveAsync(ct);
            throw;
        }

        try
        {
            await ctx.SaveAsync(ct);
        }
        catch (Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException)
        {
            // Another delivery for the same issue saved first (webhooks and the poll can overlap). Nothing was lost: its
            // save is the same change, and the next poll reconciles anything it missed.
            return JiraSyncReport.Empty;
        }

        return report;
    }

    /// <summary>
    /// The backstop: reads the issues that changed in Jira since the last poll and syncs them, then sends what changed in
    /// Workbench that no event reported. <paramref name="everything"/> reads every issue (the first sync after linking, and "Sync now").
    /// A Jira that is down is recorded on the link and never throws: the next poll tries again.
    /// </summary>
    public async Task<JiraSyncReport> ReconcileAsync(Guid linkId, bool everything, CancellationToken ct)
    {
        await using var ctx = await LoadAsync(linkId, ct);
        if (ctx is null)
        {
            return JiraSyncReport.Empty;
        }

        var started = Now;
        var report = JiraSyncReport.Empty;
        try
        {
            var jql = ctx.Scope;
            if (!everything && ctx.Link.LastReconcileAtUtc is { } last)
            {
                // Relative minutes, so Jira's time zone never matters; a few minutes of overlap cover clock drift.
                var minutes = (int)Math.Ceiling((started - last).TotalMinutes) + 5;
                jql = $"({jql}) AND updated >= -{Math.Max(10, minutes)}m";
            }

            var issues = await ctx.Client.SearchIssuesAsync(jql + " ORDER BY updated ASC", Math.Max(1, options.Value.MaxPagesPerReconcile), ct);
            foreach (var issue in issues)
            {
                report = report.Add(await PullIssueAsync(ctx, issue, ct));
            }

            foreach (var itemLink in ctx.ItemLinks.Values.Where(l => !l.IsRemoved).ToList())
            {
                if (ctx.Items.GetValueOrDefault(itemLink.ItemId) is not { } item)
                {
                    itemLink.IsRemoved = true;
                }
                else if (ctx.WorkbenchChanged(item, itemLink))
                {
                    var issue = await ctx.Client.GetIssueAsync(itemLink.IssueKey, ct);
                    report = report.Add(issue is null ? await MarkDeletedAsync(ctx, itemLink, ct) : await SyncPairAsync(ctx, item, itemLink, issue, created: false, ct));
                }
            }

            foreach (var item in ctx.Items.Values.Where(ctx.WantsIssueFor).OrderBy(i => i.Order).ToList())
            {
                report = report.Add(await CreateIssueForAsync(ctx, item, ct));
            }

            ctx.Synced();
            ctx.Link.LastReconcileAtUtc = started;
        }
        catch (JiraApiException ex)
        {
            ctx.Failed(ex, null);
            ctx.Link.LastReconcileAtUtc = started;
            report = report with { Error = ex.Message, Failed = report.Failed + 1 };
            logger.LogWarning("Jira poll of link {LinkId} failed: {Message}", linkId, ex.Message);
        }

        await ctx.SaveAsync(ct);
        return report;
    }

    // ===================================================================== pull

    /// <summary>An issue read from Jira: update its task, or add the task when it has none.</summary>
    private async Task<JiraSyncReport> PullIssueAsync(JiraSyncContext ctx, JiraIssue issue, CancellationToken ct)
    {
        if (ctx.IssueLinks.TryGetValue(issue.Id, out var existing))
        {
            if (existing.IsRemoved)
            {
                existing.IsRemoved = false;
            }

            return ctx.Items.GetValueOrDefault(existing.ItemId) is { } item
                ? await SyncPairAsync(ctx, item, existing, issue, created: false, ct)
                : JiraSyncReport.Empty; // the task was deleted in Workbench; the issue stays in Jira and is not re-added
        }

        // An issue made from a Workbench task whose link wasn't saved (a crash in between) carries the task's label: link that task instead of adding another.
        var labelled = issue.Labels.Select(JiraRules.TaskIdFromLabel).FirstOrDefault(id => id is { } t && ctx.Items.ContainsKey(t) && !ctx.ItemLinks.ContainsKey(t));
        if (labelled is { } taskId)
        {
            var adopted = new JiraItemLink { LinkId = ctx.Link.Id, ItemId = taskId, IssueId = issue.Id, IssueKey = issue.Key, WebUrl = ctx.Client.BrowseUrl(issue.Key), BaseWorkbenchDue = ctx.Items[taskId].Finish };
            ctx.Db.Set<JiraItemLink>().Add(adopted);
            ctx.ItemLinks[taskId] = adopted;
            ctx.IssueLinks[issue.Id] = adopted;
            return await SyncPairAsync(ctx, ctx.Items[taskId], adopted, issue, created: false, ct);
        }

        var order = ctx.Items.Values.Where(i => i.ParentId == null).Select(i => i.Order).DefaultIfEmpty(-1).Max() + 1;
        var now = Now;
        var item2 = new Item
        {
            SheetId = ctx.Sheet.Id,
            Order = order,
            Title = JiraRules.Clip(issue.Summary.Trim(), 500),
            Finish = issue.DueDate,
            Start = issue.DueDate,
            WbsCodeIsAuto = true,
            CreatedAtUtc = now,
            CreatedBy = Actor,
            ModifiedAtUtc = now,
            ModifiedBy = Actor,
        };
        var link = new JiraItemLink
        {
            LinkId = ctx.Link.Id, ItemId = item2.Id, IssueId = issue.Id, IssueKey = issue.Key, WebUrl = ctx.Client.BrowseUrl(issue.Key),
        };
        ctx.Db.Items.Add(item2);
        ctx.Items[item2.Id] = item2;
        ctx.Db.Set<JiraItemLink>().Add(link);
        ctx.ItemLinks[item2.Id] = link;
        ctx.IssueLinks[issue.Id] = link;
        ctx.Activity(item2.Id, ActivityKinds.ItemCreated, $"Added \"{item2.Title}\" from Jira {issue.Key}");
        var report = await SyncPairAsync(ctx, item2, link, issue, created: true, ct);
        return report with { Created = report.Created + 1, Synced = Math.Max(0, report.Synced - 1) };
    }

    // ===================================================================== both ways

    private async Task<JiraSyncReport> SyncPairAsync(JiraSyncContext ctx, Item item, JiraItemLink link, JiraIssue issue, bool created, CancellationToken ct)
    {
        var changed = false;
        link.IssueKey = issue.Key;
        link.WebUrl = ctx.Client.BrowseUrl(issue.Key);
        link.JiraStatus = issue.Status;
        link.EstimateHours = issue.EstimateHours;

        // Title: Jira wins.
        var title = JiraRules.Clip(issue.Summary.Trim(), 500);
        if (title.Length > 0 && !string.Equals(item.Title, title, StringComparison.Ordinal))
        {
            ctx.Activity(item.Id, "item.jira", $"Title changed from Jira {issue.Key}: \"{item.Title}\" is now \"{title}\"");
            item.Title = title;
            changed = true;
        }

        link.BaseTitle = item.Title;

        // Assignee: Jira wins.
        changed |= await SyncAssigneeAsync(ctx, item, link, issue, ct);

        // Status: both ways.
        var wbDone = ctx.IsDone(item);
        var jiraDone = issue.IsDone;
        var pullStatus = created || jiraDone != link.BaseJiraDone;
        var pushStatus = !created && wbDone != link.BaseWorkbenchDone;
        var pushedDone = false;
        if (pullStatus && pushStatus && wbDone != jiraDone)
        {
            ctx.Note(item, link, "status", $"Both sides changed the status; Jira's ({issue.Status}) was kept.", null, isError: false);
            pushStatus = false;
        }

        if (pullStatus && wbDone != jiraDone)
        {
            if (ctx.SetDone(item, jiraDone))
            {
                ctx.Activity(item.Id, "item.jira", jiraDone ? $"Marked Done because Jira {issue.Key} is {issue.Status}" : $"Reopened because Jira {issue.Key} is {issue.Status}");
                changed = true;
            }
            else if (jiraDone)
            {
                ctx.Note(item, link, "status", $"Jira {issue.Key} is Done but this plan has no Done status to set.", "Add a Status column with a Done option, or a % Complete column, on the Plan tab.", isError: false);
            }
        }
        else if (pushStatus && wbDone && !jiraDone)
        {
            try
            {
                if (await ctx.Client.TransitionToDoneAsync(issue.Key, ct))
                {
                    pushedDone = true;
                    ctx.Activity(item.Id, "item.jira", $"Moved Jira {issue.Key} to Done");
                    ctx.ClearNote(item.Id, "status");
                }
                else
                {
                    ctx.Note(item, link, "status", $"Jira {issue.Key} is \"{issue.Status}\" and its workflow has no way to Done from there.",
                        "Move it to Done in Jira, or ask your Jira admin to add the transition.", isError: true);
                }
            }
            catch (JiraApiException ex) when (!ex.IsRetryable)
            {
                ctx.Note(item, link, "status", $"Couldn't move Jira {issue.Key} to Done: {ex.Message}", ex.Hint, isError: true);
            }
        }
        else if (pushStatus && !wbDone && jiraDone)
        {
            ctx.Note(item, link, "status", $"This task was reopened, but Jira {issue.Key} stays Done: reopening isn't sent to Jira.", "Reopen it in Jira too if the work isn't finished.", isError: false);
        }

        link.BaseWorkbenchDone = ctx.IsDone(item);
        link.BaseJiraDone = pushedDone || jiraDone;

        // Due date: both ways; the link's setting decides when both changed.
        changed |= await SyncDueAsync(ctx, item, link, issue, created, ct);

        // Estimate: shown on the task (and in the plan's estimate column when it has one); never time.
        if (ctx.SetEstimate(item, issue.EstimateHours))
        {
            changed = true;
        }

        if (changed)
        {
            ctx.Touch(item);
        }

        link.LastSyncedAtUtc = Now;
        link.LastError = null;
        link.WorkbenchModifiedAtUtc = item.ModifiedAtUtc;
        link.Version = Guid.NewGuid();
        return new JiraSyncReport(changed || created ? 1 : 0, 0, 0);
    }

    private async Task<bool> SyncAssigneeAsync(JiraSyncContext ctx, Item item, JiraItemLink link, JiraIssue issue, CancellationToken ct)
    {
        var accountId = issue.Assignee?.AccountId;
        if (accountId == link.BaseAssigneeAccountId && (accountId is null || link.BaseAssigneeEmail is not null))
        {
            return false;
        }

        var email = issue.Assignee is { } who ? ctx.PersonFor(who) : null;
        if (email is null && issue.Assignee is { } unknown)
        {
            // Jira hides most emails; ask it for the account of each Workbench person on the plan once, so a match by email still works.
            email = await ctx.FindByLookupAsync(unknown, ct);
        }

        var changed = false;
        if (link.BaseAssigneeEmail is { } before && !string.Equals(before, email, StringComparison.OrdinalIgnoreCase) && ctx.RemoveAssignee(item, before))
        {
            ctx.Activity(item.Id, "item.jira", $"Unassigned {ctx.NameOf(before)} because Jira {issue.Key} was reassigned");
            changed = true;
        }

        if (email is not null)
        {
            if (ctx.AddAssignee(item, email))
            {
                ctx.Activity(item.Id, "item.jira", $"Assigned {ctx.NameOf(email)} from Jira {issue.Key}");
                changed = true;
            }

            ctx.ClearNote(item.Id, "assignee");
        }
        else if (issue.Assignee is { } nobody)
        {
            ctx.Note(item, link, "assignee", $"Jira {issue.Key} is assigned to {nobody.DisplayName}, who isn't a person in Workbench by email or name.",
                "Add them as a person with the same email they use in Jira, and the next sync assigns them.", isError: false);
        }

        link.BaseAssigneeAccountId = accountId;
        link.BaseAssigneeEmail = email;
        return changed;
    }

    private async Task<bool> SyncDueAsync(JiraSyncContext ctx, Item item, JiraItemLink link, JiraIssue issue, bool created, CancellationToken ct)
    {
        var changed = false;
        var jiraDue = issue.DueDate;
        var wbDue = item.Finish;
        var jiraChanged = created || jiraDue != link.BaseJiraDue;
        var wbChanged = !created && wbDue != link.BaseWorkbenchDue;
        var pull = false;
        var push = false;
        if (jiraDue != wbDue)
        {
            if (jiraChanged && wbChanged)
            {
                var jiraWins = ctx.Link.DateWinner == JiraDateWinner.Jira;
                pull = jiraWins;
                push = !jiraWins;
                ctx.Note(item, link, "due",
                    $"The due date changed on both sides since the last sync (Workbench {JiraRules.Date(wbDue)}, Jira {JiraRules.Date(jiraDue)}); {(jiraWins ? "Jira's" : "Workbench's")} was kept.",
                    "Change who wins on this project's Jira panel.", isError: false);
            }
            else
            {
                pull = jiraChanged;
                push = wbChanged;
            }
        }

        if (pull && jiraDue is { } due)
        {
            item.Finish = due;
            if (item.Start is null || item.Start > due || created)
            {
                item.Start = due;
            }

            ctx.Activity(item.Id, "item.jira", $"Due date set to {JiraRules.Date(due)} from Jira {issue.Key}");
            changed = true;
        }
        else if (push)
        {
            try
            {
                await ctx.Client.SetDueDateAsync(issue.Key, wbDue, ct);
                jiraDue = wbDue;
                ctx.Activity(item.Id, "item.jira", $"Due date {JiraRules.Date(wbDue)} sent to Jira {issue.Key}");
            }
            catch (JiraApiException ex) when (!ex.IsRetryable)
            {
                ctx.Note(item, link, "due", $"Couldn't send the due date to Jira {issue.Key}: {ex.Message}", ex.Hint, isError: true);
                return changed;
            }
        }

        link.BaseWorkbenchDue = item.Finish;
        link.BaseJiraDue = jiraDue;
        if (pull || push)
        {
            ctx.ClearNote(item.Id, "due", keepIfInfo: true);
        }

        return changed;
    }

    // ===================================================================== workbench -> jira

    /// <summary>Creates a Jira issue for a task added in Workbench (when the link says to). A retry after a crash finds the issue by its label instead of making a second.</summary>
    private async Task<JiraSyncReport> CreateIssueForAsync(JiraSyncContext ctx, Item item, CancellationToken ct)
    {
        var label = JiraRules.TaskLabel(item.Id);
        var existing = (await ctx.Client.SearchIssuesAsync($"project = {JiraUrls.Quote(ctx.Link.JiraProjectKey)} AND labels = {JiraUrls.Quote(label)}", 1, ct)).FirstOrDefault();
        JiraIssue issue;
        try
        {
            issue = existing ?? await ctx.Client.CreateIssueAsync(ctx.Link.JiraProjectKey, ctx.Link.IssueType, JiraRules.Clip(item.Title.Trim(), 250), item.Finish, label, ct);
        }
        catch (JiraApiException ex) when (!ex.IsRetryable)
        {
            ctx.Note(item, null, "sync", $"Couldn't create a Jira issue for \"{item.Title}\": {ex.Message}", ex.Hint, isError: true);
            return new JiraSyncReport(0, 0, 1, ex.Message);
        }

        var link = new JiraItemLink
        {
            LinkId = ctx.Link.Id, ItemId = item.Id, IssueId = issue.Id, IssueKey = issue.Key, WebUrl = ctx.Client.BrowseUrl(issue.Key),
            BaseWorkbenchDone = false, BaseJiraDone = issue.IsDone, BaseWorkbenchDue = item.Finish, BaseJiraDue = issue.DueDate, BaseTitle = item.Title,
        };
        ctx.Db.Set<JiraItemLink>().Add(link);
        ctx.ItemLinks[item.Id] = link;
        ctx.IssueLinks[issue.Id] = link;
        ctx.Activity(item.Id, "item.jira", $"Created Jira {issue.Key} for this task");
        var report = await SyncPairAsync(ctx, item, link, issue, created: false, ct);
        return report with { Created = 1, Synced = 0 };
    }

    private async Task<JiraSyncReport> MarkDeletedAsync(JiraSyncContext ctx, JiraItemLink link, CancellationToken ct)
    {
        if (link.IsRemoved)
        {
            return JiraSyncReport.Empty;
        }

        link.IsRemoved = true;
        link.LastError = "This issue was deleted in Jira (or the account can no longer see it). The task stays in the plan.";
        link.LastSyncedAtUtc = Now;
        ctx.Activity(link.ItemId, "item.jira", $"Jira {link.IssueKey} was deleted; this task is no longer linked");
        await Task.CompletedTask;
        return new JiraSyncReport(1, 0, 0);
    }

    // ===================================================================== context

    private async Task<JiraSyncContext?> LoadAsync(Guid linkId, CancellationToken ct)
    {
        var db = await dbFactory.CreateAsync(ct);
        try
        {
            var link = await db.Set<JiraProjectLink>().FirstOrDefaultAsync(l => l.Id == linkId, ct);
            var connection = await db.Set<JiraConnection>().AsNoTracking().FirstOrDefaultAsync(ct);
            if (link is null || connection is not { IsActive: true })
            {
                await db.DisposeAsync();
                return null;
            }

            var ctx = new JiraSyncContext(db, link, clients.For(connection), publisher, clock);
            await ctx.LoadAsync(ct);
            return ctx;
        }
        catch
        {
            await db.DisposeAsync();
            throw;
        }
    }

    /// <summary>One sync pass's view of a linked project: the plan, its links and people, and the changes to save.</summary>
    internal sealed class JiraSyncContext(IWorkbenchDbContext db, JiraProjectLink link, IJiraClient client, ISheetChangePublisher publisher, TimeProvider clock) : IAsyncDisposable
    {
        private readonly List<ActivityEvent> _activity = [];
        private readonly HashSet<Guid> _changedItems = [];
        private readonly Dictionary<string, string?> _lookups = new(StringComparer.Ordinal);
        private List<ItemAssignee> _assignees = [];
        private Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
        private List<JiraSyncNote> _notes = [];

        public IWorkbenchDbContext Db => db;
        public JiraProjectLink Link => link;
        public IJiraClient Client => client;
        public Sheet Sheet { get; private set; } = null!;
        public Dictionary<Guid, Item> Items { get; private set; } = [];
        public Dictionary<Guid, JiraItemLink> ItemLinks { get; private set; } = [];
        public Dictionary<string, JiraItemLink> IssueLinks { get; private set; } = [];
        public Column? StatusColumn { get; private set; }
        public Column? PercentColumn { get; private set; }
        public Column? EstimateColumn { get; private set; }

        private DateTime Now => clock.GetUtcNow().UtcDateTime;

        /// <summary>The JQL of the issues this link covers (never ordered).</summary>
        public string Scope
        {
            get
            {
                var jql = $"project = {JiraUrls.Quote(link.JiraProjectKey)}";
                if (!string.IsNullOrWhiteSpace(link.EpicKey))
                {
                    jql += $" AND parent = {JiraUrls.Quote(link.EpicKey.Trim())}";
                }

                if (!string.IsNullOrWhiteSpace(link.Jql))
                {
                    jql += $" AND ({JiraRules.StripOrderBy(link.Jql)})";
                }

                return jql;
            }
        }

        public async Task LoadAsync(CancellationToken ct)
        {
            var sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == link.ProjectId).Select(p => p.SheetId).FirstOrDefaultAsync(ct) ?? link.SheetId;
            Sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, ct);
            Items = await db.Items.Where(i => i.SheetId == sheetId).ToDictionaryAsync(i => i.Id, ct);
            var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(ct);
            StatusColumn = TaskRules.StatusColumn(columns);
            PercentColumn = TaskRules.PercentColumn(columns);
            EstimateColumn = EstimateColumnNames
                .Select(n => columns.FirstOrDefault(c => c.Type == ColumnType.Number && c.Formula is null && string.Equals(c.Name, n, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(c => c is not null);
            ItemLinks = (await db.Set<JiraItemLink>().Where(l => l.LinkId == link.Id).ToListAsync(ct)).ToDictionary(l => l.ItemId);
            IssueLinks = ItemLinks.Values.ToDictionary(l => l.IssueId, StringComparer.Ordinal);
            _assignees = await db.Set<ItemAssignee>().Where(a => a.SheetId == sheetId).ToListAsync(ct);
            foreach (var e in await db.Set<Employee>().AsNoTracking().Select(e => new { e.Email, e.DisplayName }).ToListAsync(ct))
            {
                _names.TryAdd(e.Email.ToLowerInvariant(), e.DisplayName);
            }

            foreach (var u in await db.Set<Domain.Identity.AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(ct))
            {
                _names.TryAdd(u.Email.ToLowerInvariant(), u.DisplayName);
            }

            _notes = await db.Set<JiraSyncNote>().Where(n => n.LinkId == link.Id && n.IsOpen).ToListAsync(ct);
        }

        public string NameOf(string email) => _names.GetValueOrDefault(email) ?? email;

        /// <summary>The Workbench person for a Jira user: by the email Jira shows, else by a display name only one person has.</summary>
        public string? PersonFor(JiraUser user)
        {
            if (user.Email is { } email && _names.ContainsKey(email.ToLowerInvariant()))
            {
                return email.ToLowerInvariant();
            }

            var byName = _names.Where(n => string.Equals(n.Value, user.DisplayName, StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).Take(2).ToList();
            return byName.Count == 1 ? byName[0] : null;
        }

        /// <summary>Asks Jira which of the people on this plan has this account (Jira can find an account by email even when it hides it).</summary>
        public async Task<string?> FindByLookupAsync(JiraUser user, CancellationToken ct)
        {
            if (_lookups.TryGetValue(user.AccountId, out var cached))
            {
                return cached;
            }

            string? match = null;
            foreach (var email in PlanPeople().Take(50))
            {
                var found = await client.FindUserByEmailAsync(email, ct);
                if (found?.AccountId == user.AccountId)
                {
                    match = email;
                    break;
                }
            }

            _lookups[user.AccountId] = match;
            return match;
        }

        private IEnumerable<string> PlanPeople() => _names.Keys.OrderBy(k => k, StringComparer.Ordinal);

        public bool AddAssignee(Item item, string email)
        {
            email = email.ToLowerInvariant();
            if (_assignees.Any(a => a.ItemId == item.Id && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            var row = new ItemAssignee { SheetId = item.SheetId, ItemId = item.Id, Email = email, AssignedBy = Actor, AssignedAtUtc = Now };
            db.Set<ItemAssignee>().Add(row);
            _assignees.Add(row);
            _changedItems.Add(item.Id);
            return true;
        }

        public bool RemoveAssignee(Item item, string email)
        {
            var rows = _assignees.Where(a => a.ItemId == item.Id && string.Equals(a.Email, email, StringComparison.OrdinalIgnoreCase)).ToList();
            foreach (var row in rows)
            {
                db.Set<ItemAssignee>().Remove(row);
                _assignees.Remove(row);
            }

            if (rows.Count > 0)
            {
                _changedItems.Add(item.Id);
            }

            return rows.Count > 0;
        }

        public bool IsDone(Item item) =>
            StatusColumn is { } c ? TaskRules.IsDone(item.Values.GetValueOrDefault(c.Id.ToString()))
            : PercentColumn is { } p && (ScheduleEngine.Percent(item.Values.GetValueOrDefault(p.Id.ToString())) ?? 0) >= 100;

        /// <summary>Makes the task done (or not). False when the plan has no Status or % Complete column to say so.</summary>
        public bool SetDone(Item item, bool done)
        {
            if (StatusColumn is { } c)
            {
                var option = done
                    ? c.Options.FirstOrDefault(TaskRules.IsDone)
                    : c.Options.FirstOrDefault(o => o.Contains("progress", StringComparison.OrdinalIgnoreCase)) ?? c.Options.FirstOrDefault(o => !TaskRules.IsDone(o));
                if (option is null && done)
                {
                    return false;
                }

                item.Values = new Dictionary<string, string?>(item.Values) { [c.Id.ToString()] = option };
                return true;
            }

            if (PercentColumn is { } p)
            {
                item.Values = new Dictionary<string, string?>(item.Values) { [p.Id.ToString()] = done ? "100" : "0" };
                return true;
            }

            return false;
        }

        public bool SetEstimate(Item item, decimal? hours)
        {
            if (EstimateColumn is not { } c || hours is null)
            {
                return false;
            }

            var text = hours.Value.ToString("0.##", CultureInfo.InvariantCulture);
            if (item.Values.GetValueOrDefault(c.Id.ToString()) == text)
            {
                return false;
            }

            item.Values = new Dictionary<string, string?>(item.Values) { [c.Id.ToString()] = text };
            return true;
        }

        /// <summary>The task changed in Workbench since the last sync in a way Jira cares about (Done, due date).</summary>
        public bool WorkbenchChanged(Item item, JiraItemLink itemLink) =>
            IsDone(item) != itemLink.BaseWorkbenchDone || item.Finish != itemLink.BaseWorkbenchDue;

        /// <summary>A task added in Workbench after linking, a leaf, with no issue yet, when the link creates issues.</summary>
        public bool WantsIssueFor(Item item) =>
            link.CreateIssuesForNewTasks && !ItemLinks.ContainsKey(item.Id) && !item.IsMilestone && item.CreatedBy != Actor
            && item.CreatedAtUtc is { } created && created >= link.CreatedAtUtc && !string.IsNullOrWhiteSpace(item.Title)
            && !Items.Values.Any(i => i.ParentId == item.Id);

        public void Synced()
        {
            link.LastSyncedAtUtc = Now;
            link.Status = JiraLinkStatus.Active;
            link.LastError = null;
            link.LastErrorHint = null;
        }

        /// <summary>Records a Jira failure on the link (and the task): needs a person for 401/403, else just shown.</summary>
        public void Failed(JiraApiException ex, Guid? itemId)
        {
            link.LastError = JiraRules.Clip(ex.Message, 1000);
            link.LastErrorHint = JiraRules.Clip(ex.Hint, 1000);
            if (ex.StatusCode is 401 or 403)
            {
                link.Status = JiraLinkStatus.NeedsAttention;
            }

            if (itemId is { } id && ItemLinks.GetValueOrDefault(id) is { } itemLink)
            {
                itemLink.LastError = JiraRules.Clip(ex.Message, 1000);
            }
        }

        public void Touch(Item item)
        {
            item.ModifiedAtUtc = Now;
            item.ModifiedBy = Actor;
            _changedItems.Add(item.Id);
        }

        public void Activity(Guid itemId, string kind, string summary) => _activity.Add(new ActivityEvent
        {
            SheetId = Sheet.Id, ItemId = itemId, Actor = Actor, Kind = kind, Summary = JiraRules.Clip(summary, 2000), OccurredAtUtc = Now,
        });

        /// <summary>Shows something on the project's panel. A note open about the same task and field is updated rather than repeated.</summary>
        public void Note(Item item, JiraItemLink? itemLink, string field, string message, string? hint, bool isError)
        {
            var existing = _notes.FirstOrDefault(n => n.ItemId == item.Id && n.Field == field);
            if (existing is null)
            {
                existing = new JiraSyncNote { LinkId = link.Id, ItemId = item.Id, Field = field, Message = "", CreatedAtUtc = Now };
                db.Set<JiraSyncNote>().Add(existing);
                _notes.Add(existing);
            }
            else if (existing.Message == message)
            {
                return;
            }

            existing.IssueKey = itemLink?.IssueKey;
            existing.Message = JiraRules.Clip(message, 1000);
            existing.Hint = hint is null ? null : JiraRules.Clip(hint, 1000);
            existing.IsError = isError;
            existing.UpdatedAtUtc = Now;
            Activity(item.Id, "item.jira", message);
        }

        /// <summary>Closes the open note about a field once the thing it was about is settled (an informational note about a kept date stays).</summary>
        public void ClearNote(Guid itemId, string field, bool keepIfInfo = false)
        {
            foreach (var n in _notes.Where(n => n.ItemId == itemId && n.Field == field && (!keepIfInfo || n.IsError)).ToList())
            {
                n.IsOpen = false;
                n.UpdatedAtUtc = Now;
                _notes.Remove(n);
            }
        }

        public async Task SaveAsync(CancellationToken ct)
        {
            if (_activity.Count > 0)
            {
                db.Set<ActivityEvent>().AddRange(_activity);
            }

            link.UpdatedAtUtc = Now;
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException) when (db is DbContext context && context.ChangeTracker.Entries<JiraItemLink>().Any(e => e.State == EntityState.Added))
            {
                // Another sync linked the same issue or task first (the unique keys): drop this pass's new rows; the next one finds theirs.
                foreach (var entry in context.ChangeTracker.Entries().Where(e => e.State == EntityState.Added).ToList())
                {
                    entry.State = EntityState.Detached;
                }

                _activity.Clear();
                _changedItems.Clear();
                return;
            }

            _activity.Clear();
            if (_changedItems.Count > 0)
            {
                await PlanReflow.ReflowAndSaveAsync(db, Sheet.Id, ct, clock);
                var changed = _changedItems.ToList();
                _changedItems.Clear();

                // Saving stamped the rows: remember the stamp, so the next sync doesn't take its own change for a Workbench edit.
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

/// <summary>Small pure helpers for the Jira sync.</summary>
public static class JiraRules
{
    public static string Clip(string? text, int max)
    {
        text ??= "";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    public static string Date(DateOnly? date) => date is { } d ? d.ToString("MMM d, yyyy", CultureInfo.InvariantCulture) : "none";

    /// <summary>The label that marks the Jira issue made for a task, so a retried create finds it.</summary>
    public static string TaskLabel(Guid itemId) => "workbench-" + itemId.ToString("N");

    public static Guid? TaskIdFromLabel(string label) =>
        label.StartsWith("workbench-", StringComparison.Ordinal) && Guid.TryParseExact(label["workbench-".Length..], "N", out var id) ? id : null;

    public static string StripOrderBy(string jql)
    {
        var at = jql.LastIndexOf("order by", StringComparison.OrdinalIgnoreCase);
        return (at >= 0 ? jql[..at] : jql).Trim();
    }

    public static string Rules(JiraDateWinner winner, bool creates) =>
        $"Jira wins the title and assignee. Dates: {(winner == JiraDateWinner.Workbench ? "Workbench" : "Jira")} wins when both changed. Marking a task Done moves its issue to Done. " +
        (creates ? "New tasks here create Jira issues. " : "") + "Time stays in Workbench.";
}

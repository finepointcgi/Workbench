using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.Identity.Passwords;
using Workbench.Application.Security;
using Workbench.Application.Tasks;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations.Jira;
using Workbench.Contracts.Security;
using Workbench.Domain;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Integrations;
using Workbench.Domain.Integrations.Jira;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Integrations.Jira;

/// <summary>Admin › Integrations › Jira: the site, the account and the API token. Administrators only (the decorator).</summary>
public sealed class JiraConnectionService(
    IWorkbenchDbContextFactory dbFactory,
    JiraClientFactory clients,
    JiraSecrets secrets,
    OutboundUrlGuard guard,
    IOptions<IntegrationOptions> integrationOptions,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    TimeProvider clock) : IJiraConnectionService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<JiraConnectionDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<JiraConnection>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        return ToDto(row, await db.Set<JiraProjectLink>().CountAsync(cancellationToken));
    }

    public async Task<JiraConnectionDto> SaveAsync(SaveJiraConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var (siteUrl, email) = await ValidateAsync(request, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<JiraConnection>().FirstOrDefaultAsync(cancellationToken);
        var token = request.Token?.Trim();
        if (row is null && string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("Paste a Jira API token.");
        }

        if (row is not null && string.IsNullOrEmpty(token) && !string.Equals(row.SiteUrl, siteUrl, StringComparison.OrdinalIgnoreCase) && await db.Set<JiraProjectLink>().AnyAsync(cancellationToken))
        {
            throw new ArgumentException("Projects are linked to the current Jira site. Unlink them before moving to another site, or paste the new site's token.");
        }

        var now = Now;
        if (row is null)
        {
            row = new JiraConnection { SiteUrl = siteUrl, AccountEmail = email, TokenProtected = "", TokenHint = "", CreatedBy = Me, CreatedAtUtc = now };
            db.Set<JiraConnection>().Add(row);
        }

        row.SiteUrl = siteUrl;
        row.AccountEmail = email;
        row.IsActive = request.IsActive;
        if (!string.IsNullOrEmpty(token))
        {
            CheckToken(token);
            row.TokenProtected = secrets.ProtectToken(token);
            row.TokenHint = token[^Math.Min(4, token.Length)..];
        }

        Apply(row, await TestCoreAsync(siteUrl, email, secrets.UnprotectToken(row.TokenProtected), cancellationToken), now);
        row.UpdatedBy = Me;
        row.UpdatedAtUtc = now;
        await JiraOutbox.EnsureConnectorAsync(db, row.IsActive, Me, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"Jira: {new Uri(siteUrl).Host}", detail: string.IsNullOrEmpty(token) ? "Settings changed" : "Token saved", cancellationToken: cancellationToken);
        return ToDto(row, await db.Set<JiraProjectLink>().CountAsync(cancellationToken));
    }

    public async Task<JiraTestResult> TestAsync(SaveJiraConnectionRequest? unsaved = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<JiraConnection>().FirstOrDefaultAsync(cancellationToken);
        if (unsaved is null)
        {
            if (row is null)
            {
                return new JiraTestResult(false, "Jira isn't connected yet.", "Enter your Jira address, the account's email and an API token, then choose Save.", null);
            }

            var saved = await TestCoreAsync(row.SiteUrl, row.AccountEmail, secrets.UnprotectToken(row.TokenProtected), cancellationToken);
            Apply(row, saved, Now);
            await db.SaveChangesAsync(cancellationToken);
            return saved;
        }

        string siteUrl;
        string email;
        try
        {
            (siteUrl, email) = await ValidateAsync(unsaved, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return new JiraTestResult(false, ex.Message, null, null);
        }

        var token = string.IsNullOrWhiteSpace(unsaved.Token) ? row is null ? null : secrets.UnprotectToken(row.TokenProtected) : unsaved.Token.Trim();
        return token is null
            ? new JiraTestResult(false, "Paste an API token to test.", "Create one at id.atlassian.com › Security › API tokens.", null)
            : await TestCoreAsync(siteUrl, email, token, cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var linked = await db.Set<JiraProjectLink>().CountAsync(cancellationToken);
        if (linked > 0)
        {
            throw new ArgumentException($"{linked} project{(linked == 1 ? " is" : "s are")} linked to Jira. Unlink {(linked == 1 ? "it" : "them")} on the project's Setup tab first.");
        }

        var row = await db.Set<JiraConnection>().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return;
        }

        db.Set<JiraConnection>().Remove(row);
        await JiraOutbox.EnsureConnectorAsync(db, false, Me, Now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"Jira: {new Uri(row.SiteUrl).Host}", detail: "Disconnected", cancellationToken: cancellationToken);
    }

    private async Task<(string SiteUrl, string Email)> ValidateAsync(SaveJiraConnectionRequest request, CancellationToken ct)
    {
        var uri = JiraUrls.NormalizeSite(request.SiteUrl, integrationOptions.Value.AllowHttp);
        await guard.ValidateAsync(uri.ToString(), "The Jira address", ct);
        var email = request.AccountEmail?.Trim().ToLowerInvariant() ?? "";
        if (email.Length is 0 or > 320 || !email.Contains('@') || email.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("Enter the email of the Atlassian account the API token belongs to.");
        }

        return (uri.ToString().TrimEnd('/'), email);
    }

    private static void CheckToken(string token)
    {
        if (token.Length is < 10 or > 1000 || token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("That doesn't look like a Jira API token. Copy it again from id.atlassian.com › Security › API tokens.");
        }
    }

    private async Task<JiraTestResult> TestCoreAsync(string siteUrl, string email, string token, CancellationToken ct)
    {
        try
        {
            using var client = clients.Create(siteUrl, email, token);
            var me = await client.GetMyselfAsync(ct);
            await client.SearchProjectsAsync(null, ct); // the account can see projects at all
            return new JiraTestResult(true, $"Connected to {new Uri(siteUrl).Host} as {me.DisplayName}.", null, me.DisplayName);
        }
        catch (JiraApiException ex)
        {
            return new JiraTestResult(false, ex.Message, ex.Hint, null);
        }
    }

    private static void Apply(JiraConnection row, JiraTestResult test, DateTime now)
    {
        row.LastTestedAtUtc = now;
        row.LastTestOk = test.Ok;
        row.LastTestMessage = JiraRules.Clip(test.Hint is null ? test.Message : $"{test.Message} {test.Hint}", 1000);
        if (test.Ok)
        {
            row.ConnectedAsName = test.Name;
        }
    }

    private static JiraConnectionDto ToDto(JiraConnection? row, int linked) =>
        row is null
            ? new JiraConnectionDto(false, null, null, null, false, null, null, null, null, linked, null, null)
            : new JiraConnectionDto(true, row.SiteUrl, row.AccountEmail, row.TokenHint, row.IsActive, row.ConnectedAsName, row.LastTestedAtUtc, row.LastTestOk, row.LastTestMessage,
                linked, row.UpdatedBy ?? row.CreatedBy, row.UpdatedAtUtc);
}

/// <summary>A project's Jira panel: link, settings, sync now, notes. Permissions are the decorator's.</summary>
public sealed class JiraProjectSyncService(
    IWorkbenchDbContextFactory dbFactory,
    JiraClientFactory clients,
    JiraSyncEngine engine,
    JiraSecrets secrets,
    AccountLinks links,
    ProjectAccess projectAccess,
    ICurrentUser currentUser,
    TimeProvider clock) : IJiraProjectSyncService
{
    public static readonly IReadOnlyList<string> HookEvents = ["Issue: created", "Issue: updated", "Issue: deleted"];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<JiraProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var connection = await db.Set<JiraConnection>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var problem = connection is null ? "Jira isn't connected yet. An administrator connects it under Admin › Integrations › Jira."
            : !connection.IsActive ? "The Jira connection is turned off (Admin › Integrations › Jira)."
            : connection.LastTestOk == false ? $"The Jira connection has a problem: {connection.LastTestMessage}"
            : null;
        var canManage = await projectAccess.CanSetUpProjectAsync(projectId, cancellationToken);
        var link = await db.Set<JiraProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ProjectId == projectId, cancellationToken);
        if (link is null)
        {
            return new JiraProjectSyncDto(projectId, problem is null, problem, canManage, null, [], [], [], 0, 0);
        }

        var sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.SheetId).FirstOrDefaultAsync(cancellationToken) ?? link.SheetId;
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).Select(i => new { i.Id, i.Title, i.CostpointWbsId }).ToDictionaryAsync(i => i.Id, cancellationToken);
        var itemLinks = await db.Set<JiraItemLink>().AsNoTracking().Where(l => l.LinkId == link.Id).ToListAsync(cancellationToken);
        var tasks = itemLinks
            .Select(l => new JiraTaskLinkDto(l.ItemId, items.GetValueOrDefault(l.ItemId)?.Title ?? "(deleted task)", items.GetValueOrDefault(l.ItemId)?.CostpointWbsId,
                l.IssueKey, l.WebUrl, l.JiraStatus, l.EstimateHours, l.LastSyncedAtUtc, l.LastError, l.IsRemoved))
            .OrderBy(t => t.IsRemoved).ThenBy(t => t.WbsCode, StringComparer.Ordinal).ThenBy(t => t.IssueKey, StringComparer.Ordinal).ToList();
        var notes = (await db.Set<JiraSyncNote>().AsNoTracking().Where(n => n.LinkId == link.Id && n.IsOpen).ToListAsync(cancellationToken))
            .OrderByDescending(n => n.IsError).ThenByDescending(n => n.UpdatedAtUtc)
            .Select(n => new JiraNoteDto(n.Id, n.ItemId, n.ItemId is { } id ? items.GetValueOrDefault(id)?.Title : null, n.IssueKey, n.Field, n.Message, n.Hint, n.IsError, n.CreatedAtUtc)).ToList();
        var recent = (await db.Set<ActivityEvent>().AsNoTracking().Where(a => a.SheetId == sheetId && a.Actor == JiraSyncEngine.Actor).OrderByDescending(a => a.OccurredAtUtc).Take(25).ToListAsync(cancellationToken))
            .Select(a => new JiraActivityDto(a.ItemId, a.Summary, a.Actor, a.OccurredAtUtc)).ToList();
        var (waiting, failed) = await DeliveriesAsync(db, link.Id, cancellationToken);
        var lastSynced = link.LastSyncedAtUtc ?? itemLinks.Max(l => l.LastSyncedAtUtc);
        var state = link.Status == JiraLinkStatus.NeedsAttention ? "Needs attention"
            : link.LastError is not null ? "Jira isn't answering"
            : waiting > 0 ? "Sending changes"
            : failed > 0 ? "Some changes failed"
            : lastSynced is null ? "Waiting for the first sync"
            : "Up to date";
        var dto = new JiraLinkDto(link.Id, link.JiraProjectKey, link.JiraProjectName, link.JiraProjectUrl, link.EpicKey, link.Jql, ToChoice(link.DateWinner), link.CreateIssuesForNewTasks,
            link.IssueType, state, JiraRules.Rules(link.DateWinner, link.CreateIssuesForNewTasks), lastSynced, link.LastError, link.LastErrorHint, link.CreatedAtUtc, link.CreatedBy);
        return new JiraProjectSyncDto(projectId, problem is null, problem, canManage, dto, tasks, notes, recent, waiting, failed);
    }

    public async Task<IReadOnlyList<JiraProjectOptionDto>> SearchJiraProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default)
    {
        using var client = await ClientAsync(cancellationToken);
        var text = query?.Trim() ?? "";
        if (text.Length > 0 && (text.Contains("://") || text.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && text.Length <= 10 && text.ToUpperInvariant() == text))
        {
            try
            {
                if (await client.GetProjectAsync(JiraUrls.ProjectKey(text), cancellationToken) is { } exact)
                {
                    return [ToOption(client, exact)];
                }
            }
            catch (ArgumentException)
            {
                // not a key: search by name below
            }
        }

        return (await client.SearchProjectsAsync(text, cancellationToken)).Select(p => ToOption(client, p)).ToList();
    }

    public async Task<JiraProjectSyncDto> LinkAsync(Guid projectId, LinkJiraProjectRequest request, CancellationToken cancellationToken = default)
    {
        using (var client = await ClientAsync(cancellationToken))
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", projectId);
            if (project.SheetId is not { } sheetId)
            {
                throw new ArgumentException("This project has no plan yet. Start the plan on its Plan tab, then link it to Jira.");
            }

            if (await db.Set<JiraProjectLink>().AnyAsync(l => l.ProjectId == projectId, cancellationToken))
            {
                throw new ArgumentException("This project is already linked to Jira. Unlink it first to link another Jira project.");
            }

            var key = JiraUrls.ProjectKey(request.Project);
            var epic = string.IsNullOrWhiteSpace(request.EpicKey) ? null : request.EpicKey.Trim().ToUpperInvariant();
            if (epic is not null && (!JiraUrls.IsIssueKey(epic) || !epic.StartsWith(key + "-", StringComparison.Ordinal)))
            {
                throw new ArgumentException($"The epic must be an issue key in {key}, such as {key}-12.");
            }

            var jql = string.IsNullOrWhiteSpace(request.Jql) ? null : JiraRules.StripOrderBy(request.Jql);
            if (jql is { Length: > 2000 })
            {
                throw new ArgumentException("The JQL filter is too long (2,000 characters at most).");
            }

            var jira = await client.GetProjectAsync(key, cancellationToken) ?? throw new ArgumentException($"Jira has no project {key} that this account can see.");
            if (await db.Set<JiraProjectLink>().AnyAsync(l => l.JiraProjectKey == jira.Key && l.EpicKey == epic && l.Jql == jql, cancellationToken))
            {
                throw new ArgumentException($"{jira.Key} is already linked to another Workbench project with the same filter. Each set of issues syncs with one plan, or the same issue would be a task in two plans.");
            }

            if (jql is not null)
            {
                // Fail now, with Jira's own words, rather than at the first sync.
                await client.SearchIssuesAsync($"project = {JiraUrls.Quote(jira.Key)} AND ({jql})", 1, cancellationToken);
            }

            var now = Now;
            var link = new JiraProjectLink
            {
                ProjectId = projectId, SheetId = sheetId, JiraProjectKey = jira.Key, JiraProjectName = jira.Name, JiraProjectUrl = $"{client.SiteUrl}/browse/{Uri.EscapeDataString(jira.Key)}",
                EpicKey = epic, Jql = jql, DateWinner = request.DateWinner == JiraDateWinnerChoice.Jira ? JiraDateWinner.Jira : JiraDateWinner.Workbench,
                CreateIssuesForNewTasks = request.CreateIssuesForNewTasks, IssueType = string.IsNullOrWhiteSpace(request.IssueType) ? "Task" : JiraRules.Clip(request.IssueType.Trim(), 64),
                WebhookSecretProtected = secrets.ProtectWebhookSecret(JiraSecrets.NewWebhookSecret()), CreatedBy = Me, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            db.Set<JiraProjectLink>().Add(link);
            var added = await EnsureStatusColumnAsync(db, sheetId, cancellationToken);
            db.Set<ActivityEvent>().Add(new ActivityEvent
            {
                SheetId = sheetId, Actor = Me, Kind = "item.jira", OccurredAtUtc = now,
                Summary = $"Linked the plan to Jira project {jira.Key}{(epic is null ? "" : $" (epic {epic})")}" + (added ? "; added a Status column" : ""),
            });
            JiraOutbox.RaiseFullSync(db, link.Id, Me, now);
            await JiraOutbox.EnsureConnectorAsync(db, true, Me, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            JiraChangeListener.Invalidate();
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task<JiraProjectSyncDto> UpdateLinkAsync(Guid projectId, UpdateJiraLinkRequest request, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var link = await db.Set<JiraProjectLink>().FirstOrDefaultAsync(l => l.ProjectId == projectId, cancellationToken) ?? throw new ArgumentException("This project isn't linked to Jira.");
            link.DateWinner = request.DateWinner == JiraDateWinnerChoice.Jira ? JiraDateWinner.Jira : JiraDateWinner.Workbench;
            link.CreateIssuesForNewTasks = request.CreateIssuesForNewTasks;
            link.IssueType = string.IsNullOrWhiteSpace(request.IssueType) ? "Task" : JiraRules.Clip(request.IssueType.Trim(), 64);
            link.UpdatedBy = Me;
            link.UpdatedAtUtc = Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var link = await db.Set<JiraProjectLink>().FirstOrDefaultAsync(l => l.ProjectId == projectId, cancellationToken) ?? throw new ArgumentException("This project isn't linked to Jira.");
        db.Set<JiraItemLink>().RemoveRange(await db.Set<JiraItemLink>().Where(l => l.LinkId == link.Id).ToListAsync(cancellationToken));
        db.Set<JiraSyncNote>().RemoveRange(await db.Set<JiraSyncNote>().Where(n => n.LinkId == link.Id).ToListAsync(cancellationToken));
        db.Set<JiraWebhookReceipt>().RemoveRange(await db.Set<JiraWebhookReceipt>().Where(r => r.LinkId == link.Id).ToListAsync(cancellationToken));
        db.Set<JiraProjectLink>().Remove(link);
        db.Set<ActivityEvent>().Add(new ActivityEvent
        {
            SheetId = link.SheetId, Actor = Me, Kind = "item.jira", OccurredAtUtc = Now,
            Summary = $"Unlinked the plan from Jira project {link.JiraProjectKey}. The issues stay in Jira and the tasks stay here.",
        });
        await db.SaveChangesAsync(cancellationToken);
        JiraChangeListener.Invalidate();
    }

    public async Task<JiraSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var link = await LinkRowAsync(projectId, cancellationToken);
        var report = await engine.ReconcileAsync(link.Id, everything: true, cancellationToken);
        var message = report.Error is { } error ? $"Jira couldn't be reached or refused: {error}"
            : report.Synced + report.Created == 0 ? "Everything was already up to date."
            : string.Join(", ", new[]
            {
                report.Created > 0 ? $"{report.Created} added" : null,
                report.Synced > 0 ? $"{report.Synced} updated" : null,
                report.Failed > 0 ? $"{report.Failed} failed" : null,
            }.Where(s => s is not null)) + ".";
        return new JiraSyncNowResult(report.Synced, report.Created, report.Failed, message);
    }

    public async Task<JiraWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var link = await LinkRowAsync(projectId, cancellationToken);
        return new JiraWebhookSetupDto(links.Absolute($"/webhooks/jira/{link.Id}"), secrets.UnprotectWebhookSecret(link.WebhookSecretProtected), HookEvents,
            "Jira Cloud: Settings › System › WebHooks › Create a WebHook; paste the URL and the secret, tick the three issue events, and add the JQL \"project = "
            + link.JiraProjectKey + "\". Or use an automation rule's \"Send web request\" with the header X-Workbench-Token set to the secret. Without a webhook, Workbench still polls every few minutes.");
    }

    public async Task<JiraProjectSyncDto> DismissNoteAsync(Guid projectId, Guid noteId, CancellationToken cancellationToken = default)
    {
        var link = await LinkRowAsync(projectId, cancellationToken);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var note = await db.Set<JiraSyncNote>().FirstOrDefaultAsync(n => n.Id == noteId && n.LinkId == link.Id, cancellationToken) ?? throw WorkbenchNotFoundException.For("Sync note", noteId);
            note.IsOpen = false;
            note.UpdatedAtUtc = Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task<JiraTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var link = await db.Set<JiraItemLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ItemId == itemId, cancellationToken);
        return link is null ? null : new JiraTaskStatusDto(link.IssueKey, link.WebUrl, link.JiraStatus, link.LastSyncedAtUtc, link.LastError, link.IsRemoved);
    }

    /// <summary>The plan needs a way to say Done: adds a Status column when it has neither Status nor % Complete.</summary>
    private static async Task<bool> EnsureStatusColumnAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken ct)
    {
        var columns = await db.Columns.Where(c => c.SheetId == sheetId).ToListAsync(ct);
        if (TaskRules.StatusColumn(columns) is not null || TaskRules.PercentColumn(columns) is not null)
        {
            return false;
        }

        db.Columns.Add(new Column
        {
            SheetId = sheetId, Name = TaskRules.StatusColumnName, Type = ColumnType.Dropdown, Order = columns.Select(c => c.Order).DefaultIfEmpty(-1).Max() + 1,
            Options = ["Not started", "In progress", "Done"],
        });
        return true;
    }

    private static async Task<(int Waiting, int Failed)> DeliveriesAsync(IWorkbenchDbContext db, Guid linkId, CancellationToken ct)
    {
        var connectorId = await db.Set<IntegrationConnector>().AsNoTracking().Where(c => c.Kind == JiraOutbox.ConnectorKind).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var key = linkId.ToString();
        var undispatched = await db.Set<IntegrationOutboxEvent>().AsNoTracking().CountAsync(e => e.DispatchedAtUtc == null && e.EventType.StartsWith("jira.") && e.PayloadJson.Contains(key), ct);
        if (connectorId is null)
        {
            return (undispatched, 0);
        }

        var rows = await db.Set<IntegrationDelivery>().AsNoTracking()
            .Where(d => d.ConnectorId == connectorId && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed))
            .Join(db.Set<IntegrationOutboxEvent>().Where(e => e.PayloadJson.Contains(key)), d => d.EventId, e => e.Id, (d, e) => d.Status)
            .ToListAsync(ct);
        return (undispatched + rows.Count(s => s == DeliveryStatus.Pending), rows.Count(s => s == DeliveryStatus.Failed));
    }

    private async Task<JiraProjectLink> LinkRowAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<JiraProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ProjectId == projectId, ct) ?? throw new ArgumentException("This project isn't linked to Jira.");
    }

    private async Task<IJiraClient> ClientAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<JiraConnection>().AsNoTracking().FirstOrDefaultAsync(ct) ?? throw new ArgumentException("Connect Jira first (Admin › Integrations › Jira).");
        return clients.For(row);
    }

    private static JiraDateWinnerChoice ToChoice(JiraDateWinner w) => w == JiraDateWinner.Jira ? JiraDateWinnerChoice.Jira : JiraDateWinnerChoice.Workbench;

    private static JiraProjectOptionDto ToOption(IJiraClient client, JiraProjectInfo p) => new(p.Key, p.Name, $"{client.SiteUrl}/browse/{Uri.EscapeDataString(p.Key)}");
}

/// <summary>Admin › Integrations › Jira is for administrators (Manage integrations); API keys never manage it.</summary>
public sealed class PermissionCheckingJiraConnectionService(IJiraConnectionService inner, AccessControl access) : IJiraConnectionService
{
    public async Task<JiraConnectionDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetAsync(cancellationToken);
    }

    public async Task<JiraConnectionDto> SaveAsync(SaveJiraConnectionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }

    public async Task<JiraTestResult> TestAsync(SaveJiraConnectionRequest? unsaved = null, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.TestAsync(unsaved, cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await inner.DeleteAsync(cancellationToken);
    }
}

/// <summary>
/// A project's Jira panel: anyone who can see the project's plan reads it; linking, changing the rules, unlinking, syncing and
/// closing notes is project setup (Finance, administrators, the project's manager with the Project manager role), the same as GitLab.
/// API keys are refused (the endpoints live under <c>/integrations</c>, which no key scope covers).
/// </summary>
public sealed class PermissionCheckingJiraProjectSyncService(IJiraProjectSyncService inner, ProjectAccess projects, AccessControl access, IWorkbenchDbContextFactory dbFactory) : IJiraProjectSyncService
{
    public async Task<JiraProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireReadAsync(projectId, cancellationToken);
        return await inner.GetAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<JiraProjectOptionDto>> SearchJiraProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "link the project to Jira", cancellationToken);
        return await inner.SearchJiraProjectsAsync(projectId, query, cancellationToken);
    }

    public async Task<JiraProjectSyncDto> LinkAsync(Guid projectId, LinkJiraProjectRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "link the project to Jira", cancellationToken);
        return await inner.LinkAsync(projectId, request, cancellationToken);
    }

    public async Task<JiraProjectSyncDto> UpdateLinkAsync(Guid projectId, UpdateJiraLinkRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "change the Jira sync rules", cancellationToken);
        return await inner.UpdateLinkAsync(projectId, request, cancellationToken);
    }

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "unlink the project from Jira", cancellationToken);
        await inner.UnlinkAsync(projectId, cancellationToken);
    }

    public async Task<JiraSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "sync the project with Jira", cancellationToken);
        return await inner.SyncNowAsync(projectId, cancellationToken);
    }

    public async Task<JiraWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "see the Jira webhook's secret", cancellationToken);
        return await inner.GetWebhookSetupAsync(projectId, cancellationToken);
    }

    public async Task<JiraProjectSyncDto> DismissNoteAsync(Guid projectId, Guid noteId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "close Jira sync notes", cancellationToken);
        return await inner.DismissNoteAsync(projectId, noteId, cancellationToken);
    }

    public async Task<JiraTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        NoApiKeys();
        await access.RequireSheetRoleAsync(sheetId, Contracts.Identity.WorkspaceRole.Viewer, cancellationToken);
        return await inner.GetTaskStatusAsync(sheetId, itemId, cancellationToken);
    }

    private async Task RequireReadAsync(Guid projectId, CancellationToken ct)
    {
        NoApiKeys();
        await projects.Visibility.RequireVisibleAsync(projectId, ct);
        if (await projects.CanSetUpProjectAsync(projectId, ct))
        {
            return;
        }

        Guid? sheetId;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.SheetId).FirstOrDefaultAsync(ct);
        }

        if (sheetId is not { } id || await access.GetSheetRoleAsync(id, ct) is null)
        {
            throw await access.DenyAsync("You need access to the project's plan to see its Jira sync.", ct);
        }
    }

    private async Task RequireSetupAsync(Guid projectId, string what, CancellationToken ct)
    {
        NoApiKeys();
        await projects.RequireProjectSetupAsync(projectId, what, ct);
    }

    private void NoApiKeys()
    {
        access.RequireSignedIn();
        if (ApiKeyPrincipal.IsServiceAccount(access.Email))
        {
            throw new WorkbenchForbiddenException("API keys can't use the Jira sync.");
        }
    }
}

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.Identity.Passwords;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations.GitLab;
using Workbench.Contracts.Security;
using Workbench.Domain.Collaboration;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;
using Workbench.Domain.Integrations.GitLab;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>Admin › Integrations › GitLab: the server, the token and people matches. Administrators only (the decorator).</summary>
public sealed class GitLabConnectionService(
    IWorkbenchDbContextFactory dbFactory,
    GitLabClientFactory clients,
    GitLabSecrets secrets,
    GitLabPeople people,
    OutboundUrlGuard guard,
    IOptions<GitLabOptions> options,
    IOptions<IntegrationOptions> integrationOptions,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    TimeProvider clock) : IGitLabConnectionService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<GitLabConnectionDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var linked = await db.Set<GitLabProjectLink>().CountAsync(cancellationToken);
        return ToDto(row, linked);
    }

    public async Task<GitLabConnectionDto> SaveAsync(SaveGitLabConnectionRequest request, CancellationToken cancellationToken = default)
    {
        var (baseUrl, caPem) = await ValidateAsync(request, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<GitLabConnection>().FirstOrDefaultAsync(cancellationToken);
        var token = request.Token?.Trim();
        if (row is null && string.IsNullOrEmpty(token))
        {
            throw new ArgumentException("Paste an access token with the api scope.");
        }

        if (row is not null && !string.Equals(row.BaseUrl, baseUrl, StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(token)
            && await db.Set<GitLabProjectLink>().AnyAsync(cancellationToken))
        {
            throw new ArgumentException("Projects are linked to the current GitLab server. Unlink them before moving to another server, or paste the new server's token.");
        }

        var now = Now;
        if (row is null)
        {
            row = new GitLabConnection { BaseUrl = baseUrl, TokenProtected = "", TokenHint = "", CreatedBy = Me, CreatedAtUtc = now };
            db.Set<GitLabConnection>().Add(row);
        }

        row.BaseUrl = baseUrl;
        row.CaCertificatePem = caPem;
        row.IsActive = request.IsActive;
        if (!string.IsNullOrEmpty(token))
        {
            CheckToken(token);
            row.TokenProtected = secrets.ProtectToken(token);
            row.TokenHint = token[^Math.Min(4, token.Length)..];
        }

        var test = await TestCoreAsync(baseUrl, secrets.UnprotectToken(row.TokenProtected), caPem, cancellationToken);
        Apply(row, test, now);
        row.UpdatedBy = Me;
        row.UpdatedAtUtc = now;
        await GitLabOutbox.EnsureConnectorAsync(db, row.IsActive, Me, now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"GitLab: {new Uri(baseUrl).Host}",
            detail: string.IsNullOrEmpty(token) ? "Settings changed" : "Token saved", cancellationToken: cancellationToken);
        return ToDto(row, await db.Set<GitLabProjectLink>().CountAsync(cancellationToken));
    }

    public async Task<GitLabTestResult> TestAsync(SaveGitLabConnectionRequest? unsaved = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<GitLabConnection>().FirstOrDefaultAsync(cancellationToken);
        if (unsaved is null)
        {
            if (row is null)
            {
                return new GitLabTestResult(false, "GitLab isn't connected yet.", "Enter your GitLab address and an access token, then choose Save.", null, null, null, null);
            }

            var saved = await TestCoreAsync(row.BaseUrl, secrets.UnprotectToken(row.TokenProtected), row.CaCertificatePem, cancellationToken);
            Apply(row, saved, Now);
            await db.SaveChangesAsync(cancellationToken);
            return saved;
        }

        string baseUrl;
        string? caPem;
        try
        {
            (baseUrl, caPem) = await ValidateAsync(unsaved, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            return new GitLabTestResult(false, ex.Message, null, null, null, null, null);
        }

        var token = string.IsNullOrWhiteSpace(unsaved.Token) ? row is null ? null : secrets.UnprotectToken(row.TokenProtected) : unsaved.Token.Trim();
        if (token is null)
        {
            return new GitLabTestResult(false, "Paste an access token to test.", "Create one in GitLab under your avatar › Preferences › Access tokens, with the api scope.", null, null, null, null);
        }

        return await TestCoreAsync(baseUrl, token, caPem, cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var linked = await db.Set<GitLabProjectLink>().CountAsync(cancellationToken);
        if (linked > 0)
        {
            throw new ArgumentException($"{linked} project{(linked == 1 ? " is" : "s are")} linked to GitLab. Unlink {(linked == 1 ? "it" : "them")} on the project's Setup tab first.");
        }

        var row = await db.Set<GitLabConnection>().FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return;
        }

        db.Set<GitLabConnection>().Remove(row);
        await GitLabOutbox.EnsureConnectorAsync(db, false, Me, Now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"GitLab: {new Uri(row.BaseUrl).Host}", detail: "Disconnected", cancellationToken: cancellationToken);
    }

    public Task<IReadOnlyList<GitLabUserMapDto>> ListPeopleAsync(CancellationToken cancellationToken = default) => GitLabPeopleAdmin.ListAsync(dbFactory, null, cancellationToken);

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        using var client = await ClientAsync(cancellationToken);
        return (await client.SearchUsersAsync(query, cancellationToken)).Select(u => new GitLabUserOptionDto(u.Id, u.Username, u.Name)).ToList();
    }

    public async Task<GitLabUserMapDto> MatchPersonAsync(MatchGitLabPersonRequest request, CancellationToken cancellationToken = default)
    {
        using var client = await ClientAsync(cancellationToken);
        var map = await GitLabPeopleAdmin.MatchAsync(dbFactory, client, request, Me, clock, cancellationToken);
        people.Forget();
        return map;
    }

    public async Task UnmatchPersonAsync(string email, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var normalized = email.Trim().ToLowerInvariant();
        var row = await db.Set<GitLabUserMap>().FirstOrDefaultAsync(m => m.Email == normalized, cancellationToken);
        if (row is not null)
        {
            db.Set<GitLabUserMap>().Remove(row);
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<GitLabClient> ClientAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(ct)
            ?? throw new ArgumentException("Connect GitLab first (Admin › Integrations › GitLab).");
        return clients.For(row);
    }

    private async Task<(string BaseUrl, string? CaPem)> ValidateAsync(SaveGitLabConnectionRequest request, CancellationToken ct)
    {
        var uri = GitLabUrls.NormalizeBase(request.BaseUrl, integrationOptions.Value.AllowHttp);
        if (!options.Value.IsPrivateHostAllowed(uri.Host))
        {
            try
            {
                await guard.ValidateAsync(uri.ToString(), "The GitLab address", ct);
            }
            catch (ArgumentException ex) when (ex.Message.Contains("private", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("internal", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"{ex.Message} For a self-managed GitLab on your own network, ask your Workbench server operator to add {uri.Host} to GitLab:AllowedPrivateHosts.");
            }
        }

        var caPem = string.IsNullOrWhiteSpace(request.CaCertificatePem) ? null : request.CaCertificatePem.Trim();
        if (caPem is not null)
        {
            if (caPem.Length > 64000)
            {
                throw new ArgumentException("The certificate authority is too long (64 KB at most).");
            }

            GitLabTls.ParseCa(caPem);
        }

        return (uri.ToString().TrimEnd('/'), caPem);
    }

    private static void CheckToken(string token)
    {
        if (token.Length is < 10 or > 255 || token.Any(char.IsWhiteSpace))
        {
            throw new ArgumentException("That doesn't look like a GitLab access token. Copy it again from GitLab (it often starts with glpat-).");
        }
    }

    private async Task<GitLabTestResult> TestCoreAsync(string baseUrl, string token, string? caPem, CancellationToken ct)
    {
        try
        {
            using var client = clients.Create(baseUrl, token, caPem);
            var version = await client.GetVersionAsync(ct);
            var me = await client.GetCurrentUserAsync(ct);
            var info = await client.GetTokenInfoAsync(ct);
            if (info?.Scopes is { Count: > 0 } scopes && !scopes.Contains("api", StringComparer.OrdinalIgnoreCase))
            {
                return new GitLabTestResult(false, $"The token works but has only the {string.Join(", ", scopes)} scope{(scopes.Count == 1 ? "" : "s")}.",
                    "The sync writes issues, epics and milestones, so the token needs the api scope. Create a new token with it.", me.Username, me.Name, version.Version, null);
            }

            if (info?.ExpiresAt is { } expires && expires <= DateOnly.FromDateTime(Now.AddDays(14)))
            {
                return new GitLabTestResult(true, $"Connected as @{me.Username} (GitLab {version.Version}). The token expires on {expires:MMM d, yyyy}.",
                    "Create a new token before then, or the sync stops.", me.Username, me.Name, version.Version, version.Enterprise);
            }

            return new GitLabTestResult(true, $"Connected as @{me.Username} (GitLab {version.Version}).", null, me.Username, me.Name, version.Version, version.Enterprise);
        }
        catch (GitLabApiException ex)
        {
            return new GitLabTestResult(false, ex.Message, ex.Hint, null, null, null, null);
        }
        catch (ArgumentException ex)
        {
            return new GitLabTestResult(false, ex.Message, null, null, null, null, null);
        }
    }

    private static void Apply(GitLabConnection row, GitLabTestResult test, DateTime now)
    {
        row.LastTestedAtUtc = now;
        row.LastTestOk = test.Ok;
        row.LastTestMessage = GitLabRules.Clip(test.Hint is null ? test.Message : $"{test.Message} {test.Hint}", 1000);
        if (test.Ok)
        {
            row.ConnectedAsUsername = test.Username;
            row.ConnectedAsName = test.Name;
            row.ServerVersion = test.Version;
        }
    }

    private static GitLabConnectionDto ToDto(GitLabConnection? row, int linked)
    {
        if (row is null)
        {
            return new GitLabConnectionDto(false, null, false, null, false, null, false, null, null, null, null, null, null, linked, null, null);
        }

        string? ca = null;
        if (row.CaCertificatePem is { } pem)
        {
            try
            {
                ca = GitLabTls.Describe(pem);
            }
            catch (ArgumentException ex)
            {
                ca = ex.Message;
            }
        }

        return new GitLabConnectionDto(true, row.BaseUrl, GitLabUrls.IsGitLabCom(row.BaseUrl), row.TokenHint, row.CaCertificatePem is not null, ca, row.IsActive,
            row.ConnectedAsUsername, row.ConnectedAsName, row.ServerVersion, row.LastTestedAtUtc, row.LastTestOk, row.LastTestMessage, linked, row.UpdatedBy ?? row.CreatedBy,
            row.UpdatedAtUtc);
    }
}

/// <summary>People matches, shared by the admin screen and a project's "People to match".</summary>
internal static class GitLabPeopleAdmin
{
    public static async Task<IReadOnlyList<GitLabUserMapDto>> ListAsync(IWorkbenchDbContextFactory dbFactory, IReadOnlySet<string>? emails, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var maps = await db.Set<GitLabUserMap>().AsNoTracking().OrderBy(m => m.Email).ToListAsync(ct);
        var names = await NamesAsync(db, ct);
        return maps.Where(m => emails is null || emails.Contains(m.Email))
            .Select(m => new GitLabUserMapDto(m.Email, names.GetValueOrDefault(m.Email), m.GitLabUserId, m.GitLabUsername, m.GitLabName, m.MatchedBy)).ToList();
    }

    public static async Task<Dictionary<string, string>> NamesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in await db.Set<Employee>().AsNoTracking().Select(e => new { e.Email, e.DisplayName }).ToListAsync(ct))
        {
            names.TryAdd(e.Email, e.DisplayName);
        }

        foreach (var u in await db.Set<AppUser>().AsNoTracking().Select(u => new { u.Email, u.DisplayName }).ToListAsync(ct))
        {
            names.TryAdd(u.Email, u.DisplayName);
        }

        return names;
    }

    public static async Task<GitLabUserMapDto> MatchAsync(
        IWorkbenchDbContextFactory dbFactory, GitLabClient client, MatchGitLabPersonRequest request, string me, TimeProvider clock, CancellationToken ct)
    {
        var email = request.Email?.Trim().ToLowerInvariant() ?? "";
        await using var db = await dbFactory.CreateAsync(ct);
        var names = await NamesAsync(db, ct);
        if (!names.ContainsKey(email))
        {
            throw new ArgumentException($"{email} isn't a person in Workbench.");
        }

        var user = await client.GetUserAsync(request.GitLabUserId, ct);
        var old = await db.Set<GitLabUserMap>().Where(m => m.Email == email || m.GitLabUserId == user.Id).ToListAsync(ct);
        db.Set<GitLabUserMap>().RemoveRange(old);
        await db.SaveChangesAsync(ct);
        var row = new GitLabUserMap
        {
            Email = email, GitLabUserId = user.Id, GitLabUsername = user.Username, GitLabName = user.Name, MatchedBy = "manual",
            CreatedBy = me, CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.Set<GitLabUserMap>().Add(row);

        // Notes about this person are settled now.
        foreach (var note in await db.Set<GitLabSyncConflict>()
                     .Where(c => c.Kind == GitLabConflictKind.PersonNotMatched && c.Status == GitLabConflictStatus.Open && c.GitLabValue == "@" + user.Username).ToListAsync(ct))
        {
            note.Status = GitLabConflictStatus.Resolved;
            note.ResolvedBy = me;
            note.ResolvedAtUtc = clock.GetUtcNow().UtcDateTime;
            note.Resolution = $"Matched to {email}";
        }

        await db.SaveChangesAsync(ct);
        return new GitLabUserMapDto(email, names.GetValueOrDefault(email), user.Id, user.Username, user.Name, row.MatchedBy);
    }
}

/// <summary>A project's GitLab link, sync panel and decisions. The decorator checks who may read and change it.</summary>
public sealed class GitLabProjectSyncService(
    IWorkbenchDbContextFactory dbFactory,
    GitLabClientFactory clients,
    GitLabSyncEngine engine,
    GitLabSecrets secrets,
    GitLabPeople people,
    AccountLinks links,
    ProjectAccess projectAccess,
    IOptions<IntegrationOptions> integrationOptions,
    ICurrentUser currentUser,
    TimeProvider clock) : IGitLabProjectSyncService
{
    public static readonly IReadOnlyList<string> ProjectHookEvents = ["Issues events", "Confidential issues events", "Milestone events"];

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<GitLabProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var connection = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(cancellationToken);
        var problem = connection is null ? "GitLab isn't connected yet. An administrator connects it under Admin › Integrations › GitLab."
            : !connection.IsActive ? "The GitLab connection is turned off (Admin › Integrations › GitLab)."
            : connection.LastTestOk == false ? $"The GitLab connection has a problem: {connection.LastTestMessage}"
            : null;
        var canManage = await projectAccess.CanSetUpProjectAsync(projectId, cancellationToken);
        var link = await db.Set<GitLabProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ProjectId == projectId, cancellationToken);
        if (link is null)
        {
            return new GitLabProjectSyncDto(projectId, problem is null, problem, canManage, null, [], [], [], [], [], 0, 0);
        }

        var sheetId = await db.Set<Project>().AsNoTracking().Where(p => p.Id == projectId).Select(p => p.SheetId).FirstOrDefaultAsync(cancellationToken) ?? link.SheetId;
        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).Select(i => new { i.Id, i.Title, i.CostpointWbsId, i.ParentId, i.Order }).ToListAsync(cancellationToken);
        var titles = items.ToDictionary(i => i.Id);
        var itemLinks = await db.Set<GitLabItemLink>().AsNoTracking().Where(l => l.LinkId == link.Id).ToListAsync(cancellationToken);
        var tasks = itemLinks
            .Select(l => new GitLabTaskLinkDto(l.ItemId, titles.GetValueOrDefault(l.ItemId)?.Title ?? "(deleted task)", titles.GetValueOrDefault(l.ItemId)?.CostpointWbsId,
                l.Kind.ToString().ToLowerInvariant(), l.Iid, GitLabRules.Label(l.Kind, l.Iid), l.WebUrl, l.LastSyncedAtUtc, l.LastError, l.IsRemoved))
            .OrderBy(t => t.IsRemoved).ThenBy(t => t.Kind == "issue").ThenBy(t => t.WbsCode, StringComparer.Ordinal).ThenBy(t => t.Title, StringComparer.Ordinal)
            .ToList();
        var conflicts = (await db.Set<GitLabSyncConflict>().AsNoTracking().Where(c => c.LinkId == link.Id && c.Status == GitLabConflictStatus.Open)
                .ToListAsync(cancellationToken))
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Select(c => ToDto(c, c.ItemId is { } id ? titles.GetValueOrDefault(id)?.Title : null, itemLinks.FirstOrDefault(l => l.ItemId == c.ItemId)))
            .ToList();
        var pending = (await db.Set<GitLabPendingIssue>().AsNoTracking().Where(p => p.LinkId == link.Id && p.Status == GitLabPendingStatus.Pending)
                .ToListAsync(cancellationToken)).OrderBy(p => p.SeenAtUtc)
            .Select(p => new GitLabPendingIssueDto(p.Id, p.Iid, p.Title, p.WebUrl, p.PhaseItemId, p.PhaseItemId is { } ph ? titles.GetValueOrDefault(ph)?.Title : null, p.Author, p.SeenAtUtc))
            .ToList();
        var recent = (await db.Set<ActivityEvent>().AsNoTracking().Where(a => a.SheetId == sheetId && a.Actor == GitLabSyncEngine.Actor)
                .OrderByDescending(a => a.OccurredAtUtc).Take(25).ToListAsync(cancellationToken))
            .Select(a => new GitLabActivityDto(a.ItemId, a.Summary, a.Actor, a.OccurredAtUtc)).ToList();
        var toMatch = await PeopleToMatchAsync(db, link, sheetId, itemLinks, conflicts, cancellationToken);
        var (waiting, failed) = await DeliveriesAsync(db, link.Id, cancellationToken);
        var lastSynced = link.LastSyncedAtUtc ?? itemLinks.Max(l => l.LastSyncedAtUtc);
        var state = link.Status == GitLabLinkStatus.NeedsAttention ? "Needs attention"
            : waiting > 0 ? "Sending changes"
            : link.LastError is not null || failed > 0 ? "Some changes failed"
            : lastSynced is null ? "Waiting for the first sync"
            : "Up to date";
        var dto = new GitLabLinkDto(link.Id, link.GitLabProjectId, link.GitLabProjectPath, link.GitLabProjectUrl, link.GitLabGroupPath,
            link.PhaseMode == GitLabPhaseMode.Epics ? "epics" : "milestones", link.PhaseLevels, state, lastSynced,
            link.LastError, link.LastErrorHint, link.HookId is not null, link.CreatedAtUtc, link.CreatedBy);
        return new GitLabProjectSyncDto(projectId, problem is null, problem, canManage, dto, tasks, conflicts, pending, toMatch, recent, waiting, failed);
    }

    public async Task<IReadOnlyList<GitLabProjectOptionDto>> SearchGitLabProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default)
    {
        var (client, connection) = await ClientAsync(cancellationToken);
        using (client)
        {
            var text = query?.Trim() ?? "";
            if (text.Contains('/') || long.TryParse(text, out _))
            {
                try
                {
                    return [ToOption(await client.GetProjectAsync(GitLabUrls.ProjectRef(connection.BaseUrl, text), cancellationToken))];
                }
                catch (GitLabApiException ex) when (ex.StatusCode == 404)
                {
                    return [];
                }
            }

            return (await client.SearchProjectsAsync(text, cancellationToken)).Select(ToOption).ToList();
        }
    }

    public async Task<GitLabProjectSyncDto> LinkAsync(Guid projectId, LinkGitLabProjectRequest request, CancellationToken cancellationToken = default)
    {
        var (client, connection) = await ClientAsync(cancellationToken);
        using (client)
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == projectId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Project", projectId);
            if (project.SheetId is not { } sheetId)
            {
                throw new ArgumentException("This project has no plan yet. Start the plan on its Plan tab, then link it to GitLab.");
            }

            if (await db.Set<GitLabProjectLink>().AnyAsync(l => l.ProjectId == projectId, cancellationToken))
            {
                throw new ArgumentException("This project is already linked to GitLab. Unlink it first to link another GitLab project.");
            }

            if (string.IsNullOrWhiteSpace(request.Project))
            {
                throw new ArgumentException("Choose the GitLab project to sync with.");
            }

            var gitLabProject = await client.GetProjectAsync(GitLabUrls.ProjectRef(connection.BaseUrl, request.Project), cancellationToken);
            if (await db.Set<GitLabProjectLink>().AnyAsync(l => l.GitLabProjectId == gitLabProject.Id, cancellationToken))
            {
                throw new ArgumentException($"{gitLabProject.PathWithNamespace} is already linked to another Workbench project. Each GitLab project syncs with one plan.");
            }

            var group = gitLabProject.Namespace is { Kind: "group" } ns ? ns : null;
            var hasEpics = group is not null && await client.GroupHasEpicsAsync(group.Id, cancellationToken);
            var mode = request.Phases switch
            {
                GitLabPhaseChoice.Epics when !hasEpics => throw new ArgumentException(group is null
                    ? "Epics live in a GitLab group, and this project is in a personal namespace. Choose milestones."
                    : "This GitLab group doesn't have epics (they need GitLab Premium or Ultimate), or the token can't see them. Choose milestones."),
                GitLabPhaseChoice.Epics => GitLabPhaseMode.Epics,
                GitLabPhaseChoice.Milestones => GitLabPhaseMode.Milestones,
                _ => hasEpics ? GitLabPhaseMode.Epics : GitLabPhaseMode.Milestones,
            };

            var now = Now;
            var secret = GitLabSecrets.NewWebhookSecret();
            var link = new GitLabProjectLink
            {
                ProjectId = projectId, SheetId = sheetId, GitLabProjectId = gitLabProject.Id, GitLabProjectPath = gitLabProject.PathWithNamespace,
                GitLabProjectUrl = gitLabProject.WebUrl, GitLabGroupId = group?.Id, GitLabGroupPath = group?.FullPath, PhaseMode = mode,
                PhaseLevels = Math.Clamp(request.PhaseLevels, 0, 9),
                // Premium and Ultimate (the tiers with epics) also allow several assignees and "blocks" links.
                MultipleAssignees = hasEpics, BlockingLinks = hasEpics,
                WebhookSecretProtected = secrets.ProtectWebhookSecret(secret), CreatedBy = Me, CreatedAtUtc = now, UpdatedAtUtc = now,
            };
            db.Set<GitLabProjectLink>().Add(link);
            await db.SaveChangesAsync(cancellationToken);
            var added = await GitLabColumns.EnsureAsync(db, sheetId, cancellationToken);

            // Webhooks so GitLab's changes arrive within seconds; without them the reconcile picks them up every few minutes.
            var url = WebhookUrl(link.Id);
            if (Uri.TryCreate(url, UriKind.Absolute, out var hookUri) && (hookUri.Scheme == Uri.UriSchemeHttps || integrationOptions.Value.AllowHttp))
            {
                try
                {
                    link.HookId = await client.AddProjectHookAsync(gitLabProject.Id, url, secret, cancellationToken);
                }
                catch (GitLabApiException)
                {
                    // Needs Maintainer: the panel shows how to add it by hand.
                }

                if (mode == GitLabPhaseMode.Epics && group is not null)
                {
                    try
                    {
                        link.GroupHookId = await client.AddGroupHookAsync(group.Id, url, secret, cancellationToken);
                    }
                    catch (GitLabApiException)
                    {
                        // Group webhooks need the Owner role; epic changes then come in with the reconcile.
                    }
                }
            }

            var phaseWord = mode == GitLabPhaseMode.Epics ? "epics" : "milestones";
            db.Set<ActivityEvent>().Add(new ActivityEvent
            {
                SheetId = sheetId, Actor = Me, Kind = "item.gitlab", OccurredAtUtc = now,
                Summary = $"Linked the plan to GitLab project {gitLabProject.PathWithNamespace} (phases as {phaseWord})"
                    + (added.Count > 0 ? $"; added the {string.Join(" and ", added)} column{(added.Count == 1 ? "" : "s")}" : ""),
            });

            // Send the plan: one outbox event per task, phases first. The dispatcher creates them in GitLab with retries.
            var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
            var shape = new GitLabRules.PlanShape(items, link.PhaseLevels);
            var order = 0;
            foreach (var item in shape.InCreateOrder())
            {
                GitLabOutbox.RaiseTaskChanged(db, link.Id, sheetId, item.Id, "Linked", Me, now.AddTicks(order++));
            }

            await GitLabOutbox.EnsureConnectorAsync(db, connection.IsActive, Me, now, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            GitLabChangeListener.Invalidate();
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var link = await db.Set<GitLabProjectLink>().FirstOrDefaultAsync(l => l.ProjectId == projectId, cancellationToken)
            ?? throw new ArgumentException("This project isn't linked to GitLab.");
        if (await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(cancellationToken) is { } connection)
        {
            using var client = clients.For(connection);
            try
            {
                if (link.HookId is { } hook)
                {
                    await client.DeleteProjectHookAsync(link.GitLabProjectId, hook, cancellationToken);
                }

                if (link.GroupHookId is { } groupHook && link.GitLabGroupId is { } groupId)
                {
                    await client.DeleteGroupHookAsync(groupId, groupHook, cancellationToken);
                }
            }
            catch (GitLabApiException)
            {
                // The webhook may already be gone; its secret dies with the link, so it can't reach Workbench any more.
            }
        }

        var sheetId = link.SheetId;
        db.Set<GitLabProjectLink>().Remove(link);
        db.Set<ActivityEvent>().Add(new ActivityEvent
        {
            SheetId = sheetId, Actor = Me, Kind = "item.gitlab", OccurredAtUtc = Now,
            Summary = $"Unlinked the plan from GitLab project {link.GitLabProjectPath}. The issues stay in GitLab and the tasks stay here.",
        });
        await db.SaveChangesAsync(cancellationToken);
        GitLabChangeListener.Invalidate();
    }

    public async Task<GitLabSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        var report = await engine.ReconcileAsync(link.Id, everything: true, cancellationToken);
        var message = report.Error is { } error ? $"Sync stopped: {error}"
            : report.Synced + report.Created + report.NewIssues == 0 ? "Everything was already up to date."
            : string.Join(", ", new[]
            {
                report.Created > 0 ? $"{report.Created} created in GitLab" : null,
                report.Synced - report.Created > 0 ? $"{report.Synced - report.Created} synced" : null,
                report.NewIssues > 0 ? $"{report.NewIssues} new issue{(report.NewIssues == 1 ? "" : "s")} to approve" : null,
                report.Conflicts > 0 ? $"{report.Conflicts} note{(report.Conflicts == 1 ? "" : "s")} to review" : null,
                report.Failed > 0 ? $"{report.Failed} failed" : null,
            }.Where(s => s is not null)) + ".";
        return new GitLabSyncNowResult(report.Synced, report.Created, report.NewIssues, report.Conflicts, message);
    }

    public async Task<GitLabWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        var events = link.PhaseMode == GitLabPhaseMode.Epics ? ProjectHookEvents.Append("Epic events (on the group's webhook)").ToList() : ProjectHookEvents;
        return new GitLabWebhookSetupDto(WebhookUrl(link.Id), secrets.UnprotectWebhookSecret(link.WebhookSecretProtected), link.HookId is not null, events);
    }

    public async Task<GitLabProjectSyncDto> ResolveConflictAsync(Guid projectId, Guid conflictId, ResolveGitLabConflictRequest request, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var conflict = await db.Set<GitLabSyncConflict>().FirstOrDefaultAsync(c => c.Id == conflictId && c.LinkId == link.Id, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("Sync note", conflictId);
            if (conflict.Status != GitLabConflictStatus.Open)
            {
                throw new ArgumentException("This note was already settled.");
            }

            if (request.Choice == GitLabConflictChoice.Dismiss)
            {
                conflict.Status = GitLabConflictStatus.Dismissed;
                conflict.ResolvedBy = Me;
                conflict.ResolvedAtUtc = Now;
                conflict.Resolution = "Dismissed";
                await db.SaveChangesAsync(cancellationToken);
                return await GetAsync(projectId, cancellationToken);
            }

            var (useWorkbench, useGitLab) = Choices(conflict);
            if ((request.Choice == GitLabConflictChoice.UseWorkbench && !useWorkbench) || (request.Choice == GitLabConflictChoice.UseGitLab && !useGitLab))
            {
                throw new ArgumentException("That choice isn't available for this note.");
            }
        }

        await engine.ResolveAsync(link.Id, conflictId, request.Choice == GitLabConflictChoice.UseWorkbench, Me, cancellationToken);
        return await GetAsync(projectId, cancellationToken);
    }

    public async Task<GitLabProjectSyncDto> DecidePendingAsync(Guid projectId, Guid pendingId, bool approve, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        if (approve)
        {
            await engine.ApprovePendingAsync(link.Id, pendingId, Me, cancellationToken);
        }
        else
        {
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var pending = await db.Set<GitLabPendingIssue>().FirstOrDefaultAsync(p => p.Id == pendingId && p.LinkId == link.Id, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("New GitLab issue", pendingId);
            pending.Status = GitLabPendingStatus.Rejected;
            pending.DecidedBy = Me;
            pending.DecidedAtUtc = Now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(Guid projectId, string query, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        var (client, _) = await ClientAsync(cancellationToken);
        using (client)
        {
            var members = await client.ListProjectMembersAsync(link.GitLabProjectId, query, cancellationToken);
            if (members.Count == 0 && !string.IsNullOrWhiteSpace(query))
            {
                members = await client.SearchUsersAsync(query, cancellationToken);
            }

            return members.Select(u => new GitLabUserOptionDto(u.Id, u.Username, u.Name)).ToList();
        }
    }

    public async Task<GitLabProjectSyncDto> MatchPersonAsync(Guid projectId, MatchGitLabPersonRequest request, CancellationToken cancellationToken = default)
    {
        var link = await LinkAsync(projectId, cancellationToken);
        var (client, _) = await ClientAsync(cancellationToken);
        using (client)
        {
            await GitLabPeopleAdmin.MatchAsync(dbFactory, client, request, Me, clock, cancellationToken);
        }

        people.Forget();
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            // Send the tasks this person is on, so the issues get their assignee.
            foreach (var itemLink in await db.Set<GitLabItemLink>().AsNoTracking().Where(l => l.LinkId == link.Id && l.Kind == GitLabObjectKind.Issue && !l.IsRemoved).ToListAsync(cancellationToken))
            {
                GitLabOutbox.RaiseTaskChanged(db, link.Id, link.SheetId, itemLink.ItemId, "PersonMatched", Me, Now);
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetAsync(projectId, cancellationToken);
    }

    public async Task<GitLabTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var link = await db.Set<GitLabItemLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ItemId == itemId, cancellationToken);
        return link is null ? null : new GitLabTaskStatusDto(GitLabRules.Label(link.Kind, link.Iid), link.WebUrl, link.LastSyncedAtUtc, link.LastError, link.IsRemoved);
    }

    internal static (bool UseWorkbench, bool UseGitLab) Choices(GitLabSyncConflict c) => c.Field switch
    {
        "title" or "description" => (true, true),
        "assignees" => (false, false),
        "due" => (true, c.ObjectKind == GitLabObjectKind.Issue),
        "state" => (true, false),
        "percent" => (false, false),
        "sync" => (c.Outcome.Contains("deleted in GitLab", StringComparison.Ordinal), false),
        "estimate" or "wbs" or "parent" or "blocks" or "start" => (true, false),
        _ => (false, false),
    };

    private static GitLabConflictDto ToDto(GitLabSyncConflict c, string? taskTitle, GitLabItemLink? itemLink)
    {
        var (useWorkbench, useGitLab) = Choices(c);
        return new GitLabConflictDto(c.Id, c.ItemId, taskTitle, c.Iid is { } iid && c.ObjectKind is { } kind ? GitLabRules.Label(kind, iid) : null, c.Field,
            GitLabRules.FieldLabel(c.Field), c.Kind switch
            {
                GitLabConflictKind.WorkbenchOwns => "Workbench owns this",
                GitLabConflictKind.GitLabOwns => "GitLab owns this",
                GitLabConflictKind.BothChanged => "Changed in both",
                GitLabConflictKind.PersonNotMatched => "Person not matched",
                _ => "Couldn't sync",
            }, c.WorkbenchValue, c.GitLabValue, c.Outcome, c.Hint, c.UpdatedAtUtc, useWorkbench && itemLink is not null, useGitLab && itemLink is not null);
    }

    private async Task<IReadOnlyList<GitLabPersonToMatchDto>> PeopleToMatchAsync(
        IWorkbenchDbContext db, GitLabProjectLink link, Guid sheetId, IReadOnlyList<GitLabItemLink> itemLinks, IReadOnlyList<GitLabConflictDto> conflicts, CancellationToken ct)
    {
        var maps = await db.Set<GitLabUserMap>().AsNoTracking().Select(m => m.Email).ToListAsync(ct);
        var mapped = maps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var linkedItems = itemLinks.Where(l => l.Kind == GitLabObjectKind.Issue && !l.IsRemoved).Select(l => l.ItemId).ToHashSet();
        var assignees = await db.Set<Domain.Tasks.ItemAssignee>().AsNoTracking().Where(a => a.SheetId == sheetId).Select(a => new { a.ItemId, a.Email }).ToListAsync(ct);
        var elements = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ProjectId == link.ProjectId && w.ItemId != null).Select(w => new { w.Id, w.ItemId }).ToListAsync(ct);
        var staffed = await db.Set<Domain.Resourcing.StaffingAssignment>().AsNoTracking().Where(a => a.ProjectId == link.ProjectId && a.WbsElementId != null)
            .Join(db.Set<Employee>(), a => a.EmployeeId, e => e.Id, (a, e) => new { a.WbsElementId, e.Email }).ToListAsync(ct);
        var people = assignees.Where(a => linkedItems.Contains(a.ItemId)).Select(a => (a.ItemId, Email: a.Email.ToLowerInvariant()))
            .Concat(staffed.Select(s => (ItemId: elements.FirstOrDefault(e => e.Id == s.WbsElementId)?.ItemId ?? Guid.Empty, Email: s.Email.ToLowerInvariant()))
                .Where(x => linkedItems.Contains(x.ItemId)))
            .Where(x => !mapped.Contains(x.Email))
            .Distinct()
            .GroupBy(x => x.Email)
            .ToList();
        var names = await GitLabPeopleAdmin.NamesAsync(db, ct);
        var result = people.Select(g => new GitLabPersonToMatchDto("workbench", g.Key, names.GetValueOrDefault(g.Key), null, null, g.Count())).ToList();
        result.AddRange(conflicts.Where(c => c.Field == "person" && c.GitLabValue is not null).GroupBy(c => c.GitLabValue!)
            .Select(g => new GitLabPersonToMatchDto("gitlab", null, null, null, g.Key.TrimStart('@'), g.Count())));
        return result.OrderBy(r => r.Side).ThenBy(r => r.DisplayName ?? r.GitLabUsername).ToList();
    }

    private static async Task<(int Waiting, int Failed)> DeliveriesAsync(IWorkbenchDbContext db, Guid linkId, CancellationToken ct)
    {
        var connectorId = await db.Set<IntegrationConnector>().AsNoTracking().Where(c => c.Kind == GitLabOutbox.ConnectorKind).Select(c => (Guid?)c.Id).FirstOrDefaultAsync(ct);
        var undispatched = await db.Set<IntegrationOutboxEvent>().AsNoTracking()
            .CountAsync(e => e.DispatchedAtUtc == null && e.EventType.StartsWith("gitlab.") && e.PayloadJson.Contains(linkId.ToString()), ct);
        if (connectorId is null)
        {
            return (undispatched, 0);
        }

        var key = linkId.ToString();
        var rows = await db.Set<IntegrationDelivery>().AsNoTracking()
            .Where(d => d.ConnectorId == connectorId && (d.Status == DeliveryStatus.Pending || d.Status == DeliveryStatus.Failed))
            .Join(db.Set<IntegrationOutboxEvent>().Where(e => e.PayloadJson.Contains(key)), d => d.EventId, e => e.Id, (d, e) => d.Status)
            .ToListAsync(ct);
        return (undispatched + rows.Count(s => s == DeliveryStatus.Pending), rows.Count(s => s == DeliveryStatus.Failed));
    }

    private async Task<GitLabProjectLink> LinkAsync(Guid projectId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<GitLabProjectLink>().AsNoTracking().FirstOrDefaultAsync(l => l.ProjectId == projectId, ct)
            ?? throw new ArgumentException("This project isn't linked to GitLab.");
    }

    private async Task<(GitLabClient Client, GitLabConnection Connection)> ClientAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var connection = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (connection is not { IsActive: true })
        {
            throw new ArgumentException(connection is null
                ? "GitLab isn't connected yet. An administrator connects it under Admin › Integrations › GitLab."
                : "The GitLab connection is turned off (Admin › Integrations › GitLab).");
        }

        return (clients.For(connection), connection);
    }

    private string WebhookUrl(Guid linkId) => links.Absolute($"/webhooks/gitlab/{linkId}");

    private static GitLabProjectOptionDto ToOption(GitLabProject p) =>
        new(p.Id, p.PathWithNamespace, p.Name, p.WebUrl, p.Namespace is { Kind: "group" } g ? g.Id : null, p.Namespace is { Kind: "group" } n ? n.FullPath : null);
}

/// <summary>Admin › Integrations › GitLab is for administrators (Manage integrations); API keys never manage it.</summary>
public sealed class PermissionCheckingGitLabConnectionService(IGitLabConnectionService inner, AccessControl access) : IGitLabConnectionService
{
    public async Task<GitLabConnectionDto> GetAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetAsync(cancellationToken);
    }

    public async Task<GitLabConnectionDto> SaveAsync(SaveGitLabConnectionRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.SaveAsync(request, cancellationToken);
    }

    public async Task<GitLabTestResult> TestAsync(SaveGitLabConnectionRequest? unsaved = null, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.TestAsync(unsaved, cancellationToken);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await inner.DeleteAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GitLabUserMapDto>> ListPeopleAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListPeopleAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(string query, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.SearchUsersAsync(query, cancellationToken);
    }

    public async Task<GitLabUserMapDto> MatchPersonAsync(MatchGitLabPersonRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.MatchPersonAsync(request, cancellationToken);
    }

    public async Task UnmatchPersonAsync(string email, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await inner.UnmatchPersonAsync(email, cancellationToken);
    }
}

/// <summary>
/// A project's GitLab panel: anyone who can see the project's plan reads it; linking, unlinking, syncing and deciding notes
/// is project setup (Finance, administrators, the project's manager with the Project manager role). API keys are refused
/// (the endpoints live under <c>/integrations</c>, which no key scope covers).
/// </summary>
public sealed class PermissionCheckingGitLabProjectSyncService(IGitLabProjectSyncService inner, ProjectAccess projects, AccessControl access, IWorkbenchDbContextFactory dbFactory)
    : IGitLabProjectSyncService
{
    public async Task<GitLabProjectSyncDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireReadAsync(projectId, cancellationToken);
        return await inner.GetAsync(projectId, cancellationToken);
    }

    public async Task<IReadOnlyList<GitLabProjectOptionDto>> SearchGitLabProjectsAsync(Guid projectId, string? query, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "link the project to GitLab", cancellationToken);
        return await inner.SearchGitLabProjectsAsync(projectId, query, cancellationToken);
    }

    public async Task<GitLabProjectSyncDto> LinkAsync(Guid projectId, LinkGitLabProjectRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "link the project to GitLab", cancellationToken);
        return await inner.LinkAsync(projectId, request, cancellationToken);
    }

    public async Task UnlinkAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "unlink the project from GitLab", cancellationToken);
        await inner.UnlinkAsync(projectId, cancellationToken);
    }

    public async Task<GitLabSyncNowResult> SyncNowAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "sync the project with GitLab", cancellationToken);
        return await inner.SyncNowAsync(projectId, cancellationToken);
    }

    public async Task<GitLabWebhookSetupDto> GetWebhookSetupAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "see the GitLab webhook's secret", cancellationToken);
        return await inner.GetWebhookSetupAsync(projectId, cancellationToken);
    }

    public async Task<GitLabProjectSyncDto> ResolveConflictAsync(Guid projectId, Guid conflictId, ResolveGitLabConflictRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "settle GitLab sync notes", cancellationToken);
        return await inner.ResolveConflictAsync(projectId, conflictId, request, cancellationToken);
    }

    public async Task<GitLabProjectSyncDto> DecidePendingAsync(Guid projectId, Guid pendingId, bool approve, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "add GitLab issues to the plan", cancellationToken);
        return await inner.DecidePendingAsync(projectId, pendingId, approve, cancellationToken);
    }

    public async Task<IReadOnlyList<GitLabUserOptionDto>> SearchUsersAsync(Guid projectId, string query, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "match people to GitLab users", cancellationToken);
        return await inner.SearchUsersAsync(projectId, query, cancellationToken);
    }

    public async Task<GitLabProjectSyncDto> MatchPersonAsync(Guid projectId, MatchGitLabPersonRequest request, CancellationToken cancellationToken = default)
    {
        await RequireSetupAsync(projectId, "match people to GitLab users", cancellationToken);
        return await inner.MatchPersonAsync(projectId, request, cancellationToken);
    }

    public async Task<GitLabTaskStatusDto?> GetTaskStatusAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
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
            throw await access.DenyAsync("You need access to the project's plan to see its GitLab sync.", ct);
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
            throw new WorkbenchForbiddenException("API keys can't use the GitLab sync.");
        }
    }
}

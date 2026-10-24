using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations.Connectors;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations;
using Workbench.Domain.Integrations;
using Workbench.Domain.Integrations.GitLab;

namespace Workbench.Application.Integrations.GitLab;

/// <summary>
/// GitLab work goes through the integrations outbox, so it is durable and retried with backoff like every delivery: a plan
/// change or a GitLab webhook adds an event, and the dispatcher hands it to the hidden "GitLab sync" connector
/// (<see cref="GitLabSyncConnector"/>), which syncs that task or issue. Its runs show in Admin › Integrations › Connectors and
/// Deliveries. These event types are internal: they aren't in the webhook catalog, so no webhook endpoint can subscribe.
/// </summary>
public static class GitLabOutbox
{
    public const string ConnectorKind = "gitlab";
    public const string ConnectorName = "GitLab sync";
    public const string TaskChanged = "gitlab.task_changed";
    public const string Pull = "gitlab.pull";

    public sealed record TaskChangedData(Guid LinkId, Guid SheetId, Guid ItemId, string Change);

    /// <param name="ObjectKind"><c>issue</c>, <c>epic</c> or <c>milestone</c>.</param>
    public sealed record PullData(Guid LinkId, string ObjectKind, long Id, long Iid, string? Action);

    public static void RaiseTaskChanged(IWorkbenchDbContext db, Guid linkId, Guid sheetId, Guid itemId, string change, string? actor, DateTime now) =>
        db.Set<IntegrationOutboxEvent>().Add(IntegrationOutbox.Build(TaskChanged, "Item", itemId, new TaskChangedData(linkId, sheetId, itemId, change), actor, now));

    public static void RaisePull(IWorkbenchDbContext db, PullData data, DateTime now) =>
        db.Set<IntegrationOutboxEvent>().Add(IntegrationOutbox.Build(Pull, "GitLab" + char.ToUpperInvariant(data.ObjectKind[0]) + data.ObjectKind[1..], data.LinkId, data, GitLabSyncEngine.Actor, now));

    /// <summary>The hidden connector row the dispatcher delivers GitLab events to; created with the connection, off when it's off.</summary>
    public static async Task EnsureConnectorAsync(IWorkbenchDbContext db, bool active, string actor, DateTime now, CancellationToken ct)
    {
        var row = await db.Set<IntegrationConnector>().FirstOrDefaultAsync(c => c.Kind == ConnectorKind, ct);
        if (row is null)
        {
            row = new IntegrationConnector
            {
                Kind = ConnectorKind, Name = ConnectorName, SettingsJson = "{}", CreatedBy = actor, CreatedAtUtc = now, UpdatedAtUtc = now,
                Schedule = ConnectorSchedule.Manual,
            };
            db.Set<IntegrationConnector>().Add(row);
        }

        row.EventTypes = $"{TaskChanged},{Pull}";
        row.IsActive = active;
        row.UpdatedBy = actor;
        row.UpdatedAtUtc = now;
    }

    internal static T? Data<T>(IntegrationOutboxEvent e)
    {
        using var doc = JsonDocument.Parse(e.PayloadJson);
        return doc.RootElement.TryGetProperty("data", out var data) ? data.Deserialize<T>(IntegrationOutbox.Json) : default;
    }
}

/// <summary>
/// The "GitLab sync" connector: syncs the task or issue each GitLab outbox event names. A GitLab failure worth retrying (no
/// answer, rate limit, a server error) returns its status so the dispatcher retries with backoff; anything else is recorded
/// on the project's GitLab panel with what to do. It's set up on the GitLab tab, not the Connectors tab.
/// </summary>
public sealed class GitLabSyncConnector(GitLabSyncEngine engine, IWorkbenchDbContextFactory dbFactory, GitLabClientFactory clients, TimeProvider clock) : IConnector
{
    public string Kind => GitLabOutbox.ConnectorKind;

    public string Name => GitLabOutbox.ConnectorName;

    public string Description => "Sends plan changes to GitLab and takes GitLab's. Set it up on the GitLab tab.";

    public ConnectorMode Mode => ConnectorMode.Events;

    public bool IsListed => false;

    public Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectorFieldDto>>([]);

    public Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken) =>
        throw new ArgumentException("GitLab is set up on the GitLab tab of Admin › Integrations, and each project's link on its Setup tab.");

    public async Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
    {
        if (context.Event is not { } e)
        {
            return await TestAsync(cancellationToken);
        }

        Guid? linkId = null;
        Guid? itemId = null;
        try
        {
            GitLabSyncReport report;
            string what;
            switch (e.EventType)
            {
                case GitLabOutbox.TaskChanged when GitLabOutbox.Data<GitLabOutbox.TaskChangedData>(e) is { } task:
                    linkId = task.LinkId;
                    itemId = task.ItemId;
                    report = await engine.SyncTaskAsync(task.LinkId, task.ItemId, cancellationToken);
                    what = "Task";
                    break;
                case GitLabOutbox.Pull when GitLabOutbox.Data<GitLabOutbox.PullData>(e) is { } pull:
                    linkId = pull.LinkId;
                    report = pull.ObjectKind switch
                    {
                        "epic" => await engine.SyncPhaseObjectAsync(pull.LinkId, GitLabObjectKind.Epic, pull.Iid, cancellationToken),
                        "milestone" => await engine.SyncPhaseObjectAsync(pull.LinkId, GitLabObjectKind.Milestone, pull.Id, cancellationToken),
                        _ => await engine.SyncIssueAsync(pull.LinkId, pull.Iid, cancellationToken),
                    };
                    what = pull.ObjectKind == "issue" ? $"Issue #{pull.Iid}" : pull.ObjectKind == "epic" ? $"Epic &{pull.Iid}" : "Milestone";
                    break;
                default:
                    return new ConnectorResult(true, "Nothing to do for this event.");
            }

            return new ConnectorResult(true, Describe(what, report), report.Synced + report.Created + report.NewIssues);
        }
        catch (GitLabApiException ex)
        {
            if (linkId is { } id)
            {
                await RecordFailureAsync(id, itemId, ex, cancellationToken);
            }

            var response = new OutboundResponse(ex.StatusCode, null, null, ex.Message, 0);
            return new ConnectorResult(false, $"{ex.Message} {ex.Hint}", Response: response);
        }
    }

    private static string Describe(string what, GitLabSyncReport report) =>
        report.Created > 0 ? $"{what}: created in GitLab."
        : report.NewIssues > 0 ? $"{what}: new in GitLab, waiting for approval."
        : report.Conflicts > 0 ? $"{what}: synced with {report.Conflicts} note{(report.Conflicts == 1 ? "" : "s")} on the sync panel."
        : report.Synced > 0 ? $"{what}: synced."
        : $"{what}: already up to date.";

    private async Task RecordFailureAsync(Guid linkId, Guid? itemId, GitLabApiException ex, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var link = await db.Set<GitLabProjectLink>().FirstOrDefaultAsync(l => l.Id == linkId, ct);
        if (link is null)
        {
            return;
        }

        link.LastError = GitLabRules.Clip(ex.Message, 1000);
        link.LastErrorHint = GitLabRules.Clip(ex.Hint, 1000);
        if (ex.StatusCode is 401 or 403)
        {
            link.Status = GitLabLinkStatus.NeedsAttention;
        }

        if (itemId is { } id && await db.Set<GitLabItemLink>().FirstOrDefaultAsync(l => l.ItemId == id, ct) is { } itemLink)
        {
            itemLink.LastError = GitLabRules.Clip(ex.Message, 1000);
        }

        if (!ex.IsRetryable)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            var open = await db.Set<GitLabSyncConflict>().FirstOrDefaultAsync(c => c.LinkId == linkId && c.ItemId == itemId && c.Field == "sync"
                && c.Status == GitLabConflictStatus.Open, ct);
            if (open is null)
            {
                open = new GitLabSyncConflict { LinkId = linkId, ItemId = itemId, Field = "sync", Kind = GitLabConflictKind.SyncFailed, Outcome = "", CreatedAtUtc = now };
                db.Set<GitLabSyncConflict>().Add(open);
            }

            open.Outcome = GitLabRules.Clip($"A change couldn't be sent to GitLab: {ex.Message}", 1000);
            open.Hint = GitLabRules.Clip(ex.Hint, 1000);
            open.UpdatedAtUtc = now;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // The link changed meanwhile; the next sync records its own state.
        }
    }

    private async Task<ConnectorResult> TestAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var connection = await db.Set<GitLabConnection>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (connection is null)
        {
            return new ConnectorResult(false, "GitLab isn't connected. Set it up on the GitLab tab.");
        }

        try
        {
            using var client = clients.For(connection);
            var me = await client.GetCurrentUserAsync(ct);
            return new ConnectorResult(true, $"Connected to {connection.BaseUrl} as @{me.Username}.");
        }
        catch (GitLabApiException ex)
        {
            return new ConnectorResult(false, $"{ex.Message} {ex.Hint}");
        }
    }
}

/// <summary>
/// Turns plan changes into GitLab outbox events: listens to every saved sheet change (the live-update hub) and, for a sheet
/// that is a linked project's plan, adds a "task changed" event. Changes the sync itself made (actor GitLab) are skipped. The
/// reconcile job catches anything that raises no live change (dependencies, staffing hours, a crash between save and event).
/// </summary>
public sealed class GitLabChangeListener(SheetEventHub hub, IWorkbenchDbContextFactory dbFactory, TimeProvider clock, ILogger<GitLabChangeListener> logger) : IHostedService
{
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(30);
    private static int _version;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, int Version, Dictionary<Guid, Guid> Links)> _cache = new(StringComparer.Ordinal);
    private IAsyncDisposable? _subscription;

    /// <summary>Call after linking or unlinking a project, so changes start (or stop) being sent straight away.</summary>
    public static void Invalidate() => Interlocked.Increment(ref _version);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscription = hub.SubscribeAll(OnChangeAsync);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_subscription is not null)
        {
            await _subscription.DisposeAsync();
        }
    }

    public async Task OnChangeAsync(SheetChange change)
    {
        if (change.Kind is not (SheetChangeKind.ItemCreated or SheetChangeKind.ItemUpdated or SheetChangeKind.ItemMoved or SheetChangeKind.ItemDeleted)
            || change.ItemId is not { } itemId || change.Actor == GitLabSyncEngine.Actor)
        {
            return;
        }

        try
        {
            var links = await LinksAsync();
            if (!links.TryGetValue(change.SheetId, out var linkId))
            {
                return;
            }

            await using var db = await dbFactory.CreateAsync(CancellationToken.None);
            var ids = new List<Guid> { itemId };
            if (change.Kind == SheetChangeKind.ItemMoved)
            {
                // A move can change the phase (and WBS code) of everything under the task.
                var rows = await db.Items.AsNoTracking().Where(i => i.SheetId == change.SheetId).Select(i => new { i.Id, i.ParentId }).ToListAsync();
                for (var i = 0; i < ids.Count; i++)
                {
                    ids.AddRange(rows.Where(r => r.ParentId == ids[i]).Select(r => r.Id));
                }
            }

            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var id in ids.Distinct())
            {
                GitLabOutbox.RaiseTaskChanged(db, linkId, change.SheetId, id, change.Kind.ToString(), change.Actor, now);
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't queue the GitLab sync of task {ItemId}; the next reconcile sends it.", itemId);
        }
    }

    private async Task<Dictionary<Guid, Guid>> LinksAsync()
    {
        var key = TenantScope.Key;
        var now = clock.GetUtcNow();
        var version = Volatile.Read(ref _version);
        if (_cache.TryGetValue(key, out var cached) && cached.Version == version && now - cached.At < CacheFor)
        {
            return cached.Links;
        }

        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var links = await db.Set<GitLabProjectLink>().AsNoTracking()
            .Join(db.Set<Domain.ProjectAccounting.Project>(), l => l.ProjectId, p => p.Id, (l, p) => new { l.Id, SheetId = p.SheetId ?? l.SheetId })
            .ToListAsync();
        var map = links.GroupBy(l => l.SheetId).ToDictionary(g => g.Key, g => g.First().Id);
        _cache[key] = (now, version, map);
        return map;
    }
}

/// <summary>Reconciles every linked project every <see cref="GitLabOptions.ReconcileMinutes"/>, in every tenant, and prunes old webhook receipts.</summary>
public sealed class GitLabReconcileBackgroundService(
    IServiceScopeFactory scopes, ITenantJobRunner tenants, IOptions<GitLabOptions> options, TimeProvider clock, ILogger<GitLabReconcileBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.ReconcileEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), clock);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await RunOnceAsync(scope.ServiceProvider, options.Value, clock, logger, ct);
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "GitLab reconcile failed");
            }
        }
    }

    /// <summary>Reconciles the current tenant's links that are due (claimed first, so two hosts don't both run one).</summary>
    public static async Task<int> RunOnceAsync(IServiceProvider services, GitLabOptions options, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var engine = services.GetRequiredService<GitLabSyncEngine>();
        var now = clock.GetUtcNow().UtcDateTime;
        var dueBefore = now.AddMinutes(-Math.Max(1, options.ReconcileMinutes));
        List<Guid> due;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            if (!await db.Set<GitLabConnection>().AnyAsync(c => c.IsActive, ct))
            {
                return 0;
            }

            due = await db.Set<GitLabProjectLink>().AsNoTracking()
                .Where(l => l.LastReconcileAtUtc == null || l.LastReconcileAtUtc < dueBefore).Select(l => l.Id).ToListAsync(ct);
            var receiptCutoff = now.AddDays(-Math.Max(1, options.ReceiptRetentionDays));
            var old = await db.Set<GitLabWebhookReceipt>().Where(r => r.ReceivedAtUtc < receiptCutoff).Take(500).ToListAsync(ct);
            if (old.Count > 0)
            {
                db.Set<GitLabWebhookReceipt>().RemoveRange(old);
                await db.SaveChangesAsync(ct);
            }
        }

        var ran = 0;
        foreach (var id in due)
        {
            await using (var db = await dbFactory.CreateAsync(ct))
            {
                var link = await db.Set<GitLabProjectLink>().FirstOrDefaultAsync(l => l.Id == id, ct);
                if (link is null || link.LastReconcileAtUtc >= dueBefore)
                {
                    continue;
                }

                link.LastReconcileAtUtc = now;
                link.Version = Guid.NewGuid();
                try
                {
                    await db.SaveChangesAsync(ct);
                }
                catch (DbUpdateConcurrencyException)
                {
                    continue; // another host took it
                }
            }

            try
            {
                var report = await engine.ReconcileAsync(id, everything: false, ct);
                if (report.Synced + report.Created + report.NewIssues + report.Failed > 0)
                {
                    logger.LogInformation("GitLab reconcile of link {LinkId}: {Synced} synced, {Created} created, {New} new issues, {Failed} failed",
                        id, report.Synced, report.Created, report.NewIssues, report.Failed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "GitLab reconcile of link {LinkId} failed", id);
            }

            ran++;
        }

        return ran;
    }
}

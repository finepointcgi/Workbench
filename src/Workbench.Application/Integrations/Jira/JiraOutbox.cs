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
using Workbench.Domain.Integrations.Jira;

namespace Workbench.Application.Integrations.Jira;

/// <summary>
/// Jira work goes through the integrations outbox, so it is durable and retried with backoff like every delivery: a plan
/// change or a Jira webhook adds an event, and the dispatcher hands it to the hidden "Jira sync" connector
/// (<see cref="JiraSyncConnector"/>). Its runs show in Admin › Integrations › Connectors and Deliveries. These event types are
/// internal: they aren't in the webhook catalog, so no webhook endpoint can subscribe. A Jira that is down only delays them.
/// </summary>
public static class JiraOutbox
{
    public const string ConnectorKind = "jira";
    public const string ConnectorName = "Jira sync";
    public const string TaskChanged = "jira.task_changed";
    public const string Pull = "jira.pull";
    public const string FullSync = "jira.sync";

    public sealed record TaskChangedData(Guid LinkId, Guid SheetId, Guid ItemId, string Change);

    public sealed record PullData(Guid LinkId, string IssueKey, bool Deleted);

    public sealed record FullSyncData(Guid LinkId);

    public static void RaiseTaskChanged(IWorkbenchDbContext db, Guid linkId, Guid sheetId, Guid itemId, string change, string? actor, DateTime now) =>
        db.Set<IntegrationOutboxEvent>().Add(IntegrationOutbox.Build(TaskChanged, "Item", itemId, new TaskChangedData(linkId, sheetId, itemId, change), actor, now));

    public static void RaisePull(IWorkbenchDbContext db, PullData data, DateTime now) =>
        db.Set<IntegrationOutboxEvent>().Add(IntegrationOutbox.Build(Pull, "JiraIssue", data.LinkId, data, JiraSyncEngine.Actor, now));

    public static void RaiseFullSync(IWorkbenchDbContext db, Guid linkId, string? actor, DateTime now) =>
        db.Set<IntegrationOutboxEvent>().Add(IntegrationOutbox.Build(FullSync, "JiraLink", linkId, new FullSyncData(linkId), actor, now));

    /// <summary>The hidden connector row the dispatcher delivers Jira events to; created with the connection, off when it's off.</summary>
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

        row.EventTypes = $"{TaskChanged},{Pull},{FullSync}";
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
/// The "Jira sync" connector: syncs the task or issue each Jira outbox event names. A Jira failure worth retrying (no answer,
/// rate limit, a server error) returns its status so the dispatcher retries with backoff; anything else is shown on the
/// project's Jira panel with what to do. It's set up on the Jira tab, not the Connectors tab.
/// </summary>
public sealed class JiraSyncConnector(JiraSyncEngine engine, IWorkbenchDbContextFactory dbFactory, JiraClientFactory clients) : IConnector
{
    public string Kind => JiraOutbox.ConnectorKind;

    public string Name => JiraOutbox.ConnectorName;

    public string Description => "Sends plan changes to Jira and takes Jira's. Set it up on the Jira tab.";

    public ConnectorMode Mode => ConnectorMode.Events;

    public bool IsListed => false;

    public Task<IReadOnlyList<ConnectorFieldDto>> GetFieldsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ConnectorFieldDto>>([]);

    public Task ValidateAsync(ConnectorConfig config, CancellationToken cancellationToken) =>
        throw new ArgumentException("Jira is set up on the Jira tab of Admin › Integrations, and each project's link on its Setup tab.");

    public async Task<ConnectorResult> RunAsync(ConnectorContext context, CancellationToken cancellationToken)
    {
        if (context.Event is not { } e)
        {
            return await TestAsync(cancellationToken);
        }

        try
        {
            JiraSyncReport report;
            string what;
            switch (e.EventType)
            {
                case JiraOutbox.TaskChanged when JiraOutbox.Data<JiraOutbox.TaskChangedData>(e) is { } task:
                    report = await engine.SyncTaskAsync(task.LinkId, task.ItemId, cancellationToken);
                    what = "Task";
                    break;
                case JiraOutbox.Pull when JiraOutbox.Data<JiraOutbox.PullData>(e) is { } pull:
                    report = await engine.SyncIssueAsync(pull.LinkId, pull.IssueKey, pull.Deleted, cancellationToken);
                    what = $"Issue {pull.IssueKey}";
                    break;
                case JiraOutbox.FullSync when JiraOutbox.Data<JiraOutbox.FullSyncData>(e) is { } full:
                    report = await engine.ReconcileAsync(full.LinkId, everything: true, cancellationToken);
                    what = "Project";
                    break;
                default:
                    return new ConnectorResult(true, "Nothing to do for this event.");
            }

            return new ConnectorResult(true, report.Created > 0 ? $"{what}: {report.Created} created." : report.Synced > 0 ? $"{what}: synced." : $"{what}: already up to date.", report.Synced + report.Created);
        }
        catch (JiraApiException ex)
        {
            // The engine already recorded it on the link; a retryable failure is retried with backoff, anything else is final.
            return new ConnectorResult(false, $"{ex.Message} {ex.Hint}", Response: new OutboundResponse(ex.StatusCode, null, null, ex.Message, 0));
        }
    }

    private async Task<ConnectorResult> TestAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var connection = await db.Set<JiraConnection>().AsNoTracking().FirstOrDefaultAsync(ct);
        if (connection is null)
        {
            return new ConnectorResult(false, "Jira isn't connected. Set it up on the Jira tab.");
        }

        try
        {
            using var client = clients.For(connection);
            var me = await client.GetMyselfAsync(ct);
            return new ConnectorResult(true, $"Connected to {connection.SiteUrl} as {me.DisplayName}.");
        }
        catch (JiraApiException ex)
        {
            return new ConnectorResult(false, $"{ex.Message} {ex.Hint}");
        }
    }
}

/// <summary>
/// Turns plan changes into Jira outbox events: listens to every saved sheet change and, for a sheet that is a linked
/// project's plan, adds a "task changed" event. Changes the sync itself made (actor Jira) are skipped. The poll catches anything missed.
/// </summary>
public sealed class JiraChangeListener(SheetEventHub hub, IWorkbenchDbContextFactory dbFactory, TimeProvider clock, ILogger<JiraChangeListener> logger) : IHostedService
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
        if (change.Kind is not (SheetChangeKind.ItemCreated or SheetChangeKind.ItemUpdated) || change.ItemId is not { } itemId || change.Actor == JiraSyncEngine.Actor)
        {
            return;
        }

        try
        {
            if (!(await LinksAsync()).TryGetValue(change.SheetId, out var linkId))
            {
                return;
            }

            await using var db = await dbFactory.CreateAsync(CancellationToken.None);
            JiraOutbox.RaiseTaskChanged(db, linkId, change.SheetId, itemId, change.Kind.ToString(), change.Actor, clock.GetUtcNow().UtcDateTime);
            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't queue the Jira sync of task {ItemId}; the next poll sends it.", itemId);
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
        var links = await db.Set<JiraProjectLink>().AsNoTracking()
            .Join(db.Set<Domain.ProjectAccounting.Project>(), l => l.ProjectId, p => p.Id, (l, p) => new { l.Id, SheetId = p.SheetId ?? l.SheetId })
            .ToListAsync();
        var map = links.GroupBy(l => l.SheetId).ToDictionary(g => g.Key, g => g.First().Id);
        _cache[key] = (now, version, map);
        return map;
    }
}

/// <summary>Polls every linked project every <see cref="JiraOptions.ReconcileMinutes"/>, in every tenant, and prunes old webhook receipts.</summary>
public sealed class JiraReconcileBackgroundService(
    IServiceScopeFactory scopes, ITenantJobRunner tenants, IOptions<JiraOptions> options, TimeProvider clock, ILogger<JiraReconcileBackgroundService> logger)
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
                logger.LogError(ex, "Jira poll failed");
            }
        }
    }

    /// <summary>Polls the current tenant's links that are due (claimed first, so two hosts don't both run one).</summary>
    public static async Task<int> RunOnceAsync(IServiceProvider services, JiraOptions options, TimeProvider clock, ILogger logger, CancellationToken ct)
    {
        var dbFactory = services.GetRequiredService<IWorkbenchDbContextFactory>();
        var engine = services.GetRequiredService<JiraSyncEngine>();
        var now = clock.GetUtcNow().UtcDateTime;
        var dueBefore = now.AddMinutes(-Math.Max(1, options.ReconcileMinutes));
        List<Guid> due;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            if (!await db.Set<JiraConnection>().AnyAsync(c => c.IsActive, ct))
            {
                return 0;
            }

            due = await db.Set<JiraProjectLink>().AsNoTracking()
                .Where(l => l.LastReconcileAtUtc == null || l.LastReconcileAtUtc < dueBefore).Select(l => l.Id).ToListAsync(ct);
            var old = await db.Set<JiraWebhookReceipt>().Where(r => r.ReceivedAtUtc < now.AddDays(-Math.Max(1, options.ReceiptRetentionDays))).Take(500).ToListAsync(ct);
            if (old.Count > 0)
            {
                db.Set<JiraWebhookReceipt>().RemoveRange(old);
                await db.SaveChangesAsync(ct);
            }
        }

        var ran = 0;
        foreach (var id in due)
        {
            await using (var db = await dbFactory.CreateAsync(ct))
            {
                var link = await db.Set<JiraProjectLink>().FirstOrDefaultAsync(l => l.Id == id, ct);
                if (link is null || link.LastReconcileAtUtc >= dueBefore)
                {
                    continue;
                }

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
                await engine.ReconcileAsync(id, everything: false, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Jira poll of link {LinkId} failed", id);
            }

            ran++;
        }

        return ran;
    }
}

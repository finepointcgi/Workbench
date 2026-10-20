using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations.Connectors;
using Workbench.Application.Notifications;
using Workbench.Contracts.Integrations;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations;

public sealed record DispatchResult(int EventsFannedOut, int Attempts, int ConnectorRuns);

/// <summary>
/// Sends the current tenant's integration traffic, one pass at a time:
/// <list type="number">
/// <item>fans each new outbox event out to a delivery per subscribed active endpoint and event connector (once; a unique key
/// stops two hosts from doing it twice);</item>
/// <item>attempts due deliveries: signed POSTs for webhooks, the connector's post for Teams/Slack. Failures are retried with
/// exponential backoff up to <see cref="IntegrationOptions.MaxAttempts"/>; after <see cref="IntegrationOptions.UnhealthyAfterFailures"/>
/// consecutive failures the endpoint is marked unhealthy and only probed hourly until a delivery succeeds;</item>
/// <item>runs scheduled connectors that are due;</item>
/// <item>prunes events, deliveries and runs older than the retention period.</item>
/// </list>
/// Every attempt is claimed first (a concurrency token), so several hosts can run it safely. Delivery is at least once:
/// receivers should ignore an <c>id</c> they've already processed.
/// </summary>
public sealed class IntegrationDispatcher(
    IWorkbenchDbContextFactory dbFactory,
    IOutboundTransport transport,
    IntegrationSecrets secrets,
    ConnectorRunner runner,
    IBlobStore blobs,
    IOptions<IntegrationOptions> options,
    TimeProvider clock,
    ILogger<IntegrationDispatcher> logger,
    INotificationPublisher? notifications = null)
{
    public static readonly TimeSpan ProbeInterval = TimeSpan.FromHours(1);
    private const int MaxBodyChars = 64000;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private IntegrationOptions Options => options.Value;

    public async Task<DispatchResult> RunOnceAsync(CancellationToken ct)
    {
        var fanned = await FanOutAsync(ct);
        var attempts = await DeliverDueAsync(ct);
        var runs = await RunDueConnectorsAsync(ct);
        await PruneAsync(ct);
        return new DispatchResult(fanned, attempts, runs);
    }

    /// <summary>Creates the deliveries for events not yet dispatched. Returns how many events were fanned out.</summary>
    public async Task<int> FanOutAsync(CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var events = await db.Set<IntegrationOutboxEvent>().Where(e => e.DispatchedAtUtc == null).OrderBy(e => e.OccurredAtUtc).Take(200).ToListAsync(ct);
        if (events.Count == 0)
        {
            return 0;
        }

        var now = Now;
        var endpoints = (await db.Set<WebhookEndpoint>().AsNoTracking().Where(e => e.IsActive).ToListAsync(ct))
            .Select(e => (e.Id, Events: Split(e.EventTypes))).ToList();
        var eventConnectors = (await db.Set<IntegrationConnector>().AsNoTracking().Where(c => c.IsActive && c.EventTypes != null).ToListAsync(ct))
            .Where(c => runner.Find(c.Kind)?.Mode == ConnectorMode.Events)
            .Select(c => (c.Id, Events: Split(c.EventTypes))).ToList();

        foreach (var e in events)
        {
            var count = 0;
            foreach (var endpoint in endpoints.Where(x => x.Events.Contains(e.EventType)))
            {
                db.Set<IntegrationDelivery>().Add(NewDelivery(e, DeliveryTargetKind.Webhook, endpoint.Id, now));
                count++;
            }

            foreach (var connector in eventConnectors.Where(x => x.Events.Contains(e.EventType)))
            {
                db.Set<IntegrationDelivery>().Add(NewDelivery(e, DeliveryTargetKind.Connector, connector.Id, now));
                count++;
            }

            e.DispatchedAtUtc = now;
            e.DeliveryCount = count;
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex)
        {
            // Another host fanned these out first (the unique delivery key); its rows stand.
            logger.LogInformation(ex, "Integration fan-out skipped {Count} events another dispatcher took", events.Count);
            return 0;
        }

        return events.Count;
    }

    internal static IntegrationDelivery NewDelivery(IntegrationOutboxEvent e, DeliveryTargetKind kind, Guid targetId, DateTime now, Guid? redeliveryOf = null, string? requestedBy = null) => new()
    {
        TargetKind = kind,
        EndpointId = kind == DeliveryTargetKind.Webhook ? targetId : null,
        ConnectorId = kind == DeliveryTargetKind.Connector ? targetId : null,
        EventId = e.Id,
        EventType = e.EventType,
        DedupKey = redeliveryOf is null ? $"{e.Id:N}:{targetId:N}" : $"{e.Id:N}:{targetId:N}:{Guid.NewGuid():N}",
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
        RedeliveryOfId = redeliveryOf,
        RequestedBy = requestedBy,
    };

    /// <summary>Attempts every delivery that is due. Returns how many attempts were made.</summary>
    public async Task<int> DeliverDueAsync(CancellationToken ct)
    {
        List<Guid> due;
        var now = Now;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            due = await db.Set<IntegrationDelivery>().AsNoTracking()
                .Where(d => d.Status == DeliveryStatus.Pending && d.NextAttemptAtUtc <= now)
                .OrderBy(d => d.NextAttemptAtUtc).Select(d => d.Id).Take(100).ToListAsync(ct);
        }

        var attempts = 0;
        foreach (var id in due)
        {
            if (await AttemptAsync(id, ct))
            {
                attempts++;
            }
        }

        return attempts;
    }

    /// <summary>Makes one attempt of a due delivery. False when it wasn't due, was claimed elsewhere, or was cancelled or deferred.</summary>
    public async Task<bool> AttemptAsync(Guid deliveryId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var now = Now;
        var delivery = await db.Set<IntegrationDelivery>().FirstOrDefaultAsync(d => d.Id == deliveryId, ct);
        if (delivery is not { Status: DeliveryStatus.Pending } || delivery.NextAttemptAtUtc > now)
        {
            return false;
        }

        var e = await db.Set<IntegrationOutboxEvent>().AsNoTracking().FirstAsync(x => x.Id == delivery.EventId, ct);
        WebhookEndpoint? endpoint = null;
        IntegrationConnector? connector = null;
        if (delivery.TargetKind == DeliveryTargetKind.Webhook)
        {
            endpoint = await db.Set<WebhookEndpoint>().FirstOrDefaultAsync(x => x.Id == delivery.EndpointId, ct);
            if (endpoint is not { IsActive: true })
            {
                return await CancelAsync(db, delivery, "The endpoint was turned off before this went out.", ct);
            }

            if (endpoint.Health == EndpointHealth.Unhealthy && endpoint.NextProbeAtUtc > now)
            {
                // Wait for the hourly probe instead of piling attempts on a receiver that is down.
                delivery.NextAttemptAtUtc = endpoint.NextProbeAtUtc.Value;
                await TrySaveAsync(db, ct);
                return false;
            }
        }
        else
        {
            connector = await db.Set<IntegrationConnector>().FirstOrDefaultAsync(x => x.Id == delivery.ConnectorId, ct);
            if (connector is not { IsActive: true })
            {
                return await CancelAsync(db, delivery, "The connector was turned off before this went out.", ct);
            }
        }

        // Claim the attempt (a crash after this retries it once the lease ends) and, while unhealthy, the probe slot.
        delivery.AttemptCount++;
        delivery.LastAttemptAtUtc = now;
        delivery.NextAttemptAtUtc = now.AddSeconds(Options.RequestTimeoutSeconds + 60);
        delivery.Version = Guid.NewGuid();
        if (endpoint is { Health: EndpointHealth.Unhealthy })
        {
            endpoint.NextProbeAtUtc = now + ProbeInterval;
        }

        if (!await TrySaveAsync(db, ct))
        {
            return false;
        }

        var (request, response, connectorResult) = endpoint is not null
            ? await SendWebhookAsync(endpoint, delivery, e, ct)
            : await SendConnectorAsync(connector!, e, ct);

        var finished = Now;
        var success = response?.IsSuccess ?? connectorResult?.Success ?? false;
        var error = response?.Error ?? (success ? null : connectorResult?.Message ?? $"The receiver answered {response?.StatusCode}.");
        if (!success && response is { StatusCode: { } code, Error: null })
        {
            error = $"The receiver answered {code}.";
        }

        db.Set<IntegrationDeliveryAttempt>().Add(new IntegrationDeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = delivery.AttemptCount,
            AttemptedAtUtc = now,
            DurationMs = response?.DurationMs ?? (int)(finished - now).TotalMilliseconds,
            RequestUrl = Clip(request?.Url.ToString() ?? "", 2000)!,
            RequestHeaders = Clip(string.Join("\n", (request?.Headers ?? []).Select(h => $"{h.Key}: {h.Value}").Prepend($"Content-Type: {request?.ContentType ?? "application/json"}")), 4000)!,
            RequestBody = Clip(request?.Body ?? "", MaxBodyChars)!,
            ResponseStatusCode = response?.StatusCode,
            ResponseHeaders = Clip(response?.Headers, 4000),
            ResponseBody = Clip(response?.Body, 4000),
            Error = Clip(error, 1000),
            Succeeded = success,
        });
        delivery.LastStatusCode = response?.StatusCode;
        delivery.LastError = Clip(error, 1000);
        delivery.Version = Guid.NewGuid();

        var retry = !success && RetryPolicy.IsRetryable(response?.StatusCode) && delivery.AttemptCount < Math.Max(1, Options.MaxAttempts);
        if (success)
        {
            delivery.Status = DeliveryStatus.Succeeded;
            delivery.CompletedAtUtc = finished;
        }
        else if (retry)
        {
            delivery.NextAttemptAtUtc = finished + RetryPolicy.DelayAfter(delivery.AttemptCount, delivery.Id);
        }
        else
        {
            delivery.Status = DeliveryStatus.Failed;
            delivery.CompletedAtUtc = finished;
        }

        var becameUnhealthy = endpoint is not null && Track(endpoint, success, finished);
        if (connector is not null)
        {
            var run = new ConnectorRun
            {
                ConnectorId = connector.Id, Trigger = ConnectorTrigger.Event, StartedAtUtc = now, FinishedAtUtc = finished, EventId = e.Id,
                Status = success ? ConnectorRunStatus.Succeeded : ConnectorRunStatus.Failed,
                Message = ConnectorRunner.Clip(success ? connectorResult?.Message ?? "Posted." : $"{error}{(retry ? " Will retry." : "")}", 1000),
                ItemCount = success ? 1 : 0,
            };
            db.Set<ConnectorRun>().Add(run);
            ConnectorRunner.Apply(connector, run, ConnectorTrigger.Event, finished);
        }

        await db.SaveChangesAsync(ct);
        if (becameUnhealthy && notifications is not null)
        {
            await notifications.PublishAsync(endpoint!.CreatedBy, "integration.webhook_unhealthy", $"Webhook \"{endpoint.Name}\" isn't accepting deliveries",
                $"{endpoint.ConsecutiveFailures} deliveries in a row failed. Workbench now tries once an hour; fix the receiver, then choose Resume.",
                link: "admin/integrations?tab=webhooks");
        }

        return true;
    }

    /// <summary>Counts a webhook outcome against the endpoint. Returns true when this failure made it unhealthy.</summary>
    private bool Track(WebhookEndpoint endpoint, bool success, DateTime now)
    {
        if (success)
        {
            endpoint.ConsecutiveFailures = 0;
            endpoint.Health = EndpointHealth.Healthy;
            endpoint.UnhealthySinceUtc = null;
            endpoint.NextProbeAtUtc = null;
            endpoint.LastSuccessAtUtc = now;
            return false;
        }

        endpoint.ConsecutiveFailures++;
        endpoint.LastFailureAtUtc = now;
        if (endpoint.Health == EndpointHealth.Healthy && endpoint.ConsecutiveFailures >= Math.Max(1, Options.UnhealthyAfterFailures))
        {
            endpoint.Health = EndpointHealth.Unhealthy;
            endpoint.UnhealthySinceUtc = now;
            endpoint.NextProbeAtUtc = now + ProbeInterval;
            return true;
        }

        return false;
    }

    private async Task<(OutboundRequest?, OutboundResponse?, ConnectorResult?)> SendWebhookAsync(
        WebhookEndpoint endpoint, IntegrationDelivery delivery, IntegrationOutboxEvent e, CancellationToken ct)
    {
        Uri url;
        try
        {
            url = OutboundUrlGuard.ParseShape(endpoint.Url, "The endpoint URL", options.Value.AllowHttp);
        }
        catch (ArgumentException ex)
        {
            return (null, new OutboundResponse(null, null, null, ex.Message, 0), null);
        }

        var timestamp = clock.GetUtcNow().ToUnixTimeSeconds();
        var secret = secrets.UnprotectSigningSecret(endpoint.SecretProtected);
        var request = new OutboundRequest(url, e.PayloadJson, "application/json",
        [
            new(WebhookSignature.EventHeader, e.EventType),
            new(WebhookSignature.EventIdHeader, e.Id.ToString()),
            new(WebhookSignature.DeliveryHeader, delivery.Id.ToString()),
            new(WebhookSignature.SignatureHeader, WebhookSignature.Header(secret, timestamp, e.PayloadJson)),
            new("Workbench-Attempt", delivery.AttemptCount.ToString(CultureInfo.InvariantCulture)),
        ]);
        var response = await transport.SendAsync(request, ct);
        return (request, response, null);
    }

    private async Task<(OutboundRequest?, OutboundResponse?, ConnectorResult?)> SendConnectorAsync(IntegrationConnector row, IntegrationOutboxEvent e, CancellationToken ct)
    {
        if (runner.Find(row.Kind) is not { } connector)
        {
            return (null, null, new ConnectorResult(false, $"This server doesn't have the {row.Kind} connector."));
        }

        try
        {
            var result = await connector.RunAsync(new ConnectorContext(row, runner.ConfigOf(row), ConnectorTrigger.Event, e, Now), ct);
            return (result.Request, result.Response, result);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or HttpRequestException)
        {
            return (null, null, new ConnectorResult(false, ex.Message));
        }
    }

    private async Task<bool> CancelAsync(IWorkbenchDbContext db, IntegrationDelivery delivery, string reason, CancellationToken ct)
    {
        delivery.Status = DeliveryStatus.Cancelled;
        delivery.LastError = reason;
        delivery.CompletedAtUtc = Now;
        delivery.Version = Guid.NewGuid();
        await TrySaveAsync(db, ct);
        return false;
    }

    private static async Task<bool> TrySaveAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // Another dispatcher claimed it.
        }
    }

    /// <summary>Runs scheduled connectors whose time has come. Returns how many ran.</summary>
    public async Task<int> RunDueConnectorsAsync(CancellationToken ct)
    {
        var now = Now;
        List<Guid> due;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            due = await db.Set<IntegrationConnector>().AsNoTracking()
                .Where(c => c.IsActive && c.Schedule != ConnectorSchedule.Manual && c.NextRunAtUtc != null && c.NextRunAtUtc <= now)
                .Select(c => c.Id).ToListAsync(ct);
        }

        var ran = 0;
        foreach (var id in due)
        {
            await using (var db = await dbFactory.CreateAsync(ct))
            {
                var row = await db.Set<IntegrationConnector>().FirstAsync(c => c.Id == id, ct);
                if (runner.Find(row.Kind)?.Mode != ConnectorMode.Schedule)
                {
                    row.NextRunAtUtc = null;
                    await TrySaveAsync(db, ct);
                    continue;
                }

                // Claim: push the next run out before running, so a second host (or a crash) can't repeat it.
                row.NextRunAtUtc = ConnectorScheduleMath.Next(row.Schedule, row.RunAtHourUtc, row.RunOnDay, now);
                row.Version = Guid.NewGuid();
                if (!await TrySaveAsync(db, ct))
                {
                    continue;
                }
            }

            await runner.RunAsync(id, ConnectorTrigger.Schedule, null, ct);
            ran++;
        }

        return ran;
    }

    /// <summary>Deletes finished deliveries, sent events and connector runs (with their files) older than the retention period.</summary>
    public async Task PruneAsync(CancellationToken ct)
    {
        var cutoff = Now.AddDays(-Math.Max(1, Options.RetentionDays));
        await using var db = await dbFactory.CreateAsync(ct);
        var deliveries = await db.Set<IntegrationDelivery>().Include(d => d.Attempts)
            .Where(d => d.Status != DeliveryStatus.Pending && d.CreatedAtUtc < cutoff).Take(200).ToListAsync(ct);
        db.Set<IntegrationDelivery>().RemoveRange(deliveries);
        var events = await db.Set<IntegrationOutboxEvent>()
            .Where(e => e.DispatchedAtUtc != null && e.OccurredAtUtc < cutoff
                && !db.Set<IntegrationDelivery>().Any(d => d.EventId == e.Id && (d.Status == DeliveryStatus.Pending || d.CreatedAtUtc >= cutoff)))
            .Take(200).ToListAsync(ct);
        db.Set<IntegrationOutboxEvent>().RemoveRange(events);
        var runs = await db.Set<ConnectorRun>().Where(r => r.StartedAtUtc < cutoff).Take(200).ToListAsync(ct);
        db.Set<ConnectorRun>().RemoveRange(runs);
        if (deliveries.Count + events.Count + runs.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(ct);
        foreach (var key in runs.Where(r => r.OutputBlobKey != null).Select(r => r.OutputBlobKey!))
        {
            await blobs.DeleteAsync(key, ct);
        }
    }

    internal static HashSet<string> Split(string? csv) =>
        (csv ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);

    /// <summary>Event keys from a stored list, in catalog order.</summary>
    internal static IReadOnlyList<string> Ordered(string? csv)
    {
        var set = Split(csv);
        return WebhookEventCatalog.All.Select(e => e.Key).Where(set.Contains).ToList();
    }

    private static string? Clip(string? text, int max) => text is null || text.Length <= max ? text : text[..max];
}

/// <summary>Runs the dispatcher every <see cref="IntegrationOptions.DispatchIntervalSeconds"/> in every tenant.</summary>
public sealed class IntegrationDispatcherBackgroundService(
    IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, IOptions<IntegrationOptions> options, TimeProvider clock, ILogger<IntegrationDispatcherBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.DispatcherEnabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(Math.Clamp(options.Value.DispatchIntervalSeconds, 2, 3600)), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var result = await scope.ServiceProvider.GetRequiredService<IntegrationDispatcher>().RunOnceAsync(ct);
                    if (result.Attempts + result.ConnectorRuns > 0)
                    {
                        logger.LogInformation("Integrations: {Events} events fanned out, {Attempts} delivery attempts, {Runs} connector runs",
                            result.EventsFannedOut, result.Attempts, result.ConnectorRuns);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Integration dispatch failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

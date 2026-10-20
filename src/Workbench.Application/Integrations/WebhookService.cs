using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations;

/// <summary>Webhook endpoints and the delivery log (Admin › Integrations). Administrators only (the permission decorator).</summary>
public sealed class WebhookService(
    IWorkbenchDbContextFactory dbFactory,
    OutboundUrlGuard guard,
    IntegrationSecrets secrets,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    TimeProvider clock) : IWebhookService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<WebhookEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<WebhookEndpoint>().AsNoTracking().OrderBy(e => e.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<WebhookEndpointDto> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await FindAsync(db, endpointId, cancellationToken));
    }

    public async Task<WebhookSecretDto> CreateEndpointAsync(SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var (name, url, events, description) = await ValidateAsync(request, cancellationToken);
        var secret = IntegrationSecrets.NewSigningSecret();
        var now = Now;
        var endpoint = new WebhookEndpoint
        {
            Name = name,
            Url = url,
            Description = description,
            EventTypes = string.Join(',', events),
            IsActive = request.IsActive,
            SecretProtected = secrets.ProtectSigningSecret(secret),
            SecretHint = secret[^4..],
            CreatedBy = Me,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<WebhookEndpoint>().Add(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.WebhookChanged, $"Webhook {name}", detail: $"Created: {Host(url)}; events {string.Join(", ", events)}", cancellationToken: cancellationToken);
        return new WebhookSecretDto(ToDto(endpoint), secret);
    }

    public async Task<WebhookEndpointDto> UpdateEndpointAsync(Guid endpointId, SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        var (name, url, events, description) = await ValidateAsync(request, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var endpoint = await FindAsync(db, endpointId, cancellationToken);
        var changedUrl = endpoint.Url != url;
        endpoint.Name = name;
        endpoint.Url = url;
        endpoint.Description = description;
        endpoint.EventTypes = string.Join(',', events);
        endpoint.IsActive = request.IsActive;
        endpoint.UpdatedBy = Me;
        endpoint.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.WebhookChanged, $"Webhook {name}",
            detail: $"Updated{(changedUrl ? $": now {Host(url)}" : "")}; {(request.IsActive ? "on" : "off")}; events {string.Join(", ", events)}", cancellationToken: cancellationToken);
        return ToDto(endpoint);
    }

    public async Task<WebhookSecretDto> RotateSecretAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var endpoint = await FindAsync(db, endpointId, cancellationToken);
        var secret = IntegrationSecrets.NewSigningSecret();
        endpoint.SecretProtected = secrets.ProtectSigningSecret(secret);
        endpoint.SecretHint = secret[^4..];
        endpoint.SecretRotatedAtUtc = Now;
        endpoint.UpdatedBy = Me;
        endpoint.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.WebhookChanged, $"Webhook {endpoint.Name}", detail: "Signing secret rotated", cancellationToken: cancellationToken);
        return new WebhookSecretDto(ToDto(endpoint), secret);
    }

    public async Task<WebhookEndpointDto> ResetHealthAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var endpoint = await FindAsync(db, endpointId, cancellationToken);
        endpoint.Health = EndpointHealth.Healthy;
        endpoint.ConsecutiveFailures = 0;
        endpoint.UnhealthySinceUtc = null;
        endpoint.NextProbeAtUtc = null;
        endpoint.UpdatedBy = Me;
        endpoint.UpdatedAtUtc = Now;

        // Waiting deliveries go out on the next run instead of at their backed-off times.
        var now = Now;
        var waiting = await db.Set<IntegrationDelivery>().Where(d => d.EndpointId == endpointId && d.Status == DeliveryStatus.Pending).ToListAsync(cancellationToken);
        foreach (var d in waiting)
        {
            d.NextAttemptAtUtc = now;
            d.Version = Guid.NewGuid();
        }

        await db.SaveChangesAsync(cancellationToken);
        return ToDto(endpoint);
    }

    public async Task DeleteEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var endpoint = await FindAsync(db, endpointId, cancellationToken);
        var deliveries = await db.Set<IntegrationDelivery>().Include(d => d.Attempts).Where(d => d.EndpointId == endpointId).ToListAsync(cancellationToken);
        db.Set<IntegrationDelivery>().RemoveRange(deliveries);
        db.Set<WebhookEndpoint>().Remove(endpoint);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.WebhookChanged, $"Webhook {endpoint.Name}", detail: "Deleted", cancellationToken: cancellationToken);
    }

    public async Task<DeliverySummaryDto> SendTestAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var endpoint = await FindAsync(db, endpointId, cancellationToken);
        if (!endpoint.IsActive)
        {
            throw new ArgumentException("Turn the endpoint on before sending a test.");
        }

        var now = Now;
        var ping = IntegrationOutbox.Build(IntegrationOutbox.PingEventType, "WebhookEndpoint", endpoint.Id,
            new { message = "Test delivery from Workbench. No action is needed.", endpointId = endpoint.Id }, Me, now);
        ping.DispatchedAtUtc = now;
        ping.DeliveryCount = 1;
        var delivery = IntegrationDispatcher.NewDelivery(ping, DeliveryTargetKind.Webhook, endpoint.Id, now, requestedBy: Me);
        db.Set<IntegrationOutboxEvent>().Add(ping);
        db.Set<IntegrationDelivery>().Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(delivery, endpoint.Name);
    }

    public async Task<IReadOnlyList<DeliverySummaryDto>> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<IntegrationDelivery>().AsNoTracking();
        if (query.EndpointId is { } endpointId)
        {
            q = q.Where(d => d.EndpointId == endpointId);
        }

        if (query.ConnectorId is { } connectorId)
        {
            q = q.Where(d => d.ConnectorId == connectorId);
        }

        if (query.Status is { } status)
        {
            var s = (DeliveryStatus)(int)status;
            q = q.Where(d => d.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(query.EventType))
        {
            var type = query.EventType.Trim().ToLowerInvariant();
            q = q.Where(d => d.EventType == type);
        }

        var rows = await q.OrderByDescending(d => d.CreatedAtUtc).Take(Math.Clamp(query.Take, 1, 500)).ToListAsync(cancellationToken);
        var names = await TargetNamesAsync(db, rows, cancellationToken);
        return rows.Select(d => ToSummary(d, names.GetValueOrDefault((d.EndpointId ?? d.ConnectorId)!.Value, "(removed)"))).ToList();
    }

    public async Task<DeliveryDetailDto> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var delivery = await db.Set<IntegrationDelivery>().AsNoTracking().Include(d => d.Attempts).FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Delivery", deliveryId);
        var e = await db.Set<IntegrationOutboxEvent>().AsNoTracking().FirstAsync(x => x.Id == delivery.EventId, cancellationToken);
        var names = await TargetNamesAsync(db, [delivery], cancellationToken);
        return new DeliveryDetailDto(
            ToSummary(delivery, names.GetValueOrDefault((delivery.EndpointId ?? delivery.ConnectorId)!.Value, "(removed)")),
            e.PayloadJson,
            delivery.Attempts.OrderBy(a => a.AttemptNumber).Select(a => new DeliveryAttemptDto(a.AttemptNumber, a.AttemptedAtUtc, a.DurationMs, a.RequestUrl,
                a.RequestHeaders, a.RequestBody, a.ResponseStatusCode, a.ResponseHeaders, a.ResponseBody, a.Error, a.Succeeded)).ToList());
    }

    public async Task<DeliverySummaryDto> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var original = await db.Set<IntegrationDelivery>().AsNoTracking().FirstOrDefaultAsync(d => d.Id == deliveryId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Delivery", deliveryId);
        if (original.Status == DeliveryStatus.Pending)
        {
            throw new ArgumentException("This delivery is still being tried; wait for it to finish before sending it again.");
        }

        var targetId = (original.EndpointId ?? original.ConnectorId)!.Value;
        var names = await TargetNamesAsync(db, [original], cancellationToken);
        if (!names.ContainsKey(targetId))
        {
            throw new ArgumentException("The endpoint or connector this went to was removed.");
        }

        var e = await db.Set<IntegrationOutboxEvent>().AsNoTracking().FirstAsync(x => x.Id == original.EventId, cancellationToken);
        var delivery = IntegrationDispatcher.NewDelivery(e, original.TargetKind, targetId, Now, original.Id, Me);
        db.Set<IntegrationDelivery>().Add(delivery);
        await db.SaveChangesAsync(cancellationToken);
        return ToSummary(delivery, names[targetId]);
    }

    private async Task<(string Name, string Url, IReadOnlyList<string> Events, string? Description)> ValidateAsync(SaveWebhookEndpointRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ArgumentException("Give the endpoint a name (200 characters at most).");
        }

        var url = (await guard.ValidateAsync(request.Url, "The endpoint URL", ct)).ToString();
        var events = WebhookEventCatalog.Normalize(request.EventTypes);
        var description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim();
        if (description?.Length > 1000)
        {
            throw new ArgumentException("The description is too long (1,000 characters at most).");
        }

        return (name, url, events, description);
    }

    private static async Task<WebhookEndpoint> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<WebhookEndpoint>().FirstOrDefaultAsync(e => e.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Webhook endpoint", id);

    private static async Task<Dictionary<Guid, string>> TargetNamesAsync(IWorkbenchDbContext db, IReadOnlyCollection<IntegrationDelivery> rows, CancellationToken ct)
    {
        var endpointIds = rows.Where(d => d.EndpointId != null).Select(d => d.EndpointId!.Value).Distinct().ToList();
        var connectorIds = rows.Where(d => d.ConnectorId != null).Select(d => d.ConnectorId!.Value).Distinct().ToList();
        var names = await db.Set<WebhookEndpoint>().AsNoTracking().Where(e => endpointIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Name, ct);
        foreach (var c in await db.Set<IntegrationConnector>().AsNoTracking().Where(c => connectorIds.Contains(c.Id)).Select(c => new { c.Id, c.Name }).ToListAsync(ct))
        {
            names[c.Id] = c.Name;
        }

        return names;
    }

    private static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out var u) ? u.Host : url;

    internal static WebhookEndpointDto ToDto(WebhookEndpoint e) => new(
        e.Id, e.Name, e.Url, e.Description, IntegrationDispatcher.Ordered(e.EventTypes), e.IsActive, e.SecretHint,
        (WebhookHealthState)(int)e.Health, e.ConsecutiveFailures, e.UnhealthySinceUtc, e.LastSuccessAtUtc, e.LastFailureAtUtc, e.CreatedBy, e.CreatedAtUtc, e.UpdatedAtUtc);

    internal static DeliverySummaryDto ToSummary(IntegrationDelivery d, string targetName) => new(
        d.Id, (DeliveryTarget)(int)d.TargetKind, d.EndpointId, d.ConnectorId, targetName, d.EventId, d.EventType, (DeliveryState)(int)d.Status, d.AttemptCount,
        d.CreatedAtUtc, d.Status == DeliveryStatus.Pending ? d.NextAttemptAtUtc : null, d.LastAttemptAtUtc, d.LastStatusCode, d.LastError, d.RedeliveryOfId);
}

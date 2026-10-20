namespace Workbench.Domain.Integrations;

/// <summary>
/// The transactional outbox: one row per event, written in the same unit of work as the change it describes, so an event
/// is sent if and only if the change was saved. The dispatcher fans each row out to deliveries once (<see cref="DispatchedAtUtc"/>).
/// </summary>
public sealed class IntegrationOutboxEvent : Entity
{
    /// <summary>An <see cref="IntegrationEventType.Key"/>.</summary>
    public required string EventType { get; set; }

    public required string Subject { get; set; }
    public Guid SubjectId { get; set; }

    /// <summary>The complete JSON body receivers get (envelope and data), fixed when the event is raised.</summary>
    public required string PayloadJson { get; set; }

    public string? Actor { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>How many deliveries the fan-out created (0 when nothing subscribed).</summary>
    public int DeliveryCount { get; set; }
}

public enum EndpointHealth
{
    Healthy,
    Unhealthy,
}

/// <summary>An admin-registered HTTPS receiver for chosen events, signed with its own HMAC-SHA256 secret.</summary>
public sealed class WebhookEndpoint : Entity
{
    public required string Name { get; set; }
    public required string Url { get; set; }
    public string? Description { get; set; }

    /// <summary>Event type keys, comma-separated, in catalog order.</summary>
    public required string EventTypes { get; set; }

    public bool IsActive { get; set; } = true;

    /// <summary>The signing secret, encrypted with Data Protection (<c>Workbench.Integrations.WebhookSecret.v1</c>). Shown once.</summary>
    public required string SecretProtected { get; set; }

    /// <summary>The secret's last four characters, so admins can tell which one a receiver has.</summary>
    public required string SecretHint { get; set; }

    public DateTime? SecretRotatedAtUtc { get; set; }

    public EndpointHealth Health { get; set; } = EndpointHealth.Healthy;
    public int ConsecutiveFailures { get; set; }
    public DateTime? UnhealthySinceUtc { get; set; }

    /// <summary>While unhealthy, one delivery is tried at this time (a probe); the rest wait for it.</summary>
    public DateTime? NextProbeAtUtc { get; set; }

    public DateTime? LastSuccessAtUtc { get; set; }
    public DateTime? LastFailureAtUtc { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum DeliveryTargetKind
{
    Webhook,
    Connector,
}

public enum DeliveryStatus
{
    Pending,
    Succeeded,

    /// <summary>Gave up: the last attempt failed and no retries are left (or the failure isn't worth retrying).</summary>
    Failed,

    /// <summary>The endpoint or connector was turned off or removed before it went out.</summary>
    Cancelled,
}

/// <summary>One event to one target (a webhook endpoint or an event connector such as Teams), retried with backoff.</summary>
public sealed class IntegrationDelivery : Entity
{
    public DeliveryTargetKind TargetKind { get; set; }
    public Guid? EndpointId { get; set; }
    public Guid? ConnectorId { get; set; }
    public Guid EventId { get; set; }
    public required string EventType { get; set; }

    /// <summary>Unique: <c>{event}:{target}</c> for the fan-out (so two dispatchers can't both create it), random for redeliveries.</summary>
    public required string DedupKey { get; set; }

    public DeliveryStatus Status { get; set; } = DeliveryStatus.Pending;
    public int AttemptCount { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime NextAttemptAtUtc { get; set; }
    public DateTime? LastAttemptAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public int? LastStatusCode { get; set; }
    public string? LastError { get; set; }

    /// <summary>Set when an admin pressed "Redeliver": the delivery this one repeats.</summary>
    public Guid? RedeliveryOfId { get; set; }

    public string? RequestedBy { get; set; }

    /// <summary>Changes on every claim, so two dispatchers never send the same attempt.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    public List<IntegrationDeliveryAttempt> Attempts { get; } = [];
}

/// <summary>The delivery log: what was sent and what came back, per attempt. Bodies are truncated.</summary>
public sealed class IntegrationDeliveryAttempt : Entity
{
    public Guid DeliveryId { get; set; }
    public int AttemptNumber { get; set; }
    public DateTime AttemptedAtUtc { get; set; }
    public int DurationMs { get; set; }
    public required string RequestUrl { get; set; }

    /// <summary>Request headers, one <c>Name: value</c> per line (signatures included; never the secret).</summary>
    public required string RequestHeaders { get; set; }

    public required string RequestBody { get; set; }
    public int? ResponseStatusCode { get; set; }
    public string? ResponseHeaders { get; set; }
    public string? ResponseBody { get; set; }
    public string? Error { get; set; }
    public bool Succeeded { get; set; }
}

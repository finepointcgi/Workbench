using Workbench.Contracts.Identity;

namespace Workbench.Contracts.Integrations;

// Admin › Integrations: outbound webhooks and their delivery log, API keys for the REST API, and prebuilt connectors.
// Every service here is for administrators only (Identity/IntegrationPermissionDecorators.cs).

/// <summary>An event Workbench can send. Keys match the domain catalog (<c>Domain.Integrations.IntegrationEventType</c>).</summary>
public sealed record WebhookEventInfo(string Key, string Label, string Group, string Description);

public static class WebhookEventCatalog
{
    public static IReadOnlyList<WebhookEventInfo> All { get; } =
    [
        new("timesheet.submitted", "Timesheet submitted", "Time", "An employee certified and submitted a timesheet."),
        new("timesheet.approved", "Timesheet approved", "Time", "A timesheet finished its approval (every step approved)."),
        new("invoice.approved", "Invoice approved", "Billing", "An invoice or credit memo was approved and posted to the ledger."),
        new("invoice.sent", "Invoice sent", "Billing", "An approved invoice was marked as sent to the customer."),
        new("invoice.paid", "Invoice paid", "Billing", "Receipts paid an invoice in full."),
        new("bill.approved", "Bill approved", "Purchasing", "A vendor bill finished its approval."),
        new("bill.paid", "Bill paid", "Purchasing", "A payment run paid a vendor bill in full."),
        new("purchase_order.approved", "Purchase order approved", "Purchasing", "A purchase order was approved and issued to the vendor."),
        new("project.created", "Project created", "Projects", "A project was set up (by hand or from a won opportunity)."),
        new("employee.hired", "Employee hired", "People", "An employee record was added."),
        new("employee.terminated", "Employee terminated", "People", "An employee's termination date was set or changed."),
        new("opportunity.stage_changed", "Opportunity stage changed", "Business development", "An opportunity moved to another stage (including won, lost and no bid)."),
        new("period.closed", "Period closed", "Accounting", "A fiscal period was closed."),
    ];

    public static bool IsKnown(string key) => All.Any(e => e.Key == key);

    /// <summary>Validates and orders event keys; at least one is required.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string>? keys)
    {
        var wanted = (keys ?? []).Select(k => k.Trim().ToLowerInvariant()).Where(k => k.Length > 0).ToHashSet();
        if (wanted.FirstOrDefault(k => !IsKnown(k)) is { } unknown)
        {
            throw new ArgumentException($"'{unknown}' isn't an event Workbench sends.");
        }

        if (wanted.Count == 0)
        {
            throw new ArgumentException("Choose at least one event.");
        }

        return All.Where(e => wanted.Contains(e.Key)).Select(e => e.Key).ToList();
    }
}

public enum WebhookHealthState
{
    Healthy,
    Unhealthy,
}

public sealed record WebhookEndpointDto(
    Guid Id,
    string Name,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool IsActive,
    string SecretHint,
    WebhookHealthState Health,
    int ConsecutiveFailures,
    DateTime? UnhealthySinceUtc,
    DateTime? LastSuccessAtUtc,
    DateTime? LastFailureAtUtc,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc);

public sealed record SaveWebhookEndpointRequest(string Name, string Url, IReadOnlyList<string> EventTypes, bool IsActive = true, string? Description = null);

/// <summary>Returned once, when an endpoint is created or its secret rotated: the only time the secret is shown.</summary>
public sealed record WebhookSecretDto(WebhookEndpointDto Endpoint, string SigningSecret);

public enum DeliveryState
{
    Pending,
    Succeeded,
    Failed,
    Cancelled,
}

public enum DeliveryTarget
{
    Webhook,
    Connector,
}

public sealed record DeliverySummaryDto(
    Guid Id,
    DeliveryTarget TargetKind,
    Guid? EndpointId,
    Guid? ConnectorId,
    string TargetName,
    Guid EventId,
    string EventType,
    DeliveryState Status,
    int AttemptCount,
    DateTime CreatedAtUtc,
    DateTime? NextAttemptAtUtc,
    DateTime? LastAttemptAtUtc,
    int? LastStatusCode,
    string? LastError,
    Guid? RedeliveryOfId);

public sealed record DeliveryAttemptDto(
    int AttemptNumber,
    DateTime AttemptedAtUtc,
    int DurationMs,
    string RequestUrl,
    string RequestHeaders,
    string RequestBody,
    int? ResponseStatusCode,
    string? ResponseHeaders,
    string? ResponseBody,
    string? Error,
    bool Succeeded);

public sealed record DeliveryDetailDto(DeliverySummaryDto Delivery, string PayloadJson, IReadOnlyList<DeliveryAttemptDto> Attempts);

public sealed record DeliveryQuery(Guid? EndpointId = null, Guid? ConnectorId = null, DeliveryState? Status = null, string? EventType = null, int Take = 100);

public interface IWebhookService
{
    IReadOnlyList<WebhookEventInfo> ListEventTypes() => WebhookEventCatalog.All;

    Task<IReadOnlyList<WebhookEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default);

    Task<WebhookEndpointDto> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Registers an endpoint. The URL must be https (outside Development) and must not point at a private or internal address.</summary>
    Task<WebhookSecretDto> CreateEndpointAsync(SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default);

    Task<WebhookEndpointDto> UpdateEndpointAsync(Guid endpointId, SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default);

    /// <summary>Replaces the signing secret at once; deliveries from now on are signed with the new one.</summary>
    Task<WebhookSecretDto> RotateSecretAsync(Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Marks an unhealthy endpoint healthy again so its waiting deliveries go out on the next run.</summary>
    Task<WebhookEndpointDto> ResetHealthAsync(Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Removes the endpoint with its delivery log.</summary>
    Task DeleteEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default);

    /// <summary>Queues a <c>ping</c> delivery to the endpoint; it goes out with the next dispatcher run.</summary>
    Task<DeliverySummaryDto> SendTestAsync(Guid endpointId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DeliverySummaryDto>> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default);

    Task<DeliveryDetailDto> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default);

    /// <summary>Queues a new delivery of the same event (same payload, fresh signature) to the same target.</summary>
    Task<DeliverySummaryDto> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default);
}

// ---- API keys ----

public sealed record ApiKeyDto(
    Guid Id,
    string Name,
    string? Description,
    string Prefix,
    OrgRoles Roles,
    IReadOnlyList<string> Scopes,
    string ServiceUserEmail,
    string CreatedBy,
    DateTime CreatedAtUtc,
    DateTime? ExpiresAtUtc,
    DateTime? RevokedAtUtc,
    string? RevokedBy,
    DateTime? LastUsedAtUtc,
    string? LastUsedFrom,
    bool IsActive);

/// <param name="Roles">The service user's organization roles (Employee is implied).</param>
/// <param name="Scopes">Areas and access, such as <c>time:read</c> and <c>billing:write</c> (<see cref="ApiKeyScopes"/>).</param>
public sealed record CreateApiKeyRequest(string Name, OrgRoles Roles, IReadOnlyList<string> Scopes, DateTime? ExpiresAtUtc = null, string? Description = null);

/// <summary>Returned once, when a key is created: the only time the key itself is shown.</summary>
public sealed record ApiKeyCreatedDto(ApiKeyDto ApiKey, string Key);

public sealed record RevokeApiKeyRequest(string? Reason);

public interface IApiKeyService
{
    IReadOnlyList<ApiKeyAreaDto> ListAreas() => ApiKeyScopes.Areas;

    Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ApiKeyCreatedDto> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default);

    /// <summary>Revokes the key at once (its service user is deactivated too). Revoked keys can't be restored.</summary>
    Task<ApiKeyDto> RevokeAsync(Guid apiKeyId, RevokeApiKeyRequest request, CancellationToken cancellationToken = default);
}

// ---- Connectors ----

public enum ConnectorMode
{
    /// <summary>Posts subscribed events as they happen (Teams, Slack).</summary>
    Events,

    /// <summary>Runs on a schedule or on demand and produces a file (GL export, report export).</summary>
    Schedule,
}

public enum ConnectorFieldKind
{
    Text,
    Secret,
    Select,
    Number,
    Date,
    MultiSelect,
}

public sealed record ConnectorOptionDto(string Value, string Label);

public sealed record ConnectorFieldDto(string Key, string Label, ConnectorFieldKind Kind, bool Required, string? Help = null,
    IReadOnlyList<ConnectorOptionDto>? Options = null, string? DefaultValue = null);

public sealed record ConnectorKindDto(string Kind, string Name, string Description, ConnectorMode Mode, IReadOnlyList<ConnectorFieldDto> Fields);

public enum ConnectorScheduleKind
{
    Manual,
    Hourly,
    Daily,
    Weekly,
    Monthly,
}

public enum ConnectorRunState
{
    Succeeded,
    Failed,
    Skipped,
}

public enum ConnectorRunTrigger
{
    Manual,
    Schedule,
    Event,
}

/// <param name="Settings">Non-secret settings by field key.</param>
/// <param name="SecretsSet">Secret fields that have a value (the values are never returned).</param>
public sealed record ConnectorDto(
    Guid Id,
    string Kind,
    string KindName,
    string Name,
    bool IsActive,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyList<string> SecretsSet,
    IReadOnlyList<string> EventTypes,
    ConnectorScheduleKind Schedule,
    int RunAtHourUtc,
    int RunOnDay,
    DateTime? NextRunAtUtc,
    DateTime? LastRunAtUtc,
    string? LastStatus,
    string CreatedBy,
    DateTime CreatedAtUtc);

/// <param name="Secrets">Secret fields to set; a field left out (or null) keeps its stored value, an empty string clears it.</param>
public sealed record SaveConnectorRequest(
    string Kind,
    string Name,
    IReadOnlyDictionary<string, string>? Settings,
    IReadOnlyDictionary<string, string?>? Secrets = null,
    IReadOnlyList<string>? EventTypes = null,
    ConnectorScheduleKind Schedule = ConnectorScheduleKind.Manual,
    int RunAtHourUtc = 6,
    int RunOnDay = 1,
    bool IsActive = true);

public sealed record ConnectorRunDto(
    Guid Id,
    Guid ConnectorId,
    ConnectorRunTrigger Trigger,
    ConnectorRunState Status,
    DateTime StartedAtUtc,
    DateTime FinishedAtUtc,
    string Message,
    int ItemCount,
    string? EventType,
    string? RequestedBy,
    string? OutputFileName,
    long? OutputBytes);

public sealed record ConnectorFileDto(string FileName, string ContentType, byte[] Content);

public interface IConnectorService
{
    Task<IReadOnlyList<ConnectorKindDto>> ListKindsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<ConnectorDto> GetAsync(Guid connectorId, CancellationToken cancellationToken = default);

    Task<ConnectorDto> CreateAsync(SaveConnectorRequest request, CancellationToken cancellationToken = default);

    Task<ConnectorDto> UpdateAsync(Guid connectorId, SaveConnectorRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid connectorId, CancellationToken cancellationToken = default);

    /// <summary>Runs the connector now: a file connector produces and sends its file; a chat connector posts a test message.</summary>
    Task<ConnectorRunDto> RunNowAsync(Guid connectorId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConnectorRunDto>> ListRunsAsync(Guid connectorId, int take = 50, CancellationToken cancellationToken = default);

    /// <summary>The copy of a file a run produced (kept in Workbench), for download.</summary>
    Task<ConnectorFileDto> GetRunFileAsync(Guid runId, CancellationToken cancellationToken = default);
}

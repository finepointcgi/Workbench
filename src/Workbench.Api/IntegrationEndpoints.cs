using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Integrations;

namespace Workbench.Api;

/// <summary>
/// Admin › Integrations over HTTP: webhooks and deliveries, API keys, connectors. Administrators only (the services check);
/// API keys are never allowed here (no key scope covers <c>/integrations</c>).
/// </summary>
internal static class IntegrationEndpoints
{
    public static void MapIntegrationEndpoints(this RouteGroupBuilder v1)
    {
        var g = v1.MapGroup("/integrations").WithTags("Integrations").AddEndpointFilter(MapConflicts);

        g.MapGet("/events", (IWebhookService s) => s.ListEventTypes());
        g.MapGet("/webhooks", (IWebhookService s, CancellationToken ct) => s.ListEndpointsAsync(ct));
        g.MapPost("/webhooks", (SaveWebhookEndpointRequest r, IWebhookService s, CancellationToken ct) => s.CreateEndpointAsync(r, ct));
        g.MapGet("/webhooks/{id:guid}", (Guid id, IWebhookService s, CancellationToken ct) => s.GetEndpointAsync(id, ct));
        g.MapPut("/webhooks/{id:guid}", (Guid id, SaveWebhookEndpointRequest r, IWebhookService s, CancellationToken ct) => s.UpdateEndpointAsync(id, r, ct));
        g.MapDelete("/webhooks/{id:guid}", async (Guid id, IWebhookService s, CancellationToken ct) =>
        {
            await s.DeleteEndpointAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/webhooks/{id:guid}/rotate-secret", (Guid id, IWebhookService s, CancellationToken ct) => s.RotateSecretAsync(id, ct));
        g.MapPost("/webhooks/{id:guid}/reset-health", (Guid id, IWebhookService s, CancellationToken ct) => s.ResetHealthAsync(id, ct));
        g.MapPost("/webhooks/{id:guid}/test", (Guid id, IWebhookService s, CancellationToken ct) => s.SendTestAsync(id, ct));
        g.MapGet("/deliveries", (Guid? endpointId, Guid? connectorId, DeliveryState? status, string? eventType, int? take, IWebhookService s, CancellationToken ct) =>
            s.ListDeliveriesAsync(new DeliveryQuery(endpointId, connectorId, status, eventType, take ?? 100), ct));
        g.MapGet("/deliveries/{id:guid}", (Guid id, IWebhookService s, CancellationToken ct) => s.GetDeliveryAsync(id, ct));
        g.MapPost("/deliveries/{id:guid}/redeliver", (Guid id, IWebhookService s, CancellationToken ct) => s.RedeliverAsync(id, ct));

        g.MapGet("/api-key-areas", (IApiKeyService s) => s.ListAreas());
        g.MapGet("/api-keys", (IApiKeyService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/api-keys", (CreateApiKeyRequest r, IApiKeyService s, CancellationToken ct) => s.CreateAsync(r, ct));
        g.MapPost("/api-keys/{id:guid}/revoke", (Guid id, RevokeApiKeyRequest r, IApiKeyService s, CancellationToken ct) => s.RevokeAsync(id, r, ct));

        g.MapGet("/connector-kinds", (IConnectorService s, CancellationToken ct) => s.ListKindsAsync(ct));
        g.MapGet("/connectors", (IConnectorService s, CancellationToken ct) => s.ListAsync(ct));
        g.MapPost("/connectors", (SaveConnectorRequest r, IConnectorService s, CancellationToken ct) => s.CreateAsync(r, ct));
        g.MapGet("/connectors/{id:guid}", (Guid id, IConnectorService s, CancellationToken ct) => s.GetAsync(id, ct));
        g.MapPut("/connectors/{id:guid}", (Guid id, SaveConnectorRequest r, IConnectorService s, CancellationToken ct) => s.UpdateAsync(id, r, ct));
        g.MapDelete("/connectors/{id:guid}", async (Guid id, IConnectorService s, CancellationToken ct) =>
        {
            await s.DeleteAsync(id, ct);
            return Results.NoContent();
        });
        g.MapPost("/connectors/{id:guid}/run", (Guid id, IConnectorService s, CancellationToken ct) => s.RunNowAsync(id, ct));
        g.MapGet("/connectors/{id:guid}/runs", (Guid id, int? take, IConnectorService s, CancellationToken ct) => s.ListRunsAsync(id, take ?? 50, ct));
        g.MapGet("/connector-runs/{id:guid}/file", async (Guid id, IConnectorService s, CancellationToken ct) =>
        {
            var file = await s.GetRunFileAsync(id, ct);
            return Results.File(file.Content, file.ContentType, file.FileName);
        });
    }

    private static async ValueTask<object?> MapConflicts(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        try
        {
            return await next(context);
        }
        catch (InvalidOperationException ex)
        {
            return Results.Problem(ex.Message, statusCode: StatusCodes.Status409Conflict);
        }
    }
}

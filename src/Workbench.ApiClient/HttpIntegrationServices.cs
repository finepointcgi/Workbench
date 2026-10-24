using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Integrations;

namespace Workbench.ApiClient;

public sealed class HttpWebhookService(HttpClient http) : IWebhookService
{
    private const string Base = "api/v1/integrations";

    public async Task<IReadOnlyList<WebhookEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/webhooks", cancellationToken)).ReadLedgerAsync<List<WebhookEndpointDto>>(cancellationToken);

    public async Task<WebhookEndpointDto> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/webhooks/{endpointId}", cancellationToken)).ReadLedgerAsync<WebhookEndpointDto>(cancellationToken);

    public async Task<WebhookSecretDto> CreateEndpointAsync(SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/webhooks", request, cancellationToken)).ReadLedgerAsync<WebhookSecretDto>(cancellationToken);

    public async Task<WebhookEndpointDto> UpdateEndpointAsync(Guid endpointId, SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/webhooks/{endpointId}", request, cancellationToken)).ReadLedgerAsync<WebhookEndpointDto>(cancellationToken);

    public async Task<WebhookSecretDto> RotateSecretAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/webhooks/{endpointId}/rotate-secret", null, cancellationToken)).ReadLedgerAsync<WebhookSecretDto>(cancellationToken);

    public async Task<WebhookEndpointDto> ResetHealthAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/webhooks/{endpointId}/reset-health", null, cancellationToken)).ReadLedgerAsync<WebhookEndpointDto>(cancellationToken);

    public async Task DeleteEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/webhooks/{endpointId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<DeliverySummaryDto> SendTestAsync(Guid endpointId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/webhooks/{endpointId}/test", null, cancellationToken)).ReadLedgerAsync<DeliverySummaryDto>(cancellationToken);

    public async Task<IReadOnlyList<DeliverySummaryDto>> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(LedgerUrls.With($"{Base}/deliveries", ("endpointId", query.EndpointId), ("connectorId", query.ConnectorId), ("status", query.Status),
            ("eventType", query.EventType), ("take", query.Take)), cancellationToken)).ReadLedgerAsync<List<DeliverySummaryDto>>(cancellationToken);

    public async Task<DeliveryDetailDto> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/deliveries/{deliveryId}", cancellationToken)).ReadLedgerAsync<DeliveryDetailDto>(cancellationToken);

    public async Task<DeliverySummaryDto> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/deliveries/{deliveryId}/redeliver", null, cancellationToken)).ReadLedgerAsync<DeliverySummaryDto>(cancellationToken);
}

public sealed class HttpApiKeyService(HttpClient http) : IApiKeyService
{
    private const string Base = "api/v1/integrations/api-keys";

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadLedgerAsync<List<ApiKeyDto>>(cancellationToken);

    public async Task<ApiKeyCreatedDto> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Base, request, cancellationToken)).ReadLedgerAsync<ApiKeyCreatedDto>(cancellationToken);

    public async Task<ApiKeyDto> RevokeAsync(Guid apiKeyId, RevokeApiKeyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/{apiKeyId}/revoke", request, cancellationToken)).ReadLedgerAsync<ApiKeyDto>(cancellationToken);
}

public sealed class HttpConnectorService(HttpClient http) : IConnectorService
{
    private const string Base = "api/v1/integrations";

    public async Task<IReadOnlyList<ConnectorKindDto>> ListKindsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connector-kinds", cancellationToken)).ReadLedgerAsync<List<ConnectorKindDto>>(cancellationToken);

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connectors", cancellationToken)).ReadLedgerAsync<List<ConnectorDto>>(cancellationToken);

    public async Task<ConnectorDto> GetAsync(Guid connectorId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connectors/{connectorId}", cancellationToken)).ReadLedgerAsync<ConnectorDto>(cancellationToken);

    public async Task<ConnectorDto> CreateAsync(SaveConnectorRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base}/connectors", request, cancellationToken)).ReadLedgerAsync<ConnectorDto>(cancellationToken);

    public async Task<ConnectorDto> UpdateAsync(Guid connectorId, SaveConnectorRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base}/connectors/{connectorId}", request, cancellationToken)).ReadLedgerAsync<ConnectorDto>(cancellationToken);

    public async Task DeleteAsync(Guid connectorId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base}/connectors/{connectorId}", cancellationToken)).EnsureLedgerOkAsync(cancellationToken);

    public async Task<ConnectorRunDto> RunNowAsync(Guid connectorId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/connectors/{connectorId}/run", null, cancellationToken)).ReadLedgerAsync<ConnectorRunDto>(cancellationToken);

    public async Task<IReadOnlyList<ConnectorRunDto>> ListRunsAsync(Guid connectorId, int take = 50, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Base}/connectors/{connectorId}/runs?take={take}", cancellationToken)).ReadLedgerAsync<List<ConnectorRunDto>>(cancellationToken);

    public async Task<ConnectorFileDto> GetRunFileAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"{Base}/connector-runs/{runId}/file", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
        var name = response.Content.Headers.ContentDisposition?.FileNameStar ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? "export";
        return new ConnectorFileDto(name, response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream", await response.Content.ReadAsByteArrayAsync(cancellationToken));
    }
}

public static class IntegrationClientRegistration
{
    public static IServiceCollection AddIntegrationClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IWebhookService, HttpWebhookService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IApiKeyService, HttpApiKeyService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IConnectorService, HttpConnectorService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.Integrations.GitLab.IGitLabConnectionService, HttpGitLabConnectionService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<Contracts.Integrations.GitLab.IGitLabProjectSyncService, HttpGitLabProjectSyncService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;

namespace Workbench.Application.Identity;

// Integrations (webhooks, API keys, connectors): administrators only, and never an API key's service user, so a key can't
// mint keys or point webhooks elsewhere even if it was given the Admin role.

public static class IntegrationPermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchIntegrationPermissions(this IServiceCollection services)
    {
        services.Decorate<IWebhookService, PermissionCheckingWebhookService>();
        services.Decorate<IApiKeyService, PermissionCheckingApiKeyService>();
        services.Decorate<IConnectorService, PermissionCheckingConnectorService>();
        return services;
    }

    internal static async Task RequireIntegrationAdminAsync(this AccessControl access, CancellationToken ct)
    {
        access.RequireSignedIn();
        if (ApiKeyPrincipal.IsServiceAccount(access.Email))
        {
            throw new WorkbenchForbiddenException("API keys can't manage integrations.");
        }

        await access.RequireAsync(Permission.AdminIntegrations, "manage integrations", ct);
    }
}

public sealed class PermissionCheckingWebhookService(IWebhookService inner, AccessControl access) : IWebhookService
{
    public async Task<IReadOnlyList<WebhookEndpointDto>> ListEndpointsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListEndpointsAsync(cancellationToken);
    }

    public async Task<WebhookEndpointDto> GetEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetEndpointAsync(endpointId, cancellationToken);
    }

    public async Task<WebhookSecretDto> CreateEndpointAsync(SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.CreateEndpointAsync(request, cancellationToken);
    }

    public async Task<WebhookEndpointDto> UpdateEndpointAsync(Guid endpointId, SaveWebhookEndpointRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.UpdateEndpointAsync(endpointId, request, cancellationToken);
    }

    public async Task<WebhookSecretDto> RotateSecretAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.RotateSecretAsync(endpointId, cancellationToken);
    }

    public async Task<WebhookEndpointDto> ResetHealthAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ResetHealthAsync(endpointId, cancellationToken);
    }

    public async Task DeleteEndpointAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await inner.DeleteEndpointAsync(endpointId, cancellationToken);
    }

    public async Task<DeliverySummaryDto> SendTestAsync(Guid endpointId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.SendTestAsync(endpointId, cancellationToken);
    }

    public async Task<IReadOnlyList<DeliverySummaryDto>> ListDeliveriesAsync(DeliveryQuery query, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListDeliveriesAsync(query, cancellationToken);
    }

    public async Task<DeliveryDetailDto> GetDeliveryAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetDeliveryAsync(deliveryId, cancellationToken);
    }

    public async Task<DeliverySummaryDto> RedeliverAsync(Guid deliveryId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.RedeliverAsync(deliveryId, cancellationToken);
    }
}

public sealed class PermissionCheckingApiKeyService(IApiKeyService inner, AccessControl access) : IApiKeyService
{
    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<ApiKeyCreatedDto> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await access.RequireCanGrantRolesAsync(RoleGrant.Of(request.Roles, request.CustomRoleIds).RoleIds, cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<ApiKeyDto> RevokeAsync(Guid apiKeyId, RevokeApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.RevokeAsync(apiKeyId, request, cancellationToken);
    }
}

public sealed class PermissionCheckingConnectorService(IConnectorService inner, AccessControl access) : IConnectorService
{
    public async Task<IReadOnlyList<ConnectorKindDto>> ListKindsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListKindsAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListAsync(cancellationToken);
    }

    public async Task<ConnectorDto> GetAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetAsync(connectorId, cancellationToken);
    }

    public async Task<ConnectorDto> CreateAsync(SaveConnectorRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.CreateAsync(request, cancellationToken);
    }

    public async Task<ConnectorDto> UpdateAsync(Guid connectorId, SaveConnectorRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.UpdateAsync(connectorId, request, cancellationToken);
    }

    public async Task DeleteAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await inner.DeleteAsync(connectorId, cancellationToken);
    }

    public async Task<ConnectorRunDto> RunNowAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.RunNowAsync(connectorId, cancellationToken);
    }

    public async Task<IReadOnlyList<ConnectorRunDto>> ListRunsAsync(Guid connectorId, int take = 50, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.ListRunsAsync(connectorId, take, cancellationToken);
    }

    public async Task<ConnectorFileDto> GetRunFileAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await access.RequireIntegrationAdminAsync(cancellationToken);
        return await inner.GetRunFileAsync(runId, cancellationToken);
    }
}

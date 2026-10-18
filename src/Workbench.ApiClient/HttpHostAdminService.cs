using System.Net.Http.Json;
using Workbench.Contracts.Tenancy;

namespace Workbench.ApiClient;

/// <summary>Host administration over HTTP (point the client at the host site, not a tenant's address).</summary>
public sealed class HttpHostAdminService(HttpClient http) : IHostAdminService
{
    public async Task<HostAccessDto> GetAccessAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/host/access", cancellationToken)).ReadAsync<HostAccessDto>(cancellationToken);

    public async Task<IReadOnlyList<HostTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/host/tenants", cancellationToken).ReadListAsync<HostTenantDto>(cancellationToken);

    public async Task<HostTenantDto> CreateTenantAsync(CreateHostTenantRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/host/tenants", request, cancellationToken)).ReadAsync<HostTenantDto>(cancellationToken);

    public async Task<HostTenantDto> SuspendTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/host/tenants/{tenantId}/suspend", request, cancellationToken)).ReadAsync<HostTenantDto>(cancellationToken);

    public async Task<HostTenantDto> ReactivateTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/host/tenants/{tenantId}/reactivate", request, cancellationToken)).ReadAsync<HostTenantDto>(cancellationToken);

    public async Task<IReadOnlyList<HostAuditEventDto>> ListAuditAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/host/audit", cancellationToken).ReadListAsync<HostAuditEventDto>(cancellationToken);
}

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Tenancy;

namespace Workbench.Api;

/// <summary>Host administration (MultiTenant): <c>/api/v1/host/...</c>, only on the host site and only for host admins.</summary>
internal static class HostAdminEndpoints
{
    internal static void MapHostAdminEndpoints(this RouteGroupBuilder v1)
    {
        var host = v1.MapGroup("/host");
        host.MapGet("/access", (IHostAdminService s, CancellationToken ct) => s.GetAccessAsync(ct));
        host.MapGet("/tenants", (IHostAdminService s, CancellationToken ct) => s.ListTenantsAsync(ct));
        host.MapPost("/tenants", async (CreateHostTenantRequest request, IHostAdminService s, CancellationToken ct) =>
        {
            var tenant = await s.CreateTenantAsync(request, ct);
            return Results.Created($"/api/v1/host/tenants/{tenant.Id}", tenant);
        });
        host.MapPost("/tenants/{tenantId:guid}/suspend", (Guid tenantId, HostTenantStatusRequest request, IHostAdminService s, CancellationToken ct) =>
            s.SuspendTenantAsync(tenantId, request, ct));
        host.MapPost("/tenants/{tenantId:guid}/reactivate", (Guid tenantId, HostTenantStatusRequest request, IHostAdminService s, CancellationToken ct) =>
            s.ReactivateTenantAsync(tenantId, request, ct));
        host.MapGet("/audit", (IHostAdminService s, CancellationToken ct) => s.ListAuditAsync(ct));
    }
}

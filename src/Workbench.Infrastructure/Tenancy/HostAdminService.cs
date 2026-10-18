using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Tenancy;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>
/// <see cref="IHostAdminService"/>: host admins only, on the host site only. A tenant's own Admin role grants nothing
/// here, and a host admin signed in on a tenant's address can't use it either (A.8.2, A.8.3).
/// </summary>
internal sealed class HostAdminService(IServiceProvider services, TenancyOptions tenancy, ICurrentUser currentUser) : IHostAdminService
{
    public Task<HostAccessDto> GetAccessAsync(CancellationToken cancellationToken = default)
    {
        var hostSite = tenancy.IsMultiTenant && TenantScope.Current is null;
        return Task.FromResult(new HostAccessDto(tenancy.IsMultiTenant, hostSite, hostSite && tenancy.IsHostAdmin(currentUser.Email), tenancy.BaseDomain));
    }

    public async Task<IReadOnlyList<HostTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        var provisioner = Require();
        return (await provisioner.ListAsync(cancellationToken)).Select(o => ToDto(o.Tenant, o.DatabaseBytes, o.LastActivityUtc)).ToList();
    }

    public async Task<HostTenantDto> CreateTenantAsync(CreateHostTenantRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var provisioner = Require();
        var created = await provisioner.CreateAsync(
            new CreateTenantRequest(request.Slug, request.DisplayName, request.AdminEmail, request.AdminName, request.Plan, request.Notes),
            currentUser.Email, cancellationToken: cancellationToken);
        return ToDto(created, null, null);
    }

    public async Task<HostTenantDto> SuspendTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default) =>
        ToDto(await Require().SuspendAsync(tenantId, currentUser.Email, request?.Reason, cancellationToken), null, null);

    public async Task<HostTenantDto> ReactivateTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default) =>
        ToDto(await Require().ReactivateAsync(tenantId, currentUser.Email, request?.Reason, cancellationToken), null, null);

    public async Task<IReadOnlyList<HostAuditEventDto>> ListAuditAsync(CancellationToken cancellationToken = default) =>
        (await Require().ListAuditAsync(cancellationToken: cancellationToken)).Select(e => new HostAuditEventDto(e.AtUtc, e.Actor, e.Action, e.TenantId, e.Detail)).ToList();

    private TenantProvisioner Require()
    {
        if (!tenancy.IsMultiTenant || TenantScope.Current is not null || !tenancy.IsHostAdmin(currentUser.Email))
        {
            throw new WorkbenchForbiddenException("Only host administrators can manage tenants, on the host site.");
        }

        return services.GetRequiredService<TenantProvisioner>();
    }

    private HostTenantDto ToDto(TenantSummary t, long? bytes, DateTime? lastActivity) =>
        new(t.Id, t.Slug, t.DisplayName, t.Status.ToString(), t.Plan, t.Notes, t.CreatedAtUtc, t.StatusChangedAtUtc, t.StatusChangedBy, bytes, lastActivity,
            $"https://{t.Slug}.{tenancy.BaseDomain}/");
}

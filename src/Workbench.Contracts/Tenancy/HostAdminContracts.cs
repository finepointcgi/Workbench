namespace Workbench.Contracts.Tenancy;

/// <summary>Whether this server runs MultiTenant, whether this is the host admin site, and whether the user is a host admin.</summary>
public sealed record HostAccessDto(bool IsMultiTenant, bool IsHostSite, bool IsHostAdmin, string BaseDomain);

/// <summary>A tenant for the host admin. <see cref="Status"/> is Provisioning, Active, Suspended or Failed.</summary>
public sealed record HostTenantDto(
    Guid Id,
    string Slug,
    string DisplayName,
    string Status,
    string? Plan,
    string? Notes,
    DateTime CreatedAtUtc,
    DateTime? StatusChangedAtUtc,
    string? StatusChangedBy,
    long? DatabaseBytes,
    DateTime? LastActivityUtc,
    string Url);

/// <param name="Slug">The tenant id and subdomain: 3–40 lowercase letters, digits and hyphens.</param>
public sealed record CreateHostTenantRequest(string Slug, string DisplayName, string AdminEmail, string? AdminName = null, string? Plan = null, string? Notes = null);

public sealed record HostTenantStatusRequest(string? Reason);

public sealed record HostAuditEventDto(DateTime AtUtc, string Actor, string Action, Guid? TenantId, string? Detail);

/// <summary>
/// Host administration in MultiTenant mode: create, suspend and reactivate tenants. Only for host admins
/// (<c>Tenancy:HostAdmins</c>, not a tenant's Admin role) on the host site (the base domain, no tenant); anyone else gets
/// <see cref="WorkbenchForbiddenException"/>.
/// </summary>
public interface IHostAdminService
{
    Task<HostAccessDto> GetAccessAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostTenantDto>> ListTenantsAsync(CancellationToken cancellationToken = default);

    /// <summary>Creates the database, applies migrations, seeds the chart of accounts and fiscal calendar, and invites the first Admin.</summary>
    Task<HostTenantDto> CreateTenantAsync(CreateHostTenantRequest request, CancellationToken cancellationToken = default);

    Task<HostTenantDto> SuspendTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default);

    Task<HostTenantDto> ReactivateTenantAsync(Guid tenantId, HostTenantStatusRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<HostAuditEventDto>> ListAuditAsync(CancellationToken cancellationToken = default);
}

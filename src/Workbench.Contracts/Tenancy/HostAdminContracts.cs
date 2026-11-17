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
    string Url,
    HostEntitlementDto? Entitlement = null);

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

    /// <summary>Changes the tenant's modules, seats, plan or entitlement status (audited). Creates the entitlement if the tenant has none.</summary>
    Task<HostTenantDto> SetEntitlementAsync(Guid tenantId, SetEntitlementRequest request, CancellationToken cancellationToken = default);

    /// <summary>Moves the trial's end later by <see cref="ExtendTrialRequest.Days"/> (from today when it has already ended) and lifts a lapse (audited).</summary>
    Task<HostTenantDto> ExtendTrialAsync(Guid tenantId, ExtendTrialRequest request, CancellationToken cancellationToken = default);
}

/// <summary>A tenant's entitlement as the host admin and the license server see it. <see cref="Status"/> is active, past_due, suspended or cancelled.</summary>
public sealed record HostEntitlementDto(
    string Plan,
    IReadOnlyList<string> Modules,
    int Seats,
    int LightSeats,
    string Status,
    bool Trial,
    DateTime? TrialEndsUtc,
    DateTime? PaidThroughUtc,
    bool ReadOnly,
    string Source,
    string? ExternalRef,
    DateTime UpdatedAtUtc,
    string? UpdatedBy,
    int? SeatsInUse = null,
    int? LightSeatsInUse = null,
    DateTime? RetentionEndsUtc = null,
    string? SignupEmail = null,
    string? SignupSource = null,
    DateTime? TermsAcceptedAtUtc = null);

/// <summary>
/// Sets a tenant's entitlement. Every field is optional; a missing field is left as it is, so the license server sends what
/// its subscription says and a host admin changes one thing. <paramref name="EventAtUtc"/> is when the change happened at its
/// source (Stripe's event time): an update older than the last one applied is ignored, which makes redelivery and reordering safe.
/// </summary>
public sealed record SetEntitlementRequest(
    string? Plan = null,
    IReadOnlyList<string>? Modules = null,
    int? Seats = null,
    int? LightSeats = null,
    string? Status = null,
    bool? Trial = null,
    DateTime? TrialEndsUtc = null,
    DateTime? PaidThroughUtc = null,
    string? ExternalRef = null,
    DateTime? EventAtUtc = null,
    string? Reason = null);

public sealed record ExtendTrialRequest(int Days, string? Reason = null);

/// <summary>The entitlement endpoint's answer: the tenant and what it is entitled to.</summary>
public sealed record HostTenantEntitlementDto(Guid TenantId, string Slug, HostEntitlementDto Entitlement);

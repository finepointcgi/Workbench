namespace Workbench.Application.Tenancy;

/// <summary>A tenant (customer organization) in MultiTenant mode. Each tenant has its own database.</summary>
public sealed record TenantInfo(Guid Id, string Slug, string DisplayName);

/// <summary>Configuration section <c>Tenancy</c>.</summary>
public sealed class TenancyOptions
{
    public const string SectionName = "Tenancy";

    /// <summary><c>SingleTenant</c> (self-hosted, the default) or <c>MultiTenant</c> (SaaS, one database per tenant).</summary>
    public TenancyMode Mode { get; set; } = TenancyMode.SingleTenant;

    /// <summary>
    /// The host name tenants are subdomains of, e.g. <c>workbench.example</c> (acme.workbench.example is tenant
    /// <c>acme</c>) or <c>localhost</c> in Development. Requests to the base domain itself reach the host admin site.
    /// </summary>
    public string BaseDomain { get; set; } = "localhost";

    /// <summary>Emails of the host administrators, who run <c>/host</c>. Not the same as a tenant's Admin role.</summary>
    public string[] HostAdmins { get; set; } = [];

    /// <summary>
    /// Connection string for a new tenant's database, with <c>{tenant}</c> standing for the tenant id (hyphens become
    /// underscores), e.g. <c>Host=db;Database=workbench_{tenant};Username=workbench;Password=...</c> or
    /// <c>Data Source=App_Data/tenants/{tenant}.db</c>. The server account needs permission to create databases.
    /// </summary>
    public string? TenantConnectionString { get; set; }

    /// <summary>Development only: tenants created with the sample data on startup when missing (e.g. <c>acme</c>, <c>beta</c>).</summary>
    public string[] DevelopmentTenants { get; set; } = [];

    /// <summary>First month of the fiscal year seeded for a new tenant (10 = October, the federal fiscal year).</summary>
    public int FiscalYearStartMonth { get; set; } = 10;

    /// <summary>Allows the <c>X-Tenant</c> header to pick the tenant. Honoured only in Development.</summary>
    public bool AllowTenantHeader { get; set; } = true;

    public bool IsMultiTenant => Mode == TenancyMode.MultiTenant;

    public bool IsHostAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email) && HostAdmins.Any(a => string.Equals(a.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}

public enum TenancyMode
{
    SingleTenant,
    MultiTenant,
}

/// <summary>
/// The tenant the current code runs for. Set per request by the web host (from the subdomain), for the whole life of
/// a Blazor circuit, per SignalR hub call, and per tenant by background jobs (<see cref="ITenantJobRunner"/>). It is an
/// ambient value (<see cref="AsyncLocal{T}"/>) so the singleton database factory can see it without feature code
/// passing it around. Null in SingleTenant mode, and on the host admin site.
/// </summary>
public static class TenantScope
{
    private static readonly AsyncLocal<TenantInfo?> Ambient = new();

    public static TenantInfo? Current => Ambient.Value;

    /// <summary>A key that separates per-tenant entries in shared caches and event fan-outs ("" without a tenant).</summary>
    public static string Key => Ambient.Value?.Slug ?? string.Empty;

    /// <summary>Runs the caller as <paramref name="tenant"/> until the returned scope is disposed.</summary>
    public static IDisposable Enter(TenantInfo? tenant)
    {
        var previous = Ambient.Value;
        Ambient.Value = tenant;
        return new Restore(previous);
    }

    /// <summary>Prefixes <paramref name="key"/> with the current tenant, for singleton caches keyed by email or id.</summary>
    public static string Qualify(string key) => Key.Length == 0 ? key : $"{Key}|{key}";

    private sealed class Restore(TenantInfo? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}

/// <summary>The current tenant, for code that would rather inject than read <see cref="TenantScope"/>.</summary>
public interface ITenantContext
{
    TenantInfo? Current { get; }

    bool IsMultiTenant { get; }

    /// <summary>True in MultiTenant mode outside any tenant: the host admin site.</summary>
    bool IsHost { get; }
}

public sealed class AmbientTenantContext(TenancyOptions options) : ITenantContext
{
    public TenantInfo? Current => TenantScope.Current;

    public bool IsMultiTenant => options.IsMultiTenant;

    public bool IsHost => options.IsMultiTenant && TenantScope.Current is null;
}

/// <summary>
/// Runs background work once per active tenant (in MultiTenant mode), or once (SingleTenant). Every
/// <c>BackgroundService</c> that touches the database wraps each run in <see cref="ForEachTenantAsync"/>.
/// A failure in one tenant is logged and doesn't stop the others.
/// </summary>
public interface ITenantJobRunner
{
    Task ForEachTenantAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken);
}

public sealed class SingleTenantJobRunner : ITenantJobRunner
{
    public Task ForEachTenantAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken) => work(cancellationToken);
}

/// <summary>Thrown when tenant data is used without a tenant in MultiTenant mode. Fails closed.</summary>
public sealed class TenantNotResolvedException()
    : InvalidOperationException("No tenant is selected for this request, so no tenant database can be opened.");

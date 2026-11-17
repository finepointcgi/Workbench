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

    /// <summary>Self-service signup and the hosted entitlement endpoint (<c>Tenancy:Signup</c>). Off by default.</summary>
    public SignupOptions Signup { get; set; } = new();

    public bool IsMultiTenant => Mode == TenancyMode.MultiTenant;

    public bool IsHostAdmin(string? email) =>
        !string.IsNullOrWhiteSpace(email) && HostAdmins.Any(a => string.Equals(a.Trim(), email.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Configuration section <c>Tenancy:Signup</c>: the hosted front door (MultiTenant mode only).</summary>
public sealed class SignupOptions
{
    /// <summary>Turns on <c>/signup</c> on the base domain. Off by default: a self-hosted or private deployment never offers it.</summary>
    public bool Enabled { get; set; }

    public int TrialDays { get; set; } = 14;

    /// <summary>Modules in the trial (the "Business" trial). Default: every module.</summary>
    public string[] TrialModules { get; set; } = ["finance", "capture", "workforce", "controls", "govcon", "enterprise"];

    public int TrialSeats { get; set; } = 10;

    public int TrialLightSeats { get; set; } = 10;

    /// <summary>Link to the terms the visitor accepts (the draft terms page). Empty: the checkbox is shown without a link.</summary>
    public string? TermsUrl { get; set; }

    /// <summary>Recorded with each consent so a later change of terms is traceable.</summary>
    public string TermsVersion { get; set; } = "draft-1";

    /// <summary>Signups allowed per client address per hour.</summary>
    public int MaxPerIpPerHour { get; set; } = 5;

    /// <summary>Signups (or resends) allowed per email address per day.</summary>
    public int MaxPerEmailPerDay { get; set; } = 3;

    /// <summary>How long the emailed sign-in link works.</summary>
    public int MagicLinkMinutes { get; set; } = 60;

    /// <summary>Days of read-only access after a paid term ends or a payment fails, before the tenant locks. The license server's key grace is 7.</summary>
    public int GraceDays { get; set; } = 7;

    /// <summary>
    /// Days a lapsed tenant's data is kept before the host admin may delete it. Nothing is ever deleted automatically;
    /// the host page only shows when the period has passed.
    /// </summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>
    /// The shared secret the license server sends (<c>Authorization: Bearer</c>) to <c>/api/v1/host/entitlements</c>. At least 32
    /// characters. Unset: the endpoint answers 404. Put it in an environment variable or a secret store, never in a file in the repository.
    /// </summary>
    public string? EntitlementApiKey { get; set; }
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

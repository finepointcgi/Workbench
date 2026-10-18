namespace Workbench.Web.Security;

/// <summary>Config section <c>Authentication:Session</c> (ISO 27001 A.8.5).</summary>
public sealed class SessionSettings
{
    public const string SectionName = "Authentication:Session";

    /// <summary>The auth cookie expires after this much inactivity (default 30 minutes).</summary>
    public int IdleTimeoutMinutes { get; set; } = 30;

    /// <summary>A session ends this long after sign-in, however active (default 12 hours).</summary>
    public int AbsoluteLifetimeHours { get; set; } = 12;

    /// <summary>How often an open Blazor circuit re-checks that its user is still active (default 1 minute).</summary>
    public int CircuitRevalidationSeconds { get; set; } = 60;

    public TimeSpan IdleTimeout => TimeSpan.FromMinutes(Math.Max(1, IdleTimeoutMinutes));

    public TimeSpan AbsoluteLifetime => TimeSpan.FromHours(Math.Max(1, AbsoluteLifetimeHours));
}

/// <summary>Config section <c>Scim</c>: SCIM 2.0 user provisioning for Entra ID, Okta and other identity providers.</summary>
public sealed class ScimSettings
{
    public const string SectionName = "Scim";

    /// <summary>Maps <c>/scim/v2</c>. On by default; outside Development a strong <see cref="BearerToken"/> is then required.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The secret the identity provider sends as <c>Authorization: Bearer ...</c>. At least 32 characters. Supply it from a vault or environment variable.</summary>
    public string? BearerToken { get; set; }

    public const int MinimumTokenLength = 32;
}

/// <summary>Config section <c>RateLimiting</c>: requests per minute per client (A.8.20).</summary>
public sealed class RateLimitSettings
{
    public const string SectionName = "RateLimiting";

    public bool Enabled { get; set; } = true;

    /// <summary>Sign-in, sign-out and OIDC callbacks, per IP address.</summary>
    public int SignInPerMinute { get; set; } = 20;

    /// <summary>SCIM provisioning calls, per IP address.</summary>
    public int ScimPerMinute { get; set; } = 300;

    /// <summary>REST API calls, per signed-in user (or IP address when anonymous).</summary>
    public int ApiPerMinute { get; set; } = 600;
}

/// <summary>Config section <c>DataProtection</c> (A.8.24): where ASP.NET Core keys live and how they're encrypted at rest.</summary>
public sealed class DataProtectionSettings
{
    public const string SectionName = "DataProtection";

    /// <summary><c>Database</c> (default; shared by every server instance), <c>FileSystem</c> (<see cref="KeysFolder"/>) or <c>Default</c> (ASP.NET Core's per-machine default).</summary>
    public string Store { get; set; } = "Database";

    public string? KeysFolder { get; set; }

    /// <summary>A PFX file whose certificate encrypts the keys at rest. Its password comes from <see cref="CertificatePassword"/>.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Alternatively, a certificate thumbprint in the current user's or local machine's store (Windows).</summary>
    public string? CertificateThumbprint { get; set; }

    public string ApplicationName { get; set; } = "Workbench";
}

/// <summary>Config section <c>Security</c>.</summary>
public sealed class HostSecuritySettings
{
    public const string SectionName = "Security";

    /// <summary>
    /// True when a reverse proxy or load balancer terminates TLS and forwards plain HTTP. Forwarded headers are then
    /// honored so the app knows the original scheme and client IP.
    /// </summary>
    public bool TlsTerminatedByProxy { get; set; }

    /// <summary>HSTS max-age in days (default 365).</summary>
    public int HstsMaxAgeDays { get; set; } = 365;

    /// <summary>Extra origins for the Content-Security-Policy <c>connect-src</c> (e.g. an OTLP browser collector). Usually empty.</summary>
    public List<string> ConnectSources { get; set; } = [];
}

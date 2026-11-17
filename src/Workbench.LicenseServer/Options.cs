namespace Workbench.LicenseServer;

public sealed class AdminOptions
{
    public const string SectionName = "Admin";
    /// <summary>The admin API key (header <c>X-Admin-Key</c> or <c>Authorization: Bearer</c>). Unset = the admin API is switched off. At least 24 characters.</summary>
    public string? ApiKey { get; set; }
}

public sealed class SigningOptions
{
    public const string SectionName = "Signing";
    /// <summary>The ECDSA P-256 private key as PEM (PKCS#8). Literal "\n" sequences are accepted so it fits in one environment variable.</summary>
    public string? PrivateKeyPem { get; set; }
    /// <summary>Or a path to the PEM file (a mounted secret). Used when <see cref="PrivateKeyPem"/> is empty.</summary>
    public string? PrivateKeyPath { get; set; }
}

public sealed class KeyOptions
{
    public const string SectionName = "Keys";
    /// <summary>Days a subscription key keeps working past the paid-through date, so a late card never stops payroll.</summary>
    public int GraceDays { get; set; } = 7;
    /// <summary>How long a connected install's refreshed key lasts. The client refreshes well before it ends.</summary>
    public int RefreshDays { get; set; } = 14;
    /// <summary>Distinct installs (production, staging, DR...) one license may activate.</summary>
    public int MaxInstalls { get; set; } = 5;
    /// <summary>Default term of an offline key issued by hand, in days.</summary>
    public int OfflineDays { get; set; } = 365;
}

public sealed class LimitOptions
{
    public const string SectionName = "RateLimits";
    public int ConnectedPerMinute { get; set; } = 30;
    public int LoginPerMinute { get; set; } = 5;
}

public sealed class PortalOptions
{
    public const string SectionName = "Portal";
    /// <summary>Public https base of this server, used in emailed links, e.g. https://license.example.com. Falls back to the request.</summary>
    public string? PublicBaseUrl { get; set; }
    public int LoginLinkMinutes { get; set; } = 15;
    public int SessionHours { get; set; } = 8;
    /// <summary>Folder for the cookie-protection keys, so sessions survive a restart. Mount a volume here in a container.</summary>
    public string? DataProtectionPath { get; set; }
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";
    /// <summary>"Sqlite" (default) or "PostgreSql".</summary>
    public string Provider { get; set; } = "Sqlite";
    public string ConnectionString { get; set; } = "Data Source=App_Data/licenses.db";
    public bool MigrateOnStartup { get; set; } = true;
}

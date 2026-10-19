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

    /// <summary>
    /// How the key ring is encrypted at rest: <c>Certificate</c> (a PFX file or a certificate in the OS store),
    /// <c>AzureKeyVault</c>, <c>AwsKms</c> or <c>None</c>. When empty it is <c>Certificate</c> if a certificate is
    /// configured, else <c>None</c>. Production refuses <c>None</c>.
    /// </summary>
    public string? KeyEncryption { get; set; }

    /// <summary>A PFX file whose certificate encrypts the keys at rest. Its password comes from <see cref="CertificatePassword"/>.</summary>
    public string? CertificatePath { get; set; }

    public string? CertificatePassword { get; set; }

    /// <summary>Alternatively, the thumbprint of a certificate (with its private key) in the <c>My</c> store of the current user or local machine.</summary>
    public string? CertificateThumbprint { get; set; }

    /// <summary>
    /// Certificates that encrypted older keys and may still be needed to decrypt them (certificate rotation). They are
    /// never used to encrypt. Remove one only after <c>--rewrap-data-protection-keys</c> has re-encrypted every key.
    /// </summary>
    public List<CertificateReference> PreviousCertificates { get; set; } = [];

    public AzureKeyVaultKeySettings AzureKeyVault { get; set; } = new();

    public AwsKmsKeySettings AwsKms { get; set; } = new();

    /// <summary>How long a new key is used to protect data before the next one is created (default 90 days).</summary>
    public int KeyLifetimeDays { get; set; } = 90;

    public string ApplicationName { get; set; } = "Workbench";

    public KeyEncryptionMode ResolveKeyEncryption()
    {
        if (string.IsNullOrWhiteSpace(KeyEncryption))
        {
            return string.IsNullOrWhiteSpace(CertificatePath) && string.IsNullOrWhiteSpace(CertificateThumbprint)
                ? KeyEncryptionMode.None
                : KeyEncryptionMode.Certificate;
        }

        return Enum.TryParse<KeyEncryptionMode>(KeyEncryption.Trim(), ignoreCase: true, out var mode) && Enum.IsDefined(mode)
            ? mode
            : throw new InvalidOperationException($"Unsupported DataProtection:KeyEncryption '{KeyEncryption}'. Use Certificate, AzureKeyVault, AwsKms or None.");
    }
}

public enum KeyEncryptionMode
{
    None,
    Certificate,
    AzureKeyVault,
    AwsKms,
}

/// <summary>A certificate from a PFX file (<see cref="Path"/> and <see cref="Password"/>) or the OS store (<see cref="Thumbprint"/>).</summary>
public sealed class CertificateReference
{
    public string? Path { get; set; }
    public string? Password { get; set; }
    public string? Thumbprint { get; set; }
}

/// <summary>Config section <c>DataProtection:AzureKeyVault</c>: a key (RSA) in Azure Key Vault wraps the key ring.</summary>
public sealed class AzureKeyVaultKeySettings
{
    /// <summary>The key identifier, e.g. <c>https://my-vault.vault.azure.net/keys/workbench-dp</c> (or a specific version).</summary>
    public string? KeyId { get; set; }

    /// <summary>
    /// Optional. With <see cref="ClientSecret"/> and <see cref="TenantId"/>, a service principal; alone, the client id of a
    /// user-assigned managed identity. Without any, <c>DefaultAzureCredential</c> (managed or workload identity).
    /// </summary>
    public string? ClientId { get; set; }

    public string? TenantId { get; set; }

    public string? ClientSecret { get; set; }
}

/// <summary>Config section <c>DataProtection:AwsKms</c>: a symmetric AWS KMS key wraps the key ring.</summary>
public sealed class AwsKmsKeySettings
{
    /// <summary>The key's ARN or alias ARN, e.g. <c>arn:aws-us-gov:kms:us-gov-west-1:111122223333:alias/workbench-dp</c>.</summary>
    public string? KeyId { get; set; }

    /// <summary>Optional; otherwise the SDK's default (AWS_REGION). Credentials always come from the default chain (an IAM role).</summary>
    public string? Region { get; set; }
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

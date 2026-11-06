using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Storage;
using Workbench.Web.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// Refuses to start outside Development with a known-weak configuration (ISO 27001 A.8.9, A.8.24): the dev sign-in
/// page, plain HTTP without a TLS-terminating proxy, SCIM without a strong token, SQLite in Production without an
/// explicit opt-in, and in Production an unencrypted Data Protection key ring or SMTP without TLS. Development is never
/// checked. The public demo (<see cref="DemoMode"/>) may use the dev sign-in page and the fake assistant without an
/// identity provider, but only in the Demo environment, single-tenant, and without sending email.
/// </summary>
public static class ProductionConfigurationCheck
{
    public static IReadOnlyList<string> FindProblems(IConfiguration configuration, IHostEnvironment environment)
    {
        if (environment.IsDevelopment())
        {
            return [];
        }

        var problems = new List<string>();
        var demo = DemoMode.IsActive(configuration, environment);
        if (configuration.GetValue(DemoMode.EnabledKey, false) && !demo)
        {
            problems.Add($"{DemoMode.EnabledKey} is true in {environment.EnvironmentName}. Demo mode lets anyone sign in as any user; it only runs with ASPNETCORE_ENVIRONMENT={DemoMode.EnvironmentName} on a demo database.");
        }

        var oidc = configuration.GetSection(OidcSettings.SectionName).Get<OidcSettings>() ?? new OidcSettings();
        var passwordsEnabled = configuration.GetValue($"{Application.Identity.Passwords.PasswordOptions.SectionName}:Enabled", true);
        var multiTenant = (configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions()).IsMultiTenant;
        if (!oidc.IsConfigured && !demo && (!passwordsEnabled || multiTenant))
        {
            // SingleTenant servers can run on built-in accounts alone (bootstrap admins set a password with "Forgot password").
            problems.Add(multiTenant
                ? $"Set {OidcSettings.SectionName}:Authority and :ClientId (the MultiTenant host site signs in with them)."
                : $"Set {OidcSettings.SectionName}:Authority and :ClientId, or turn built-in accounts back on ({Application.Identity.Passwords.PasswordOptions.SectionName}:Enabled).");
        }

        if (DevSignIn.IsEnabled(environment, oidc, configuration) && !demo)
        {
            problems.Add("The dev sign-in page is enabled. Configure Authentication:Oidc:Authority and :ClientId.");
        }

        if (oidc.IsConfigured && !oidc.RequireHttpsMetadata)
        {
            problems.Add("Authentication:Oidc:RequireHttpsMetadata is false. Use an HTTPS identity provider.");
        }

        var security = configuration.GetSection(HostSecuritySettings.SectionName).Get<HostSecuritySettings>() ?? new HostSecuritySettings();
        if (!security.TlsTerminatedByProxy && IsHttpOnly(configuration))
        {
            problems.Add("The server only listens on plain HTTP. Add an https:// URL with a certificate, or set Security:TlsTerminatedByProxy=true behind a TLS-terminating proxy.");
        }

        var tenancy = configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions();
        var scim = configuration.GetSection(ScimSettings.SectionName).Get<ScimSettings>() ?? new ScimSettings();
        if (!tenancy.IsMultiTenant && scim.Enabled && (scim.BearerToken?.Trim().Length ?? 0) < ScimSettings.MinimumTokenLength)
        {
            problems.Add($"Scim:BearerToken is empty or shorter than {ScimSettings.MinimumTokenLength} characters. Set a random token from a vault or environment variable (Scim__BearerToken), or set Scim:Enabled=false.");
        }

        var provider = DatabaseProviders.FromConfiguration(configuration);
        if (environment.IsProduction() && provider == DatabaseProvider.Sqlite
            && !configuration.GetValue("Database:AllowSqliteInProduction", false))
        {
            problems.Add("Database:Provider is Sqlite in Production. Use PostgreSQL, or set Database:AllowSqliteInProduction=true for a single-user evaluation.");
        }

        problems.AddRange(DataProtectionProblems(configuration, environment));

        var smtpPlainText = configuration["Email:Smtp:Security"] is { Length: > 0 } smtpSecurity
            ? smtpSecurity.Equals("None", StringComparison.OrdinalIgnoreCase)
            : !configuration.GetValue("Email:Smtp:EnableSsl", true);
        var emailMode = configuration["Email:Mode"] is { Length: > 0 } m ? m : string.IsNullOrWhiteSpace(configuration["Email:Smtp:Host"]) ? "Pickup" : "Smtp";
        if (environment.IsProduction() && emailMode.Equals("Smtp", StringComparison.OrdinalIgnoreCase) && smtpPlainText)
        {
            problems.Add("Email is sent over SMTP without TLS. Set Email:Smtp:Security to Auto, StartTls or SslOnConnect.");
        }

        if (tenancy.IsMultiTenant)
        {
            // MultiTenant (A.8.3, A.5.23): tenants only admit invited people, and the host site needs its own admins.
            if (!configuration.GetValue("Authentication:RequireInvite", true))
            {
                problems.Add("Authentication:RequireInvite is false in MultiTenant mode. Anyone the identity provider vouches for could join any tenant; keep invites required.");
            }

            if (!tenancy.HostAdmins.Any(a => !string.IsNullOrWhiteSpace(a)))
            {
                problems.Add("Tenancy:HostAdmins is empty. List the emails of the people who may create and suspend tenants.");
            }

            if (string.IsNullOrWhiteSpace(tenancy.BaseDomain) || tenancy.BaseDomain.Trim().Equals("localhost", StringComparison.OrdinalIgnoreCase))
            {
                problems.Add("Tenancy:BaseDomain is localhost. Set the domain tenants are subdomains of (e.g. workbench.example).");
            }
        }

        if (demo)
        {
            problems.AddRange(DemoProblems(tenancy, emailMode));
        }

        problems.AddRange(AssistantProblems(configuration, allowFakeProvider: demo));
        problems.AddRange(IntegrationProblems(configuration));
        problems.AddRange(StorageProblems(configuration, environment));
        return problems;
    }

    /// <summary>Bucket storage must be complete, and S3 objects must be encrypted at rest in Production (A.8.24).</summary>
    public static IEnumerable<string> StorageProblems(IConfiguration configuration, IHostEnvironment environment)
    {
        var storage = BlobStorageSettings.FromConfiguration(configuration);
        foreach (var problem in storage.Problems())
        {
            yield return problem;
        }

        if (environment.IsProduction() && storage.Provider.Equals("S3", StringComparison.OrdinalIgnoreCase)
            && storage.S3.ServerSideEncryption.Trim().Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            yield return "Storage:S3:ServerSideEncryption is None in Production. Use AES256 or aws:kms (MinIO: configure its KMS, e.g. MINIO_KMS_SECRET_KEY).";
        }
    }

    /// <summary>Webhooks and connectors must use https and public addresses outside Development (the SSRF guard).</summary>
    public static IEnumerable<string> IntegrationProblems(IConfiguration configuration)
    {
        if (configuration.GetValue($"{Application.Integrations.IntegrationOptions.Section}:{nameof(Application.Integrations.IntegrationOptions.AllowHttp)}", false))
        {
            yield return "Integrations:AllowHttp is true. Webhooks and connectors must use https:// outside Development.";
        }

        if (configuration.GetValue($"{Application.Integrations.IntegrationOptions.Section}:{nameof(Application.Integrations.IntegrationOptions.AllowPrivateNetworkTargets)}", false))
        {
            yield return "Integrations:AllowPrivateNetworkTargets is true. Webhooks and connectors may not reach private or internal addresses outside Development.";
        }
    }

    /// <summary>
    /// The public demo: one sample company (no tenants to cross), and no real email, since its users are made up and
    /// anyone with the link can trigger notifications.
    /// </summary>
    private static IEnumerable<string> DemoProblems(TenancyOptions tenancy, string emailMode)
    {
        if (tenancy.IsMultiTenant)
        {
            yield return "Demo mode is SingleTenant only. Set Tenancy:Mode=SingleTenant.";
        }

        if (!emailMode.Equals("Pickup", StringComparison.OrdinalIgnoreCase))
        {
            yield return $"Email:Mode is {emailMode} in demo mode. The demo must not send email; set Email:Mode=Pickup.";
        }
    }

    /// <summary>
    /// The AI assistant: no fake provider outside Development and the public demo, and every configured connection
    /// complete (key or region).
    /// </summary>
    public static IEnumerable<string> AssistantProblems(IConfiguration configuration, bool allowFakeProvider = false)
    {
        var assistant = configuration.GetSection(Workbench.Application.Assistant.AssistantOptions.Section).Get<Workbench.Application.Assistant.AssistantOptions>()
            ?? new Workbench.Application.Assistant.AssistantOptions();
        if (assistant.EnableFakeProvider && !allowFakeProvider)
        {
            yield return "Assistant:EnableFakeProvider is true. The fake provider is for development, tests and the public demo (Demo:Enabled) only; configure an Anthropic or Bedrock connection instead.";
        }

        if (assistant.EnableClaudeCodeProvider)
        {
            yield return "Assistant:EnableClaudeCodeProvider is true. The local Claude Code provider is for development on your own computer only; configure an Anthropic or Bedrock connection instead.";
        }

        foreach (var problem in Workbench.Application.Assistant.AssistantConnections.Validate(assistant))
        {
            yield return problem;
        }
    }

    /// <summary>
    /// The Data Protection key ring protects cookies and the secrets stored encrypted (vendor TINs, bank accounts), so in
    /// Production it must be encrypted at rest and kept somewhere every instance shares and backups include.
    /// </summary>
    private static IEnumerable<string> DataProtectionProblems(IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        KeyEncryptionMode mode;
        try
        {
            mode = settings.ResolveKeyEncryption();
        }
        catch (InvalidOperationException ex)
        {
            return [ex.Message];
        }

        var problems = new List<string>();
        switch (mode)
        {
            case KeyEncryptionMode.None when environment.IsProduction():
                problems.Add("Data Protection keys aren't encrypted at rest. Set DataProtection:KeyEncryption to Certificate (DataProtection:CertificatePath or :CertificateThumbprint), AzureKeyVault (DataProtection:AzureKeyVault:KeyId) or AwsKms (DataProtection:AwsKms:KeyId).");
                break;
            case KeyEncryptionMode.Certificate when string.IsNullOrWhiteSpace(settings.CertificatePath) && string.IsNullOrWhiteSpace(settings.CertificateThumbprint):
                problems.Add("DataProtection:KeyEncryption is Certificate but neither DataProtection:CertificatePath nor :CertificateThumbprint is set.");
                break;
            case KeyEncryptionMode.Certificate when !string.IsNullOrWhiteSpace(settings.CertificatePath) && !File.Exists(settings.CertificatePath):
                problems.Add($"DataProtection:CertificatePath '{settings.CertificatePath}' doesn't exist.");
                break;
            case KeyEncryptionMode.AzureKeyVault when !Uri.TryCreate(settings.AzureKeyVault.KeyId, UriKind.Absolute, out var keyId) || keyId.Scheme != Uri.UriSchemeHttps:
                problems.Add("DataProtection:KeyEncryption is AzureKeyVault but DataProtection:AzureKeyVault:KeyId isn't an https:// key identifier.");
                break;
            case KeyEncryptionMode.AwsKms when string.IsNullOrWhiteSpace(settings.AwsKms.KeyId):
                problems.Add("DataProtection:KeyEncryption is AwsKms but DataProtection:AwsKms:KeyId is empty.");
                break;
        }

        foreach (var previous in settings.PreviousCertificates.Where(c => !string.IsNullOrWhiteSpace(c.Path) && !File.Exists(c.Path)))
        {
            problems.Add($"DataProtection:PreviousCertificates path '{previous.Path}' doesn't exist.");
        }

        if (environment.IsProduction() && settings.Store.Equals("Default", StringComparison.OrdinalIgnoreCase))
        {
            problems.Add("DataProtection:Store is Default in Production: keys would live on one machine (or vanish with a container). Use Database.");
        }

        return problems;
    }

    /// <summary>Throws with every problem listed, so an operator fixes them in one pass.</summary>
    public static void Enforce(IConfiguration configuration, IHostEnvironment environment)
    {
        var problems = FindProblems(configuration, environment);
        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workbench refuses to start in {environment.EnvironmentName} with a weak configuration:{Environment.NewLine}- "
                + string.Join($"{Environment.NewLine}- ", problems));
        }
    }

    /// <summary>
    /// True when every configured listening URL is http://. With nothing configured, ASP.NET Core listens on plain HTTP
    /// (port 5000 or ASPNETCORE_HTTP_PORTS), which also counts.
    /// </summary>
    internal static bool IsHttpOnly(IConfiguration configuration)
    {
        var urls = new List<string>();
        urls.AddRange((configuration["urls"] ?? configuration["ASPNETCORE_URLS"] ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        urls.AddRange(configuration.GetSection("Kestrel:Endpoints").GetChildren().Select(e => e["Url"]).OfType<string>());
        if (!string.IsNullOrWhiteSpace(configuration["https_ports"] ?? configuration["ASPNETCORE_HTTPS_PORTS"]))
        {
            return false;
        }

        return urls.Count == 0 || urls.All(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }
}

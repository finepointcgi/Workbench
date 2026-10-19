using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// Refuses to start outside Development with a known-weak configuration (ISO 27001 A.8.9, A.8.24): the dev sign-in
/// page, plain HTTP without a TLS-terminating proxy, SCIM without a strong token, SQLite in Production without an
/// explicit opt-in, and in Production an unencrypted Data Protection key ring or SMTP without TLS. Development is never
/// checked.
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
        var oidc = configuration.GetSection(OidcSettings.SectionName).Get<OidcSettings>() ?? new OidcSettings();
        if (!oidc.IsConfigured)
        {
            problems.Add($"Set {OidcSettings.SectionName}:Authority and :ClientId (single sign-on is required outside Development).");
        }

        if (DevSignIn.IsEnabled(environment, oidc))
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

        problems.AddRange(AssistantProblems(configuration));
        return problems;
    }

    /// <summary>The AI assistant: no fake provider outside Development, and every configured connection complete (key or region).</summary>
    public static IEnumerable<string> AssistantProblems(IConfiguration configuration)
    {
        var assistant = configuration.GetSection(Workbench.Application.Assistant.AssistantOptions.Section).Get<Workbench.Application.Assistant.AssistantOptions>()
            ?? new Workbench.Application.Assistant.AssistantOptions();
        if (assistant.EnableFakeProvider)
        {
            yield return "Assistant:EnableFakeProvider is true. The fake provider is for development and tests only; configure an Anthropic or Bedrock connection instead.";
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

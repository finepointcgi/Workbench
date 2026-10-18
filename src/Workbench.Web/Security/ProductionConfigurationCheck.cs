using Workbench.Web.Identity;

namespace Workbench.Web.Security;

/// <summary>
/// Refuses to start outside Development with a known-weak configuration (ISO 27001 A.8.9, A.8.24): the dev sign-in
/// page, plain HTTP without a TLS-terminating proxy, SCIM without a strong token, or SQLite in Production without an
/// explicit opt-in. Development is never checked.
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

        var scim = configuration.GetSection(ScimSettings.SectionName).Get<ScimSettings>() ?? new ScimSettings();
        if (scim.Enabled && (scim.BearerToken?.Trim().Length ?? 0) < ScimSettings.MinimumTokenLength)
        {
            problems.Add($"Scim:BearerToken is empty or shorter than {ScimSettings.MinimumTokenLength} characters. Set a random token from a vault or environment variable (Scim__BearerToken), or set Scim:Enabled=false.");
        }

        var provider = configuration["Database:Provider"] ?? "Sqlite";
        if (environment.IsProduction() && provider.Equals("Sqlite", StringComparison.OrdinalIgnoreCase)
            && !configuration.GetValue("Database:AllowSqliteInProduction", false))
        {
            problems.Add("Database:Provider is Sqlite in Production. Use PostgreSQL, or set Database:AllowSqliteInProduction=true for a single-user evaluation.");
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

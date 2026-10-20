using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>
/// The real web host on a migrated test database (see <see cref="TestDb"/>); Development seeds the sample data. With
/// <c>testAuth</c>, requests carrying <c>X-Test-User: email</c> are signed in as that user, standing in for the
/// cookie or bearer token.
/// </summary>
internal sealed class WorkbenchWebFactory(string environment, bool testAuth = false, string? oidcAuthority = null, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string UserHeader = "X-Test-User";
    public const string ScimToken = "test-scim-token-0123456789abcdef0123456789";
    // A migrated database on the suite's provider (WORKBENCH_TEST_DB): a SQLite file unless PostgreSQL was chosen.
    private readonly (string Provider, string ConnectionString) _database = TestDb.Provider == TestDatabase.PostgreSql
        ? ("PostgreSQL", PostgresTemplates.NewDatabase())
        : ("Sqlite", SqliteTemplates.NewDatabase());

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Database:Provider", _database.Provider);
        builder.UseSetting("ConnectionStrings:Workbench", _database.ConnectionString);
        builder.UseSetting("Costpoint:Source", "None");
        builder.UseSetting("Storage:Root", Path.Combine(Path.GetTempPath(), $"workbench-blobs-{Guid.NewGuid():N}"));

        // A configuration the production startup check accepts: TLS at a proxy, a SCIM token, SQLite allowed for tests,
        // Data Protection keys encrypted with a test certificate.
        builder.UseSetting("Security:TlsTerminatedByProxy", "true");
        builder.UseSetting("Scim:BearerToken", ScimToken);
        builder.UseSetting("Database:AllowSqliteInProduction", "true");
        builder.UseSetting("DataProtection:CertificatePath", Operations.TestCertificates.SharedPfx);
        builder.UseSetting("DataProtection:CertificatePassword", Operations.TestCertificates.Password);
        builder.UseSetting("Retention:NightlyJobEnabled", "false");
        builder.UseSetting("Integrations:DispatcherEnabled", "false"); // Tests run the dispatcher themselves, with a fake transport.
        foreach (var (key, value) in settings ?? new Dictionary<string, string>())
        {
            builder.UseSetting(key, value);
        }
        if (oidcAuthority is not null)
        {
            builder.UseSetting("Authentication:Oidc:Authority", oidcAuthority);
            builder.UseSetting("Authentication:Oidc:ClientId", "workbench");
        }

        if (testAuth)
        {
            builder.ConfigureTestServices(services =>
            {
                services.AddAuthentication().AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>("Test", null);
                services.PostConfigure<PolicySchemeOptions>(AuthenticationSetup.SelectorScheme, o =>
                    o.ForwardDefaultSelector = context => context.Request.Headers.ContainsKey(UserHeader) ? "Test"
                        : context.Request.Path.StartsWithSegments("/api") && HasApiKey(context.Request) ? "ApiKey"
                        : "Cookies");
            });
        }
    }

    /// <summary>Integration API keys (Bearer wbk_… or X-Api-Key) still reach the real ApiKey handler under test auth.</summary>
    private static bool HasApiKey(Microsoft.AspNetCore.Http.HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer wbk_", StringComparison.Ordinal) || request.Headers.ContainsKey("X-Api-Key");

    public HttpClient CreateClientAs(string? email)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(UserHeader, email);
        }

        return client;
    }


    private sealed class HeaderAuthHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(UserHeader, out var email))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identity = new ClaimsIdentity([new Claim("email", email.ToString()), new Claim("name", email.ToString())], "Test", "name", "role");
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), "Test")));
        }
    }
}

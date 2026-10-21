using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Infrastructure.Persistence;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>
/// The real web host on a migrated test database (see <see cref="TestDb"/>); Development seeds the sample data. With
/// <c>testAuth</c>, requests carrying <c>X-Test-User: email</c> are signed in as that user, standing in for the
/// cookie or bearer token.
/// </summary>
/// <remarks>
/// Seeding the sample data takes most of a minute, so a Development host without extra <c>settings</c> starts from a
/// copy of a database (and blob folder) seeded once per run (see <see cref="SeededWebTemplate"/>); the startup seed then
/// finds the data there and does nothing. Hosts with <c>settings</c> may change what is seeded, so they seed their own.
/// </remarks>
internal sealed class WorkbenchWebFactory(string environment, bool testAuth = false, string? oidcAuthority = null, IReadOnlyDictionary<string, string>? settings = null)
    : WebApplicationFactory<Program>
{
    public const string UserHeader = "X-Test-User";
    public const string ScimToken = "test-scim-token-0123456789abcdef0123456789";
    private readonly bool _seeded = environment == "Development" && settings is null && !SeededWebTemplate.Seeding;
    // A migrated database on the suite's provider (WORKBENCH_TEST_DB): a SQLite file unless PostgreSQL was chosen.
    private (string Provider, string ConnectionString)? _database;
    private string? _storageRoot;
    private bool _keepDatabase = SeededWebTemplate.Seeding;

    public (string Provider, string ConnectionString) Database => _database ??= _seeded
        ? SeededWebTemplate.NewDatabase()
        : TestDb.Provider == TestDatabase.PostgreSql ? ("PostgreSQL", PostgresTemplates.NewDatabase()) : ("Sqlite", SqliteTemplates.NewDatabase());

    public string StorageRoot => _storageRoot ??= _seeded
        ? SeededWebTemplate.NewStorageRoot()
        : Path.Combine(Path.GetTempPath(), $"workbench-blobs-{Guid.NewGuid():N}");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(environment);
        builder.UseSetting("Database:Provider", Database.Provider);
        builder.UseSetting("ConnectionStrings:Workbench", Database.ConnectionString);
        builder.UseSetting("Costpoint:Source", "None");
        builder.UseSetting("Storage:Root", StorageRoot);

        // A configuration the production startup check accepts: TLS at a proxy, a SCIM token, SQLite allowed for tests,
        // Data Protection keys encrypted with a test certificate.
        builder.UseSetting("Security:TlsTerminatedByProxy", "true");
        builder.UseSetting("Scim:BearerToken", ScimToken);
        builder.UseSetting("Database:AllowSqliteInProduction", "true");
        builder.UseSetting("DataProtection:CertificatePath", Operations.TestCertificates.SharedPfx);
        builder.UseSetting("DataProtection:CertificatePassword", Operations.TestCertificates.Password);
        builder.UseSetting("Retention:NightlyJobEnabled", "false");
        builder.UseSetting("Integrations:DispatcherEnabled", "false"); // Tests run the dispatcher themselves, with a fake transport.
        builder.UseSetting("Automations:BackgroundEnabled", "false"); // Tests run the automation processor themselves.
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

    /// <summary>Drops the host's database and blob folder once the host has stopped (not the seeded template's).</summary>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing || _keepDatabase || _database is not { } database)
        {
            return;
        }

        _keepDatabase = true; // once
        DatabaseReaper.Drop(database.Provider == "PostgreSQL" ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite, database.ConnectionString);
        try
        {
            Directory.Delete(StorageRoot, recursive: true);
        }
        catch (IOException)
        {
        }
    }

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

/// <summary>
/// The Development sample data, seeded once per test run by a real Development host, as a database template and a blob
/// folder that <see cref="WorkbenchWebFactory"/> copies for each host. The seed reads the clock, so all hosts in a run
/// see the same "today" the template was seeded on.
/// </summary>
internal static class SeededWebTemplate
{
    private static readonly Lazy<(string Provider, string Template, string Storage)> Template = new(Create);
    [ThreadStatic] private static bool _seeding;

    /// <summary>True on the thread that is seeding the template (its host seeds itself).</summary>
    public static bool Seeding => _seeding;

    public static (string Provider, string ConnectionString) NewDatabase()
    {
        var (provider, template, _) = Template.Value;
        return provider == "PostgreSQL"
            ? (provider, PostgresTemplates.NewDatabase(template))
            : (provider, SqliteTemplates.NewDatabase(template));
    }

    public static string NewStorageRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"workbench-blobs-{Guid.NewGuid():N}");
        var source = Template.Value.Storage;
        foreach (var file in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [])
        {
            var target = Path.Combine(root, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        Directory.CreateDirectory(root);
        return root;
    }

    private static (string, string, string) Create()
    {
        _seeding = true;
        try
        {
            var factory = new WorkbenchWebFactory("Development");
            var (provider, connectionString) = factory.Database;
            var storage = factory.StorageRoot;
            using (factory)
            {
                _ = factory.Services; // starts the host, which migrates and seeds
            }

            if (provider == "PostgreSQL")
            {
                // CREATE DATABASE ... TEMPLATE needs the template to have no open connections.
                Npgsql.NpgsqlConnection.ClearAllPools();
                return (provider, new Npgsql.NpgsqlConnectionStringBuilder(connectionString).Database!, storage);
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return (provider, new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource, storage);
        }
        finally
        {
            _seeding = false;
        }
    }
}

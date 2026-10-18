using System.Net.Http.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Tenancy;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Tenancy;

/// <summary>
/// The real web host in MultiTenant mode: a catalog plus one database per tenant, on the suite's provider (SQLite files,
/// or PostgreSQL databases when <c>WORKBENCH_TEST_DB=PostgreSql</c>). Development provisions <c>acme</c> and <c>beta</c> with
/// the sample data on startup; requests carry <c>X-Test-User</c> for the signed-in user.
/// </summary>
internal sealed class MultiTenantWeb : IAsyncDisposable
{
    public const string HostAdmin = "host@workbench.local";
    public const string DevAdmin = "dev@workbench.local";

    private MultiTenantWeb(WorkbenchWebFactory factory, string baseDomain)
    {
        Factory = factory;
        BaseDomain = baseDomain;
    }

    public WorkbenchWebFactory Factory { get; }

    public string BaseDomain { get; }

    public IServiceProvider Services => Factory.Services;

    /// <summary>Development: tenants acme and beta exist with the sample data, the X-Tenant header works.</summary>
    public static Task<MultiTenantWeb> DevelopmentAsync() => CreateAsync("Development", "localhost", ["acme", "beta"]);

    /// <summary>
    /// Staging (the production startup checks apply): nothing is migrated on startup, so this runs the migrator the way
    /// <c>--migrate</c> does and provisions acme and beta through the host admin's provisioner.
    /// </summary>
    public static async Task<MultiTenantWeb> StagingAsync()
    {
        var web = await CreateAsync("Staging", "workbench.test", []);
        await web.Services.GetRequiredService<Workbench.Infrastructure.Persistence.DatabaseMigrator>().MigrateAsync();
        var provisioner = web.Services.GetRequiredService<TenantProvisioner>();
        await provisioner.CreateAsync(new CreateTenantRequest("acme", "Acme Federal", "admin@acme.example"), HostAdmin);
        await provisioner.CreateAsync(new CreateTenantRequest("beta", "Beta Systems", "admin@beta.example"), HostAdmin);
        return web;
    }

    private static Task<MultiTenantWeb> CreateAsync(string environment, string baseDomain, string[] developmentTenants)
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var settings = new Dictionary<string, string>
        {
            ["Tenancy:Mode"] = "MultiTenant",
            ["Tenancy:BaseDomain"] = baseDomain,
            ["Tenancy:HostAdmins:0"] = HostAdmin,
            ["Database:MigrateOnStartup"] = environment == "Development" ? "true" : "false",
        };
        for (var i = 0; i < developmentTenants.Length; i++)
        {
            settings[$"Tenancy:DevelopmentTenants:{i}"] = developmentTenants[i];
        }

        if (TestDb.Provider == TestDatabase.PostgreSql)
        {
            settings["Database:Provider"] = "PostgreSQL";
            settings["ConnectionStrings:Catalog"] = PostgresTemplates.ConnectionString($"{PostgresTemplates.RunId}_{id}_catalog");
            settings["Tenancy:TenantConnectionString"] = PostgresTemplates.ConnectionString($"{PostgresTemplates.RunId}_{id}_{{tenant}}");
        }
        else
        {
            var folder = Path.Combine(Path.GetTempPath(), $"workbench-tenants-{id}");
            settings["Database:Provider"] = "Sqlite";
            settings["ConnectionStrings:Catalog"] = $"Data Source={Path.Combine(folder, "catalog.db")};Pooling=False";
            settings["Tenancy:TenantConnectionString"] = $"Data Source={Path.Combine(folder, "{tenant}.db")};Pooling=False";
        }

        var factory = new WorkbenchWebFactory(environment, testAuth: true,
            oidcAuthority: environment == "Development" ? null : "https://login.example.test/", settings: settings);
        _ = factory.Server; // start the host (and, in Development, provision the tenants)
        return Task.FromResult(new MultiTenantWeb(factory, baseDomain));
    }

    /// <summary>A client for a tenant's address (or the host site when <paramref name="tenant"/> is null), signed in as <paramref name="email"/>.</summary>
    public HttpClient Client(string? tenant, string? email = DevAdmin)
    {
        var client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, BaseAddress = Url(tenant) });
        if (email is not null)
        {
            client.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, email);
        }

        return client;
    }

    public Uri Url(string? tenant) => new(tenant is null ? $"http://{BaseDomain}/" : $"http://{tenant}.{BaseDomain}/");

    /// <summary>A SignalR connection to a tenant's hub (long polling through the test server).</summary>
    public HubConnection Hub(string tenant, string path, string email = DevAdmin) =>
        new HubConnectionBuilder()
            .WithUrl(new Uri(Url(tenant), path.TrimStart('/')), o =>
            {
                o.HttpMessageHandlerFactory = _ => Factory.Server.CreateHandler();
                o.Transports = HttpTransportType.LongPolling;
                o.Headers[WorkbenchWebFactory.UserHeader] = email;
            })
            .Build();

    /// <summary>Runs <paramref name="work"/> as the server would for <paramref name="slug"/>'s tenant.</summary>
    public async Task<T> InTenantAsync<T>(string slug, Func<IServiceProvider, Task<T>> work)
    {
        var tenant = (await Services.GetRequiredService<TenantCatalog>().FindAsync(slug))!.Info;
        using (TenantScope.Enter(tenant))
        {
            await using var scope = Services.CreateAsyncScope();
            return await work(scope.ServiceProvider);
        }
    }

    public static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}

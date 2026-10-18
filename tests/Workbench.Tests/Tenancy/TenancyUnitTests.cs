using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.ApiClient;
using Workbench.Application.Collaboration;
using Workbench.Application.Notifications;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Infrastructure;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;
using Workbench.Web.Security;

namespace Workbench.Tests.Tenancy;

public sealed class TenancyUnitTests
{
    private static readonly TenantInfo Acme = new(Guid.CreateVersion7(), "acme", "Acme");
    private static readonly TenantInfo Beta = new(Guid.CreateVersion7(), "beta", "Beta");

    [Fact]
    public void Data_protected_for_one_tenant_only_unprotects_in_that_tenant()
    {
        var provider = new TenantDataProtectionProvider(new EphemeralDataProtectionProvider());
        var protector = provider.CreateProtector("Workbench.Purchasing.VendorTaxId.v1"); // created once, like a singleton's

        string secret;
        using (TenantScope.Enter(Acme))
        {
            secret = protector.Protect("123456789");
            Assert.Equal("123456789", protector.Unprotect(secret));
        }

        using (TenantScope.Enter(Beta))
        {
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(secret));
        }

        using (TenantScope.Enter(null))
        {
            Assert.ThrowsAny<CryptographicException>(() => protector.Unprotect(secret));
        }

        // The catalog's own secrets use the root provider, the same from any tenant.
        var root = TenantDataProtectionProvider.RootOf(provider).CreateProtector(TenantCatalog.ConnectionStringPurpose);
        string connection;
        using (TenantScope.Enter(Acme))
        {
            connection = root.Protect("Host=db;Database=acme");
        }

        using (TenantScope.Enter(Beta))
        {
            Assert.Equal("Host=db;Database=acme", root.Unprotect(connection));
        }
    }

    [Fact]
    public async Task In_process_live_updates_and_notifications_stay_in_their_tenant()
    {
        var sheets = new SheetEventHub();
        var sheetId = Guid.CreateVersion7();
        var betaChanges = 0;
        var acmeChanges = 0;
        using (TenantScope.Enter(Beta))
        {
            await sheets.SubscribeAsync(sheetId, _ => { betaChanges++; return Task.CompletedTask; });
        }

        using (TenantScope.Enter(Acme))
        {
            await sheets.SubscribeAsync(sheetId, _ => { acmeChanges++; return Task.CompletedTask; });
            await sheets.PublishAsync(new SheetChange(sheetId, SheetChangeKind.ItemUpdated, null, null, "dev@workbench.local", DateTime.UtcNow));
        }

        Assert.Equal((1, 0), (acmeChanges, betaChanges));

        var notifications = new NotificationHub();
        var heardInBeta = new List<NotificationDto>();
        using (TenantScope.Enter(Beta))
        {
            notifications.Subscribe("dev@workbench.local", n => { heardInBeta.Add(n); return Task.CompletedTask; });
        }

        using (TenantScope.Enter(Acme))
        {
            await notifications.PublishAsync("DEV@workbench.local", new NotificationDto(Guid.NewGuid(), "x", "Acme only", "", null, null, null, DateTime.UtcNow, null));
        }

        Assert.Empty(heardInBeta);
        Assert.Equal("dev@workbench.local", TenantScope.Qualify("dev@workbench.local"));
        Assert.Equal("acme|dev@workbench.local", Qualified(Acme, "dev@workbench.local"));
    }

    [Fact]
    public void MultiTenant_production_needs_invites_host_admins_and_a_real_domain_and_single_tenant_is_unchanged()
    {
        var baseline = new Dictionary<string, string?>
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["Authentication:Oidc:ClientId"] = "workbench",
            ["Security:TlsTerminatedByProxy"] = "true",
            ["Database:Provider"] = "PostgreSQL",
            ["Scim:BearerToken"] = new string('x', 40),
        };
        Assert.Empty(Problems("Production", baseline));

        var multi = new Dictionary<string, string?>(baseline) { ["Tenancy:Mode"] = "MultiTenant", ["Authentication:RequireInvite"] = "false", ["Tenancy:BaseDomain"] = "localhost" };
        var problems = Problems("Production", multi);
        Assert.Contains(problems, p => p.Contains("RequireInvite"));
        Assert.Contains(problems, p => p.Contains("Tenancy:HostAdmins"));
        Assert.Contains(problems, p => p.Contains("Tenancy:BaseDomain"));

        var good = new Dictionary<string, string?>(baseline)
        {
            ["Tenancy:Mode"] = "MultiTenant",
            ["Tenancy:BaseDomain"] = "workbench.example",
            ["Tenancy:HostAdmins:0"] = "ops@workbench.example",
            ["Scim:BearerToken"] = "", // SCIM is off in MultiTenant mode, so its token isn't checked
        };
        Assert.Empty(Problems("Production", good));
        Assert.Contains(Problems("Production", new Dictionary<string, string?>(good) { ["Database:Provider"] = "Sqlite" }), p => p.Contains("Sqlite in Production"));
        Assert.Empty(Problems("Development", new Dictionary<string, string?> { ["Tenancy:Mode"] = "MultiTenant" }));
    }

    [Fact]
    public async Task A_development_database_made_by_EnsureCreated_is_set_aside_and_migrated_fresh()
    {
        var folder = Directory.CreateTempSubdirectory("workbench-legacy-").FullName;
        var path = Path.Combine(folder, "workbench.db");
        await using (var legacy = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.Sqlite, $"Data Source={path};Pooling=False")))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await using var services = Services("Development", $"Data Source={path};Pooling=False");
        await services.GetRequiredService<DatabaseMigrator>().MigrateAsync();

        Assert.Single(Directory.GetFiles(folder, "workbench.db.pre-migrations-*.bak"));
        await using var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync();
        Assert.NotEmpty(await db.Database.GetAppliedMigrationsAsync());

        // Outside Development it refuses rather than touching the data.
        var other = Path.Combine(folder, "prod.db");
        await using (var legacy = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.Sqlite, $"Data Source={other};Pooling=False")))
        {
            await legacy.Database.EnsureCreatedAsync();
        }

        await using var production = Services("Production", $"Data Source={other};Pooling=False");
        await Assert.ThrowsAsync<InvalidOperationException>(() => production.GetRequiredService<DatabaseMigrator>().MigrateAsync());
        SqliteConnection.ClearAllPools();
        Directory.Delete(folder, recursive: true);
    }

    [Fact]
    public void The_desktop_client_connects_to_its_tenants_address()
    {
        const string fallback = "https://localhost:7154/";
        Assert.Equal(new Uri("https://localhost:7154/"), WorkbenchServerAddress.Resolve(null, null, null, fallback));
        Assert.Equal(new Uri("https://acme.workbench.example/"), WorkbenchServerAddress.Resolve("https://acme.workbench.example", null, null, fallback));
        Assert.Equal(new Uri("https://acme.workbench.example/"), WorkbenchServerAddress.Resolve(null, "https://workbench.example/", "Acme", fallback));
        Assert.Throws<ArgumentException>(() => WorkbenchServerAddress.Resolve(null, "https://workbench.example/", "a.b", fallback));
    }

    private static string Qualified(TenantInfo tenant, string key)
    {
        using (TenantScope.Enter(tenant))
        {
            return TenantScope.Qualify(key);
        }
    }

    private static ServiceProvider Services(string environment, string connectionString)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = "Sqlite",
            ["ConnectionStrings:Workbench"] = connectionString,
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IHostEnvironment>(new FakeEnvironment(environment));
        services.AddLogging();
        services.AddWorkbenchInfrastructure(configuration);
        return services.BuildServiceProvider();
    }

    private static IReadOnlyList<string> Problems(string environment, Dictionary<string, string?> values) =>
        ProductionConfigurationCheck.FindProblems(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new FakeEnvironment(environment));

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Workbench.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

public sealed class TenantLinkTests
{
    [Fact]
    public void Email_links_point_at_the_current_tenants_address()
    {
        var options = new Workbench.Application.Notifications.NotificationOptions { PublicBaseUrl = "https://{tenant}.workbench.example" };
        Assert.Null(options.PublicBaseUrl);
        using (TenantScope.Enter(new TenantInfo(Guid.CreateVersion7(), "acme", "Acme")))
        {
            Assert.Equal("https://acme.workbench.example", options.PublicBaseUrl);
        }

        Assert.Equal("https://workbench.example", new Workbench.Application.Notifications.NotificationOptions { PublicBaseUrl = "https://workbench.example" }.PublicBaseUrl);
    }
}

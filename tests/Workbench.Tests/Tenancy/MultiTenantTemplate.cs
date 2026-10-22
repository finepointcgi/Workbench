using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;
using Workbench.Web.Security;

namespace Workbench.Tests.Tenancy;

/// <summary>
/// The Development multi-tenant host's databases (the catalog, acme and beta with the sample data) and blob folder,
/// seeded once per run by a real host and copied for each <see cref="MultiTenantWeb.DevelopmentAsync"/>: seeding two
/// tenants takes minutes on CI. The catalog is copied with the tenants, so tenant Ids (part of every tenant Data
/// Protection purpose) and the key ring (kept in the catalog) stay the same and values encrypted at seed time still
/// decrypt. Only the catalog's encrypted tenant connection strings are rewritten to point at the copies.
/// </summary>
internal static class MultiTenantTemplate
{
    private static readonly Lazy<(string Id, string Storage, string[] Tenants)> Template = new(Create);

    public static MultiTenantWeb NewHost()
    {
        var (template, templateStorage, tenants) = Template.Value;
        var id = MultiTenantWeb.NewId();
        foreach (var database in tenants.Prepend("catalog"))
        {
            Copy(template, id, database);
        }

        Retarget(id);
        var storage = Path.Combine(Path.GetTempPath(), $"workbench-blobs-{id}");
        CopyFolder(templateStorage, storage);
        return MultiTenantWeb.Start("Development", "localhost", ["acme", "beta"], id, storage);
    }

    /// <summary>Deletes host <paramref name="id"/>'s catalog and tenant databases and its blob folder (best effort).</summary>
    public static void Delete(string id, string storageRoot)
    {
        try
        {
            if (TestDb.Provider == TestDatabase.PostgreSql)
            {
                PostgresTemplates.DropDatabases($"datname LIKE '{PostgresTemplates.RunId}_{id}_%'");
            }
            else
            {
                SqliteConnection.ClearAllPools();
                Directory.Delete(MultiTenantWeb.SqliteFolder(id), recursive: true);
            }

            Directory.Delete(storageRoot, recursive: true);
        }
        catch (Exception ex) when (ex is NpgsqlException or IOException or UnauthorizedAccessException)
        {
        }
    }

    private static (string, string, string[]) Create()
    {
        var id = MultiTenantWeb.NewId();
        var storage = Path.Combine(Path.GetTempPath(), $"workbench-blobs-{id}");
        var web = MultiTenantWeb.Start("Development", "localhost", ["acme", "beta"], id, storage);
        var tenants = web.Services.GetRequiredService<TenantCatalog>().ListAsync().GetAwaiter().GetResult()
            .Select(t => t.Slug.Replace('-', '_')).ToArray();
        web.Factory.Dispose(); // keeps the databases and the blob folder

        // Copying needs the template databases closed (CREATE DATABASE ... TEMPLATE refuses open connections).
        NpgsqlConnection.ClearAllPools();
        SqliteConnection.ClearAllPools();
        return (id, storage, tenants);
    }

    private static void Copy(string template, string id, string database)
    {
        if (TestDb.Provider == TestDatabase.PostgreSql)
        {
            PostgresTemplates.Execute($"CREATE DATABASE \"{PostgresTemplates.RunId}_{id}_{database}\" TEMPLATE \"{PostgresTemplates.RunId}_{template}_{database}\"");
            return;
        }

        var folder = MultiTenantWeb.SqliteFolder(id);
        Directory.CreateDirectory(folder);
        File.Copy(Path.Combine(MultiTenantWeb.SqliteFolder(template), $"{database}.db"), Path.Combine(folder, $"{database}.db"));
    }

    /// <summary>
    /// Points the copied catalog's tenants at the copied databases, encrypting the connection strings with the copied key
    /// ring the way the host's <see cref="TenantCatalog"/> does (the root provider, the same application name and certificate).
    /// </summary>
    private static void Retarget(string id)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["DataProtection:CertificatePath"] = Operations.TestCertificates.SharedPfx,
            ["DataProtection:CertificatePassword"] = Operations.TestCertificates.Password,
        }).Build();
        var provider = TestDb.Provider == TestDatabase.PostgreSql ? DatabaseProvider.PostgreSql : DatabaseProvider.Sqlite;
        var services = new ServiceCollection();
        services.AddSingleton(new TenancyOptions { Mode = TenancyMode.MultiTenant });
        services.AddDbContextFactory<CatalogDbContext>(o => o.Use(provider, MultiTenantWeb.CatalogConnectionString(id)));
        services.AddWorkbenchDataProtection(configuration, null!);
        using var serviceProvider = services.BuildServiceProvider();
        var protector = serviceProvider.GetRequiredService<IDataProtectionProvider>().CreateProtector(TenantCatalog.ConnectionStringPurpose);
        using var db = serviceProvider.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContext();
        foreach (var tenant in db.Tenants)
        {
            tenant.ProtectedConnectionString = protector.Protect(
                MultiTenantWeb.TenantConnectionString(id).Replace("{tenant}", tenant.Slug.Replace('-', '_'), StringComparison.Ordinal));
        }

        db.SaveChanges();
    }

    private static void CopyFolder(string source, string target)
    {
        foreach (var file in Directory.Exists(source) ? Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories) : [])
        {
            var copy = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            File.Copy(file, copy);
        }

        Directory.CreateDirectory(target);
    }
}

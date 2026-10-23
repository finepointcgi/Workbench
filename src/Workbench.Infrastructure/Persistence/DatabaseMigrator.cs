using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Applies EF Core migrations: the single database (SingleTenant), or the catalog and then every tenant database
/// (MultiTenant). Used on startup when <c>Database:MigrateOnStartup</c> is true and by <c>Workbench.Web --migrate</c>.
/// </summary>
public sealed class DatabaseMigrator(IServiceProvider services, TenancyOptions tenancy, ILogger<DatabaseMigrator> logger)
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        if (!tenancy.IsMultiTenant)
        {
            await MigrateCurrentAsync(cancellationToken);
            return;
        }

        await MigrateCatalogAsync(cancellationToken);
        await using var db = await services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(cancellationToken);
        var tenants = await db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.Active || t.Status == TenantStatus.Suspended)
            .OrderBy(t => t.Slug).ToListAsync(cancellationToken);
        foreach (var tenant in tenants)
        {
            using (TenantScope.Enter(new TenantInfo(tenant.Id, tenant.Slug, tenant.DisplayName)))
            {
                await MigrateCurrentAsync(cancellationToken);
            }
        }
    }

    public async Task MigrateCatalogAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(cancellationToken);
        EnsureSqliteFolder(db);
        await db.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Migrates the database of the ambient tenant (or the single database).</summary>
    public async Task MigrateCurrentAsync(CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(cancellationToken))
        {
            EnsureSqliteFolder(db);
            if (!await IsLegacyAsync(db, cancellationToken))
            {
                var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
                if (pending.Count > 0)
                {
                    logger.LogInformation("Applying {Count} migration(s) to {Database}: {Migrations}", pending.Count, Describe(), string.Join(", ", pending));
                }

                await db.Database.MigrateAsync(cancellationToken);
                await MigrateDataAsync(db, cancellationToken);
                return;
            }

            if (!IsDevelopment() || !db.Database.IsSqlite())
            {
                throw new InvalidOperationException(
                    $"The database for {Describe()} has tables but no migration history (it was made with EnsureCreated before migrations existed). "
                    + "Back it up and recreate it, or baseline it by hand; Workbench won't migrate over it.");
            }
        }

        // Development SQLite made by EnsureCreated: keep it aside and start over (the sample data is re-seeded).
        var path = new SqliteConnectionStringBuilder(factory.CreateDbContext().Database.GetConnectionString()).DataSource;
        SqliteConnection.ClearAllPools();
        var backup = $"{path}.pre-migrations-{DateTime.UtcNow:yyyyMMddHHmmss}.bak";
        File.Move(path, backup);
        foreach (var suffix in new[] { "-wal", "-shm" })
        {
            File.Delete(path + suffix);
        }

        logger.LogWarning("Moved the pre-migrations development database to {Backup} and created a new one.", backup);
        await using var fresh = await factory.CreateDbContextAsync(cancellationToken);
        await fresh.Database.MigrateAsync(cancellationToken);
    }

    /// <summary>Data migrations that run after the schema is current. Each is idempotent.</summary>
    private async Task MigrateDataAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        // Sheet rules became sheet automations (Admin › Automation, one builder).
        var rules = await Application.Automations.SheetRuleMigration.MigrateAsync(db, cancellationToken);
        if (rules > 0)
        {
            logger.LogInformation("Moved {Count} sheet rule(s) in {Database} into sheet automations.", rules, Describe());
        }
    }

    /// <summary>
    /// Development: in MultiTenant mode, provisions <c>Tenancy:DevelopmentTenants</c> that don't exist yet, then seeds the
    /// sample data into every active tenant (or the single database).
    /// </summary>
    public async Task SeedDevelopmentAsync(CancellationToken cancellationToken = default)
    {
        if (!tenancy.IsMultiTenant)
        {
            await services.SeedSampleDataAsync(cancellationToken);
            return;
        }

        var provisioner = services.GetRequiredService<TenantProvisioner>();
        foreach (var slug in tenancy.DevelopmentTenants)
        {
            await provisioner.EnsureDevelopmentTenantAsync(slug, cancellationToken);
        }

        await services.GetRequiredService<ITenantJobRunner>().ForEachTenantAsync(ct => services.SeedSampleDataAsync(ct), cancellationToken);
    }

    private static async Task<bool> IsLegacyAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        var creator = db.GetService<IRelationalDatabaseCreator>();
        return await creator.ExistsAsync(cancellationToken)
            && await creator.HasTablesAsync(cancellationToken)
            && !await db.GetService<IHistoryRepository>().ExistsAsync(cancellationToken);
    }

    private static void EnsureSqliteFolder(DbContext db)
    {
        if (!db.Database.IsSqlite())
        {
            return;
        }

        var path = new SqliteConnectionStringBuilder(db.Database.GetConnectionString()).DataSource;
        var folder = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(folder) && path != ":memory:")
        {
            Directory.CreateDirectory(folder);
        }
    }

    private bool IsDevelopment() => services.GetService<IHostEnvironment>()?.IsDevelopment() ?? false;

    private static string Describe() => TenantScope.Current is { } t ? $"tenant {t.Slug}" : "the Workbench database";
}

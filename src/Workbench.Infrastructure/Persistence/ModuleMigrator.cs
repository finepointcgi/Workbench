using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Plugins;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Applies the migrations of the license modules loaded from the modules folder. Each module has its own <see cref="DbContext"/>, its own
/// migrations assembly per provider and its own history table (<c>__EFMigrationsHistory_&lt;module&gt;</c>), so a module's schema history is
/// independent of core's: a module can be added, updated or removed without touching core's migrations, and core's checks
/// (<c>has-pending-model-changes</c>) never see a module's tables. Runs right after the core migrations, against the same database,
/// for the single database and for every tenant's.
/// </summary>
internal static class ModuleMigrator
{
    public static async Task MigrateAsync(ModuleHost host, DatabaseProvider provider, string connectionString, ILogger logger, string database, CancellationToken cancellationToken)
    {
        foreach (var module in host.Modules.Where(m => m.Migrations is not null))
        {
            var migrations = module.Migrations!;
            var table = migrations.HistoryTableFor(module.Id);
            var options = new DbContextOptionsBuilder();
            switch (provider)
            {
                case DatabaseProvider.Sqlite:
                    options.UseSqlite(connectionString, o => o.MigrationsAssembly(migrations.SqliteMigrationsAssembly).MigrationsHistoryTable(table));
                    break;
                case DatabaseProvider.PostgreSql:
                    options.UseNpgsql(connectionString, o => o.MigrationsAssembly(migrations.PostgreSqlMigrationsAssembly).MigrationsHistoryTable(table));
                    break;
            }

            await using var db = (DbContext)Activator.CreateInstance(migrations.ContextType, options.Options)!;
            var pending = (await db.Database.GetPendingMigrationsAsync(cancellationToken)).ToList();
            if (pending.Count > 0)
            {
                logger.LogInformation("Applying {Count} migration(s) of module {Module} to {Database}: {Migrations}", pending.Count, module.Id, database, string.Join(", ", pending));
            }

            await db.Database.MigrateAsync(cancellationToken);
        }
    }
}

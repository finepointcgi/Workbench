using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Workbench.Infrastructure.Persistence;

public enum DatabaseProvider
{
    Sqlite,
    PostgreSql,
}

/// <summary>The configured provider, as a service.</summary>
public sealed record WorkbenchDatabase(DatabaseProvider Provider);

/// <summary>
/// The one place that turns <c>Database:Provider</c> and a connection string into EF Core options. Migrations live in
/// one assembly per provider (<c>Workbench.Migrations.PostgreSql</c>, <c>Workbench.Migrations.Sqlite</c>), because
/// EF Core migrations are provider-specific SQL and a single assembly can only hold one model snapshot per context.
/// </summary>
public static class DatabaseProviders
{
    public const string PostgreSqlMigrationsAssembly = "Workbench.Migrations.PostgreSql";
    public const string SqliteMigrationsAssembly = "Workbench.Migrations.Sqlite";

    /// <summary>PostgreSQL unless configured otherwise (Development's appsettings picks SQLite).</summary>
    public static DatabaseProvider FromConfiguration(IConfiguration configuration) => Parse(configuration["Database:Provider"]);

    public static DatabaseProvider Parse(string? value) => (value ?? "PostgreSQL").Trim().ToLowerInvariant() switch
    {
        "postgresql" or "postgres" or "npgsql" => DatabaseProvider.PostgreSql,
        "sqlite" => DatabaseProvider.Sqlite,
        _ => throw new InvalidOperationException($"Unsupported Database:Provider '{value}'. Use PostgreSQL or Sqlite."),
    };

    public static DbContextOptionsBuilder Use(this DbContextOptionsBuilder options, DatabaseProvider provider, string connectionString)
    {
        switch (provider)
        {
            case DatabaseProvider.Sqlite:
                options.UseSqlite(connectionString, o => o.MigrationsAssembly(SqliteMigrationsAssembly));
                break;
            case DatabaseProvider.PostgreSql:
                options.UseNpgsql(connectionString, o => o.MigrationsAssembly(PostgreSqlMigrationsAssembly));
                break;
        }

        if (Workbench.Plugins.ModuleHost.Current.HasModel)
        {
            // Modules add entities to the runtime model and keep their own migrations, so the core snapshot (checked in CI against the
            // model without modules) legitimately differs from this model. EF Core would refuse to migrate over that.
            options.ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning));
            options.ReplaceService<Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory, ModuleModelCacheKeyFactory>();
        }

        return options;
    }
}

/// <summary>Keeps one EF model per module set, so a host that loads different modules never reuses another's model.</summary>
internal sealed class ModuleModelCacheKeyFactory : Microsoft.EntityFrameworkCore.Infrastructure.IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), designTime, Workbench.Plugins.ModuleHost.Current.ModelKey);
}

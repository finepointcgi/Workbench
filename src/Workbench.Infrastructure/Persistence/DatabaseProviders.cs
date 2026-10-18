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

        return options;
    }
}

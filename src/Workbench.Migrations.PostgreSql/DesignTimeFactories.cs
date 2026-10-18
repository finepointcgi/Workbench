using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Migrations.PostgreSql;

/// <summary>
/// Used only by <c>dotnet ef</c> (scripts/new-migration.sh). Generating a migration never connects, so the connection
/// string just has to parse; <c>WORKBENCH_DESIGN_CONNECTION</c> overrides it for commands that do (e.g. <c>database update</c>).
/// </summary>
public sealed class WorkbenchDesignTimeFactory : IDesignTimeDbContextFactory<WorkbenchDbContext>
{
    public WorkbenchDbContext CreateDbContext(string[] args) => new(Options<WorkbenchDbContext>());

    internal static DbContextOptions<T> Options<T>()
        where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        builder.Use(DatabaseProvider.PostgreSql,
            Environment.GetEnvironmentVariable("WORKBENCH_DESIGN_CONNECTION") ?? "Host=localhost;Database=workbench_design;Username=workbench;Password=workbench");
        return builder.Options;
    }
}

public sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => new(WorkbenchDesignTimeFactory.Options<CatalogDbContext>());
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Migrations.Sqlite;

/// <summary>Used only by <c>dotnet ef</c> (scripts/new-migration.sh). <c>WORKBENCH_DESIGN_CONNECTION</c> overrides the database file.</summary>
public sealed class WorkbenchDesignTimeFactory : IDesignTimeDbContextFactory<WorkbenchDbContext>
{
    public WorkbenchDbContext CreateDbContext(string[] args) => new(Options<WorkbenchDbContext>());

    internal static DbContextOptions<T> Options<T>()
        where T : DbContext
    {
        var builder = new DbContextOptionsBuilder<T>();
        builder.Use(DatabaseProvider.Sqlite, Environment.GetEnvironmentVariable("WORKBENCH_DESIGN_CONNECTION") ?? "Data Source=workbench-design.db");
        return builder.Options;
    }
}

public sealed class CatalogDesignTimeFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    public CatalogDbContext CreateDbContext(string[] args) => new(WorkbenchDesignTimeFactory.Options<CatalogDbContext>());
}

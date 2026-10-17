using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Entity mappings live in <c>Configurations/</c>, one <see cref="IEntityTypeConfiguration{TEntity}"/>
/// per entity, and are picked up automatically. New entities need a configuration class, not edits here.
/// </summary>
public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options)
    : DbContext(options), IWorkbenchDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Sheet> Sheets => Set<Sheet>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<Item> Items => Set<Item>();

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkbenchDbContext).Assembly);
}

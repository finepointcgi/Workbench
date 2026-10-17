using Microsoft.EntityFrameworkCore;
using Workbench.Domain;

namespace Workbench.Application;

public interface IWorkbenchDbContext : IAsyncDisposable
{
    DbSet<Workspace> Workspaces { get; }
    DbSet<Sheet> Sheets { get; }
    DbSet<Column> Columns { get; }
    DbSet<Item> Items { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Creates a short-lived context per operation. Blazor Server circuits are long-lived,
/// so a scoped DbContext would be shared across concurrent UI events.
/// </summary>
public interface IWorkbenchDbContextFactory
{
    Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default);
}

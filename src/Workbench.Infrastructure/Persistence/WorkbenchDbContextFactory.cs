using Microsoft.EntityFrameworkCore;
using Workbench.Application;

namespace Workbench.Infrastructure.Persistence;

internal sealed class WorkbenchDbContextFactory(IDbContextFactory<WorkbenchDbContext> inner) : IWorkbenchDbContextFactory
{
    public async Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
        await inner.CreateDbContextAsync(cancellationToken);
}

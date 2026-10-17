using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests;

/// <summary>An isolated in-memory database per test.</summary>
internal static class TestDb
{
    public static IWorkbenchDbContextFactory CreateFactory()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContextFactory<WorkbenchDbContext>(o => o.UseInMemoryDatabase(databaseName));
        return new Factory(services.BuildServiceProvider().GetRequiredService<IDbContextFactory<WorkbenchDbContext>>());
    }

    private sealed class Factory(IDbContextFactory<WorkbenchDbContext> inner) : IWorkbenchDbContextFactory
    {
        public async Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            await inner.CreateDbContextAsync(cancellationToken);
    }
}

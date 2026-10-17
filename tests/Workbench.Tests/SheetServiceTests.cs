using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Sheets;
using Workbench.Contracts.Sheets;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests;

public sealed class SheetServiceTests
{
    private static SheetService CreateService()
    {
        var services = new ServiceCollection();
        var databaseName = Guid.NewGuid().ToString();
        services.AddDbContextFactory<WorkbenchDbContext>(o => o.UseInMemoryDatabase(databaseName));
        var provider = services.BuildServiceProvider();
        var factory = new TestDbContextFactory(provider.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>());
        return new SheetService(factory, TimeProvider.System);
    }

    [Fact]
    public async Task CreateAsync_creates_default_workspace_and_lists_sheet()
    {
        var service = CreateService();

        var created = await service.CreateAsync(new CreateSheetRequest("  Proposal schedule ", " ", true));
        var sheets = await service.ListAsync();

        Assert.Equal("Proposal schedule", created.Name);
        Assert.Null(created.Description);
        var listed = Assert.Single(sheets);
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal(SheetService.DefaultWorkspaceName, listed.Workspace);
        Assert.True(listed.SchedulingEnabled);
    }

    [Fact]
    public async Task CreateAsync_rejects_blank_name()
    {
        var service = CreateService();

        await Assert.ThrowsAsync<ArgumentException>(() => service.CreateAsync(new CreateSheetRequest(" ", null, false)));
    }

    private sealed class TestDbContextFactory(IDbContextFactory<WorkbenchDbContext> inner) : IWorkbenchDbContextFactory
    {
        public async Task<IWorkbenchDbContext> CreateAsync(CancellationToken cancellationToken = default) =>
            await inner.CreateDbContextAsync(cancellationToken);
    }
}

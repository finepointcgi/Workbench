using Workbench.Application.Sheets;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests;

public sealed class SheetServiceTests
{
    private static SheetService CreateService() => new(TestDb.CreateFactory(), TimeProvider.System);

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

}

using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Scheduling;

public sealed class DependencyServiceTests
{
    private readonly ItemService _items;
    private readonly SheetService _sheets;
    private readonly DependencyService _dependencies;

    public DependencyServiceTests()
    {
        var factory = TestDb.CreateFactory();
        _items = new ItemService(factory, TimeProvider.System);
        _sheets = new SheetService(factory, TimeProvider.System);
        _dependencies = new DependencyService(factory);
    }

    private async Task<Guid> NewSheetAsync() => (await _sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;

    private async Task<Guid> NewItemAsync(Guid sheetId, string title) => (await _items.CreateAsync(sheetId, new CreateItemRequest(title))).Id;

    [Fact]
    public async Task Rejects_a_link_that_closes_a_cycle()
    {
        var sheetId = await NewSheetAsync();
        var a = await NewItemAsync(sheetId, "A");
        var b = await NewItemAsync(sheetId, "B");
        var c = await NewItemAsync(sheetId, "C");
        await _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, b));
        await _dependencies.AddAsync(sheetId, new CreateDependencyRequest(b, c));

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _dependencies.AddAsync(sheetId, new CreateDependencyRequest(c, a)));

        Assert.Contains("circular", ex.Message);
        Assert.Equal(2, (await _dependencies.ListAsync(sheetId)).Count);
    }

    [Fact]
    public async Task Rejects_self_links_and_duplicates()
    {
        var sheetId = await NewSheetAsync();
        var a = await NewItemAsync(sheetId, "A");
        var b = await NewItemAsync(sheetId, "B");
        await _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, b));

        await Assert.ThrowsAsync<ArgumentException>(() => _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, a)));
        await Assert.ThrowsAsync<ArgumentException>(() => _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, b)));
    }

    [Fact]
    public async Task Rejects_items_from_another_sheet()
    {
        var sheetId = await NewSheetAsync();
        var otherSheetId = await NewSheetAsync();
        var a = await NewItemAsync(sheetId, "A");
        var foreign = await NewItemAsync(otherSheetId, "Elsewhere");

        await Assert.ThrowsAsync<ArgumentException>(() => _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, foreign)));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => _dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, Guid.NewGuid())));
    }

    [Fact]
    public async Task Remove_deletes_the_link()
    {
        var sheetId = await NewSheetAsync();
        var link = await _dependencies.AddAsync(sheetId, new CreateDependencyRequest(await NewItemAsync(sheetId, "A"), await NewItemAsync(sheetId, "B")));

        await _dependencies.RemoveAsync(sheetId, link.Id);

        Assert.Empty(await _dependencies.ListAsync(sheetId));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => _dependencies.RemoveAsync(sheetId, link.Id));
    }

    [Fact]
    public void Cycle_check_follows_long_paths_only_forward()
    {
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid(), d = Guid.NewGuid();
        (Guid, Guid)[] edges = [(a, b), (b, c), (c, d)];

        Assert.True(DependencyService.CreatesCycle(edges, d, a));
        Assert.False(DependencyService.CreatesCycle(edges, a, d));
        Assert.False(DependencyService.CreatesCycle(edges, d, Guid.NewGuid()));
    }

    [Fact]
    public async Task Deleting_an_item_cascades_to_its_links_and_baseline_entries_on_sqlite()
    {
        using var db = new SqliteTestDb();
        var sheets = new SheetService(db, TimeProvider.System);
        var items = new ItemService(db, TimeProvider.System);
        var dependencies = new DependencyService(db);
        var baselines = new BaselineService(db, TimeProvider.System);
        var sheetId = (await sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var a = (await items.CreateAsync(sheetId, new CreateItemRequest("A"))).Id;
        var b = (await items.CreateAsync(sheetId, new CreateItemRequest("B"))).Id;
        var c = (await items.CreateAsync(sheetId, new CreateItemRequest("C"))).Id;
        await dependencies.AddAsync(sheetId, new CreateDependencyRequest(a, b));
        await dependencies.AddAsync(sheetId, new CreateDependencyRequest(b, c));
        var baseline = await baselines.CaptureAsync(sheetId, new CaptureBaselineRequest("Plan"));

        await items.DeleteAsync(sheetId, b);

        Assert.Empty(await dependencies.ListAsync(sheetId));
        await using var context = await db.CreateAsync();
        Assert.Equal(0, context.Set<Domain.Scheduling.Dependency>().Count());
        Assert.Equal(2, context.Set<Domain.Scheduling.BaselineEntry>().Count(e => e.BaselineId == baseline.Id));
    }
}

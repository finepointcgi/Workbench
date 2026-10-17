using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests;

public sealed class ItemServiceTests
{
    private readonly ItemService _items;
    private readonly ColumnService _columns;
    private readonly SheetService _sheets;

    public ItemServiceTests()
    {
        var factory = TestDb.CreateFactory();
        _items = new ItemService(factory, TimeProvider.System);
        _columns = new ColumnService(factory, TimeProvider.System);
        _sheets = new SheetService(factory, TimeProvider.System);
    }

    private async Task<Guid> NewSheetAsync() => (await _sheets.CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;

    [Fact]
    public async Task Create_inserts_at_order_and_shifts_siblings()
    {
        var sheetId = await NewSheetAsync();
        var a = await _items.CreateAsync(sheetId, new CreateItemRequest("A"));
        var b = await _items.CreateAsync(sheetId, new CreateItemRequest("B"));
        var first = await _items.CreateAsync(sheetId, new CreateItemRequest("First", Order: 0));

        var titles = (await _items.ListAsync(sheetId)).OrderBy(i => i.Order).Select(i => i.Title);

        Assert.Equal(["First", "A", "B"], titles);
        Assert.Equal(0, first.Order);
    }

    [Fact]
    public async Task Move_under_descendant_is_rejected()
    {
        var sheetId = await NewSheetAsync();
        var parent = await _items.CreateAsync(sheetId, new CreateItemRequest("Parent"));
        var child = await _items.CreateAsync(sheetId, new CreateItemRequest("Child", parent.Id));

        await Assert.ThrowsAsync<ArgumentException>(() => _items.MoveAsync(sheetId, parent.Id, new MoveItemRequest(child.Id, 0)));
    }

    [Fact]
    public async Task Move_reparents_and_renumbers_both_sibling_lists()
    {
        var sheetId = await NewSheetAsync();
        var a = await _items.CreateAsync(sheetId, new CreateItemRequest("A"));
        var b = await _items.CreateAsync(sheetId, new CreateItemRequest("B"));
        var c = await _items.CreateAsync(sheetId, new CreateItemRequest("C"));

        await _items.MoveAsync(sheetId, b.Id, new MoveItemRequest(c.Id, 0));
        var all = await _items.ListAsync(sheetId);

        Assert.Equal([("A", 0), ("C", 1)], all.Where(i => i.ParentId is null).OrderBy(i => i.Order).Select(i => (i.Title, i.Order)));
        Assert.Equal(c.Id, all.Single(i => i.Title == "B").ParentId);
    }

    [Fact]
    public async Task Delete_removes_descendants()
    {
        var sheetId = await NewSheetAsync();
        var parent = await _items.CreateAsync(sheetId, new CreateItemRequest("Parent"));
        var child = await _items.CreateAsync(sheetId, new CreateItemRequest("Child", parent.Id));
        await _items.CreateAsync(sheetId, new CreateItemRequest("Grandchild", child.Id));
        await _items.CreateAsync(sheetId, new CreateItemRequest("Other"));

        await _items.DeleteAsync(sheetId, parent.Id);

        var remaining = Assert.Single(await _items.ListAsync(sheetId));
        Assert.Equal("Other", remaining.Title);
        Assert.Equal(0, remaining.Order);
    }

    [Fact]
    public async Task Update_rejects_finish_before_start()
    {
        var sheetId = await NewSheetAsync();
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Task"));

        await Assert.ThrowsAsync<ArgumentException>(() => _items.UpdateAsync(sheetId, item.Id,
            new UpdateItemRequest("Task", new DateOnly(2026, 11, 10), new DateOnly(2026, 11, 1), null, new Dictionary<string, string?>())));
    }

    [Fact]
    public async Task Deleting_a_column_clears_its_values()
    {
        var sheetId = await NewSheetAsync();
        var column = await _columns.CreateAsync(sheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Open", "Done"]));
        var item = await _items.CreateAsync(sheetId, new CreateItemRequest("Task", Values: new Dictionary<string, string?> { [column.Id.ToString()] = "Open" }));

        await _columns.DeleteAsync(sheetId, column.Id);

        Assert.Empty((await _items.ListAsync(sheetId)).Single(i => i.Id == item.Id).Values);
        Assert.Empty((await _sheets.GetAsync(sheetId))!.Columns);
    }

    [Fact]
    public async Task Unknown_sheet_throws_not_found()
    {
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => _items.ListAsync(Guid.NewGuid()));
    }
}

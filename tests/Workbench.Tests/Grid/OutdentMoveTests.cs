using Workbench.Application.Sheets;
using Workbench.Contracts.Sheets;

namespace Workbench.Tests.Grid;

/// <summary>The grid outdents a row by moving it to just after its parent; the service must honor that position.</summary>
public sealed class OutdentMoveTests
{
    [Fact]
    public async Task Moving_a_last_child_out_places_it_right_after_its_parent()
    {
        var factory = TestDb.CreateFactory();
        var items = new ItemService(factory, TimeProvider.System);
        var sheetId = (await new SheetService(factory, TimeProvider.System).CreateAsync(new CreateSheetRequest("Plan", null, true))).Id;
        var phase1 = await items.CreateAsync(sheetId, new CreateItemRequest("Phase 1"));
        await items.CreateAsync(sheetId, new CreateItemRequest("Phase 2"));
        await items.CreateAsync(sheetId, new CreateItemRequest("A", phase1.Id));
        await items.CreateAsync(sheetId, new CreateItemRequest("B", phase1.Id));
        var last = await items.CreateAsync(sheetId, new CreateItemRequest("C", phase1.Id));

        await items.MoveAsync(sheetId, last.Id, new MoveItemRequest(null, phase1.Order + 1));

        var topLevel = (await items.ListAsync(sheetId)).Where(i => i.ParentId is null).OrderBy(i => i.Order).Select(i => i.Title);
        Assert.Equal(["Phase 1", "C", "Phase 2"], topLevel);
    }
}

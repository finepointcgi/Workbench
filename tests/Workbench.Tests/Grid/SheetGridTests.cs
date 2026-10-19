using Workbench.Tests.Accessibility;
using System.Diagnostics;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Grid;
using Workbench.Ui.Views;

namespace Workbench.Tests.Grid;

public sealed class SheetGridTests
{
    private static readonly ColumnDto Hours = new(Guid.NewGuid(), "Planned hours", ColumnKind.Number, 0, [], null);
    private static readonly ColumnDto Budget = new(Guid.NewGuid(), "Budget", ColumnKind.Currency, 1, [], null);
    private static readonly ColumnDto Total = new(Guid.NewGuid(), "Total", ColumnKind.Number, 2, [], "=[Planned hours] * 2");

    [Fact]
    public async Task Editing_a_cell_saves_the_full_item_with_the_new_value()
    {
        await using var ctx = CreateContext(out var items);
        var phase = Item("Phase 1", order: 0);
        var task = Item("Kickoff", order: 0, parent: phase.Id, values: new() { [Hours.Id.ToString()] = "8", [Budget.Id.ToString()] = "1200" });
        var changed = 0;
        var grid = ctx.Render<SheetGridView>(p => p
            .Add(v => v.Sheet, Sheet())
            .Add(v => v.Items, [phase, task])
            .Add(v => v.Changed, () => changed++));

        grid.WaitForAssertion(() => Assert.Equal(2, grid.FindAll("tr.wb-row").Count));
        A11yAssert.Check(grid);
        grid.FindAll("tr.wb-row")[1].QuerySelector("td[data-col='1']")!.DoubleClick();
        grid.Find("input.wb-editor").Input("$1,234.50");
        grid.Find(".wb-grid").KeyDown("Enter");

        grid.WaitForAssertion(() => Assert.Single(items.Updates)); // saved asynchronously
        var update = Assert.Single(items.Updates);
        Assert.Equal(task.Id, update.ItemId);
        Assert.Equal("1234.5", update.Request.Values[Hours.Id.ToString()]);
        Assert.Equal("1200", update.Request.Values[Budget.Id.ToString()]);
        Assert.Equal("Kickoff", update.Request.Title);
        grid.WaitForAssertion(() => Assert.Equal(1, changed));
        Assert.Empty(grid.FindAll("input.wb-editor"));
    }

    [Fact]
    public async Task Escape_cancels_an_edit_and_formula_cells_are_read_only()
    {
        await using var ctx = CreateContext(out var items);
        var task = Item("Kickoff", order: 0);
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, [task]));

        grid.WaitForElement("td[data-col='0']").DoubleClick();
        grid.Find("input.wb-editor").Input("Renamed");
        grid.Find(".wb-grid").KeyDown("Escape");

        grid.Find("td[data-col='3']").DoubleClick();
        Assert.Empty(grid.FindAll("input.wb-editor"));
        Assert.Contains("wb-readonly", grid.Find("td[data-col='3']").ClassName);
        Assert.Empty(items.Updates);
    }

    [Fact]
    public async Task Renders_hierarchy_with_indentation_and_collapses_children()
    {
        await using var ctx = CreateContext(out _);
        var phase = Item("Phase 1", order: 0);
        var child = Item("Kickoff", order: 0, parent: phase.Id);
        var grandchild = Item("Agenda", order: 0, parent: child.Id);
        var phase2 = Item("Phase 2", order: 1);
        var grid = ctx.Render<SheetGridView>(p => p
            .Add(v => v.Sheet, Sheet())
            .Add(v => v.Items, [phase2, grandchild, child, phase]));

        grid.WaitForAssertion(() => Assert.Equal(4, grid.FindAll("tr.wb-row").Count));
        Assert.Equal(["Phase 1", "Kickoff", "Agenda", "Phase 2"], Titles(grid));
        Assert.Equal(["0px", "20px", "40px", "0px"], grid.FindAll(".wb-title-cell").Select(Indent));
        Assert.Contains("wb-parent", grid.FindAll("tr.wb-row")[0].ClassName);

        grid.FindAll(".wb-toggle")[0].Click();

        Assert.Equal(["Phase 1", "Phase 2"], Titles(grid));
        Assert.Equal($"sheets/{SheetId}?item={phase.Id}", grid.Find(".wb-title-link").GetAttribute("href"));
    }

    [Fact]
    public async Task Alt_shift_right_indents_under_the_previous_sibling()
    {
        await using var ctx = CreateContext(out var items);
        var first = Item("First", order: 0);
        var second = Item("Second", order: 1);
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, [first, second]));

        grid.WaitForAssertion(() => Assert.Equal(2, grid.FindAll("tr.wb-row").Count));
        grid.FindAll("tr.wb-row")[1].QuerySelector("td[data-col='0']")!.Click();
        grid.Find(".wb-grid").KeyDown(new Microsoft.AspNetCore.Components.Web.KeyboardEventArgs { Key = "ArrowRight", AltKey = true, ShiftKey = true });

        // The move is saved asynchronously: wait for it rather than racing it (this failed under parallel load).
        grid.WaitForAssertion(() => Assert.Single(items.Moves));
        var move = Assert.Single(items.Moves);
        Assert.Equal(second.Id, move.ItemId);
        Assert.Equal(new MoveItemRequest(first.Id, 0), move.Request);
    }

    [Fact]
    public async Task Renders_ten_thousand_items_without_rendering_every_row()
    {
        await using var ctx = CreateContext(out _);
        var many = new List<ItemDto>(10_000);
        for (var p = 0; p < 1_000; p++)
        {
            var parent = Item($"Group {p}", order: p);
            many.Add(parent);
            for (var c = 0; c < 9; c++)
            {
                many.Add(Item($"Task {p}.{c}", order: c, parent: parent.Id, values: new() { [Hours.Id.ToString()] = c.ToString() }));
            }
        }

        var stopwatch = Stopwatch.StartNew();
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, many));
        grid.WaitForAssertion(() => Assert.NotEmpty(grid.FindAll("tr.wb-row")));
        stopwatch.Stop();

        Assert.InRange(grid.FindAll("tr.wb-row").Count, 1, 200);
        Assert.Contains("10000 rows", grid.Markup);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Rendering took {stopwatch.Elapsed}.");
    }

    [Theory]
    [InlineData(WorkspaceRole.Viewer)]
    [InlineData(WorkspaceRole.Commenter)]
    public async Task Viewers_and_commenters_get_a_read_only_grid(WorkspaceRole role)
    {
        await using var ctx = CreateContext(out var items);
        var task = Item("Kickoff", order: 0, values: new() { [Hours.Id.ToString()] = "8" });
        var grid = ctx.Render<SheetGridView>(p => p
            .Add(v => v.Sheet, Sheet())
            .Add(v => v.Items, [task])
            .AddCascadingValue(new SheetAccess(role)));

        grid.WaitForElement("td[data-col='1']").DoubleClick();
        grid.Find(".wb-grid").KeyDown("Delete");

        Assert.Empty(grid.FindAll("input.wb-editor"));
        Assert.DoesNotContain("Add row", grid.Markup);
        Assert.Empty(grid.FindAll("button[aria-label='Add column']"));
        Assert.Contains("View only", grid.Markup);
        Assert.Empty(items.Updates);
    }

    private static readonly Guid SheetId = Guid.NewGuid();

    private static BunitContext CreateContext(out FakeItemService items)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        items = new FakeItemService();
        ctx.Services.AddSingleton<IItemService>(items);
        ctx.Services.AddSingleton<IColumnService>(new NoColumnService());
        return ctx;
    }

    private static SheetDetail Sheet() => new(
        new SheetSummary(SheetId, "Integrated Master Schedule", null, "Program Office", 0, false, null, DateTime.UtcNow),
        [Hours, Budget, Total]);

    private static ItemDto Item(string title, int order, Guid? parent = null, Dictionary<string, string?>? values = null) =>
        new(Guid.NewGuid(), parent, order, title, null, null, null, values ?? []);

    private static string[] Titles(IRenderedComponent<SheetGridView> grid) =>
        grid.FindAll(".wb-title-link").Select(a => a.TextContent).ToArray();

    private static string Indent(AngleSharp.Dom.IElement element) =>
        (element.GetAttribute("style") ?? string.Empty).Replace("padding-left:", string.Empty).Trim();

    private sealed class FakeItemService : IItemService
    {
        public List<(Guid ItemId, UpdateItemRequest Request)> Updates { get; } = [];

        public List<(Guid ItemId, MoveItemRequest Request)> Moves { get; } = [];

        public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ItemDto(Guid.NewGuid(), request.ParentId, request.Order ?? 0, request.Title, null, null, null, new Dictionary<string, string?>()));

        public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            Updates.Add((itemId, request));
            return Task.FromResult(new ItemDto(itemId, null, 0, request.Title, request.Start, request.Finish, request.CostpointWbsId, request.Values));
        }

        public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
        {
            Moves.Add((itemId, request));
            return Task.FromResult(new ItemDto(itemId, request.ParentId, request.Order, "moved", null, null, null, new Dictionary<string, string?>()));
        }

        public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoColumnService : IColumnService
    {
        public Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

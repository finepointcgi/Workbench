using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Grid;
using Workbench.Ui.Views;

namespace Workbench.Tests.Grid;

/// <summary>The grid as a spreadsheet: ranges, paste from Excel, fill down, clear, undo and redo, status pills, saved views.</summary>
public sealed class SheetGridSpreadsheetTests
{
    private static readonly Guid SheetId = Guid.NewGuid();
    private static readonly ColumnDto Hours = new(Guid.NewGuid(), "Hours", ColumnKind.Number, 0, [], null);
    private static readonly ColumnDto Budget = new(Guid.NewGuid(), "Budget", ColumnKind.Currency, 1, [], null);
    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 2, ["Not started", "Working on it", "Done"], null,
        OptionColors: ["light-gray", "amber", "green"]);

    // Columns: 0 Title, 1 Hours, 2 Budget, 3 Status, 4 WBS.
    private static SheetDetail Sheet() => new(
        new SheetSummary(SheetId, "Program plan", null, "Program Office", 0, false, null, DateTime.UtcNow), [Hours, Budget, Status]);

    [Fact]
    public async Task Pasting_an_excel_block_fills_cells_and_adds_rows_past_the_end()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("Design", "Build");
        var grid = Render(ctx, items);

        Cell(grid, 0, 1).Click();
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("8\t$1,200.50\r\n4\t(300)\r\n2\t50\r\n"));

        grid.WaitForAssertion(() => Assert.Single(items.Creates));
        Assert.Equal("8", items.Value("Design", Hours));
        Assert.Equal("1200.5", items.Value("Design", Budget));
        Assert.Equal("-300", items.Value("Build", Budget));
        var added = Assert.Single(items.Creates);
        Assert.Equal("2", added.Values![Hours.Id.ToString()]);
        Assert.Equal("50", added.Values[Budget.Id.ToString()]);
        Assert.Equal(2, items.Updates.Count); // one update per row, not per cell
    }

    [Fact]
    public async Task One_value_pasted_into_a_range_fills_it_and_bad_values_are_skipped()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("A", "B", "C");
        var grid = Render(ctx, items);

        Cell(grid, 0, 1).Click();
        Cell(grid, 2, 2).Click(new MouseEventArgs { ShiftKey = true });
        Assert.Equal(6, grid.FindAll("td.wb-in-range").Count);
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("7"));

        grid.WaitForAssertion(() => Assert.Equal(3, items.Updates.Count));
        Assert.All(["A", "B", "C"], t => Assert.Equal(("7", "7"), (items.Value(t, Hours), items.Value(t, Budget))));

        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("lots"));
        Assert.Equal(3, items.Updates.Count); // "lots" isn't a number: nothing saved
    }

    [Fact]
    public async Task Ctrl_d_fills_down_delete_clears_a_range_and_undo_redo_reverse_them()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("A", "B", "C");
        items.Set("A", Hours, "5");
        items.Set("B", Hours, "6");
        var grid = Render(ctx, items);

        Cell(grid, 0, 1).Click();
        Key(grid, "ArrowDown", shift: true);
        Key(grid, "ArrowDown", shift: true);
        Assert.Equal(3, grid.FindAll("td.wb-in-range").Count);
        Key(grid, "d", ctrl: true);
        grid.WaitForAssertion(() => Assert.Equal("5", items.Value("C", Hours)));
        Assert.Equal("5", items.Value("B", Hours));

        Key(grid, "Delete");
        grid.WaitForAssertion(() => Assert.Null(items.Value("A", Hours)));
        Assert.Null(items.Value("C", Hours));

        Key(grid, "z", ctrl: true); // undo the clear
        grid.WaitForAssertion(() => Assert.Equal("5", items.Value("C", Hours)));
        Key(grid, "z", ctrl: true); // undo the fill
        grid.WaitForAssertion(() => Assert.Equal("6", items.Value("B", Hours)));
        Assert.Null(items.Value("C", Hours));
        Key(grid, "y", ctrl: true); // redo the fill
        grid.WaitForAssertion(() => Assert.Equal("5", items.Value("B", Hours)));
        Key(grid, "z", ctrl: true, shift: true); // Ctrl+Shift+Z redoes too: the clear
        grid.WaitForAssertion(() => Assert.Null(items.Value("B", Hours)));
    }

    [Fact]
    public async Task Undo_takes_back_an_edit_added_rows_a_delete_and_an_indent()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("First", "Second");
        var grid = Render(ctx, items);

        // An edit through the cell editor.
        Cell(grid, 0, 0).DoubleClick();
        grid.Find("input.wb-editor").Input("Kickoff");
        Key(grid, "Enter");
        grid.WaitForAssertion(() => Assert.NotNull(items.Find("Kickoff")));
        Key(grid, "z", ctrl: true);
        grid.WaitForAssertion(() => Assert.NotNull(items.Find("First")));

        // Rows added by a paste go away again.
        Cell(grid, 1, 0).Click();
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("Second\r\nThird\r\nFourth"));
        grid.WaitForAssertion(() => Assert.Equal(4, items.All.Count));
        Key(grid, "z", ctrl: true);
        grid.WaitForAssertion(() => Assert.Equal(2, items.All.Count));

        // An indent is put back where it was.
        grid.Render(p => p.Add(v => v.Items, items.All));
        Cell(grid, 1, 0).Click();
        Key(grid, "ArrowRight", alt: true, shift: true);
        grid.WaitForAssertion(() => Assert.Equal(items.Find("First")!.Id, items.Find("Second")!.ParentId));
        grid.Render(p => p.Add(v => v.Items, items.All));
        Key(grid, "z", ctrl: true);
        grid.WaitForAssertion(() => Assert.Null(items.Find("Second")!.ParentId));
        Assert.Equal([("First", 0), ("Second", 1)], items.All.OrderBy(i => i.Order).Select(i => (i.Title, i.Order)));
    }

    [Fact]
    public async Task Status_cells_show_colored_pills_and_change_with_one_click()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("Design");
        items.Set("Design", Status, "Working on it");
        var grid = Render(ctx, items);

        var pill = grid.Find("[data-testid=status-pill]");
        Assert.Equal("Working on it", pill.TextContent); // the label always shows: color is never the only signal
        Assert.Contains($"background:{StatusColors.Amber.Background}", pill.GetAttribute("style"));
        A11yAssert.Check(grid);

        Cell(grid, 0, 3).Click(); // one click opens the labels
        var options = grid.FindAll("[data-testid=status-option]");
        Assert.Equal(["Not started", "Working on it", "Done"], options.Select(o => o.TextContent.Trim()));
        Assert.Equal("true", options[1].GetAttribute("aria-checked"));
        options[2].Click();

        grid.WaitForAssertion(() => Assert.Equal("Done", items.Value("Design", Status)));
        Assert.Empty(grid.FindAll("[data-testid=status-popover]"));

        // From the keyboard: Enter opens the list, a number picks a label, Escape closes it.
        Cell(grid, 0, 3).Click();
        grid.Find("[data-testid=status-popover]").KeyDown(new KeyboardEventArgs { Key = "Escape" });
        Assert.Empty(grid.FindAll("[data-testid=status-popover]"));
        Key(grid, "Enter");
        grid.WaitForElement("[data-testid=status-popover]").KeyDown(new KeyboardEventArgs { Key = "1" });
        grid.WaitForAssertion(() => Assert.Equal("Not started", items.Value("Design", Status)));

        // Pasted labels take the label's own spelling.
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("done"));
        grid.WaitForAssertion(() => Assert.Equal("Done", items.Value("Design", Status)));
    }

    [Fact]
    public async Task Copy_gives_the_selection_as_tab_separated_text()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("Design", "Build");
        items.Set("Design", Hours, "1234.5");
        items.Set("Build", Status, "Done");
        var grid = Render(ctx, items);

        Cell(grid, 0, 0).Click();
        Cell(grid, 1, 3).Click(new MouseEventArgs { ShiftKey = true });

        Assert.Equal("Design\t1,234.5\t\t\r\nBuild\t\t\tDone\r\n", SheetGridOf(grid).CopySelection());
    }

    [Fact]
    public async Task The_sheet_opens_with_its_default_view_sorted_filtered_and_with_hidden_columns()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("Alpha", "Bravo", "Charlie");
        items.Set("Alpha", Status, "Done");
        items.Set("Bravo", Status, "Working on it");
        var definition = new SheetViewDefinition(
            [new ListSort(SheetFields.Title, Descending: true)],
            [new ListFilterCondition(Status.Id.ToString(), ListFilterOperator.IsNotEmpty)],
            [Budget.Id.ToString()]);
        var views = new FakeViewService(new SheetViewDto(Guid.NewGuid(), SheetId, "In flight", true, "pm@example.com", definition, false, false, DateTime.UtcNow));
        ctx.Services.AddSingleton<ISheetViewService>(views);
        var grid = Render(ctx, items);

        grid.WaitForAssertion(() => Assert.Equal(["Bravo", "Alpha"], grid.FindAll(".wb-title-link").Select(a => a.TextContent)));
        Assert.DoesNotContain(grid.FindAll("th .wb-th-name"), th => th.TextContent == "Budget");
        Assert.Contains("In flight", grid.Find("[data-testid=grid-views]").TextContent);
        Assert.Contains("Showing 2 of 3 rows", grid.Find("[data-testid=grid-view-summary]").TextContent);
        Assert.Contains("Status isn't blank", grid.Find("[data-testid=filter-chip]").TextContent);

        // Sorted or filtered rows aren't in their real order, so indenting waits for the view to clear.
        Cell(grid, 1, 0).Click();
        Key(grid, "Tab");
        Assert.Empty(items.Moves);

        grid.Find("[data-testid=grid-clear-view]").Click();
        Assert.Equal(["Alpha", "Bravo", "Charlie"], grid.FindAll(".wb-title-link").Select(a => a.TextContent));
    }

    [Fact]
    public async Task With_the_bulk_service_a_paste_is_one_request_and_its_undo_is_one_more()
    {
        await using var ctx = CreateContext(out var items);
        items.Seed("Design", "Build");
        var bulk = new FakeBulk(items);
        ctx.Services.AddSingleton<ISheetBulkEditService>(bulk);
        var grid = Render(ctx, items);

        Cell(grid, 0, 1).Click();
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("8\t100\r\n4\t200\r\n2\t300\r\n"));

        var paste = Assert.Single(bulk.Requests);
        Assert.Equal("Paste", paste.Source);
        Assert.Equal(2, paste.Rows.Count);
        Assert.Equal([Hours.Id.ToString(), Budget.Id.ToString()], paste.Rows[0].Cells.Select(c => c.Field));
        Assert.Equal(["2", "300"], Assert.Single(paste.NewRows!).Cells.Select(c => c.Value));
        Assert.Empty(items.Updates); // nothing row by row
        Assert.Equal(3, items.All.Count);

        Key(grid, "z", ctrl: true);
        grid.WaitForAssertion(() => Assert.Equal(2, bulk.Requests.Count));
        var undo = bulk.Requests[1];
        Assert.Single(undo.DeleteItemIds!);
        Assert.All(undo.Rows.SelectMany(r => r.Cells), c => Assert.Null(c.Value));
        Assert.Equal(2, items.All.Count);
        Assert.Null(items.Value("Design", Hours));

        Key(grid, "y", ctrl: true);
        grid.WaitForAssertion(() => Assert.Equal(3, bulk.Requests.Count));
        Assert.Equal(3, items.All.Count);
        Assert.Equal("8", items.Value("Design", Hours));
    }

    [Fact]
    public async Task A_view_compared_to_a_baseline_shows_baseline_dates_and_the_finish_variance()
    {
        await using var ctx = CreateContext(out var items);
        var mon = new DateOnly(2026, 10, 5);
        items.Seed("Design", "Build");
        var design = items.Find("Design")!;
        var build = items.Find("Build")!;
        var baseline = new BaselineSummary(Guid.NewGuid(), "Approved plan", DateTime.UtcNow, 2);
        var baselines = new FakeBaselines(new BaselineComparison(baseline,
        [
            new BaselineVarianceDto(design.Id, mon, mon.AddDays(6), mon, mon.AddDays(4), 0, 2, 2),
            new BaselineVarianceDto(build.Id, null, null, null, null, null, null, null),
        ], 2));
        ctx.Services.AddSingleton<IBaselineService>(baselines);
        var view = new SheetViewDto(Guid.NewGuid(), SheetId, "Against approved", true, "pm@example.com", new SheetViewDefinition(BaselineId: baseline.Id), false, false, DateTime.UtcNow);
        ctx.Services.AddSingleton<ISheetViewService>(new FakeViewService(view));
        var grid = ctx.Render<SheetGridView>(p => p
            .Add(v => v.Sheet, new SheetDetail(new SheetSummary(SheetId, "Plan", null, "Program Office", 2, true, null, DateTime.UtcNow), [Hours]))
            .Add(v => v.Items, items.All));

        grid.WaitForAssertion(() => Assert.Contains("Baseline finish", grid.FindAll("th .wb-th-name").Select(t => t.TextContent)));
        Assert.Equal(["Title", "Start", "Finish", "Baseline start", "Baseline finish", "Finish variance", "Hours", "WBS"],
            grid.FindAll("th .wb-th-name").Select(t => t.TextContent));
        grid.WaitForAssertion(() => Assert.Equal("+2 days", grid.Find("[data-testid=finish-variance]").TextContent));
        Assert.Contains("later than the baseline", grid.Find("[data-testid=finish-variance]").GetAttribute("title"));
        Assert.Equal("10/09/2026", Cell(grid, 0, 4).TextContent.Trim());
        Assert.Contains("wb-readonly", Cell(grid, 0, 4).ClassName);
        Assert.Contains("Baseline: Approved plan", grid.Find("[data-testid=grid-baseline]").TextContent);

        // Baseline cells are history: typing doesn't edit them, and a paste skips them.
        Cell(grid, 0, 5).Click();
        Key(grid, "Delete");
        await grid.InvokeAsync(() => SheetGridOf(grid).PasteAsync("9"));
        Assert.Empty(items.Updates);
    }

    // ---- Helpers ----

    private static BunitContext CreateContext(out StatefulItemService items)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        items = new StatefulItemService();
        ctx.Services.AddSingleton<IItemService>(items);
        ctx.Services.AddSingleton<IColumnService>(new NoColumns());
        return ctx;
    }

    private static IRenderedComponent<SheetGridView> Render(BunitContext ctx, StatefulItemService items)
    {
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, items.All));
        grid.WaitForAssertion(() => Assert.NotEmpty(grid.FindAll("tr.wb-row")));
        return grid;
    }

    private static SheetGrid SheetGridOf(IRenderedComponent<SheetGridView> grid) => grid.FindComponent<SheetGrid>().Instance;

    private static AngleSharp.Dom.IElement Cell(IRenderedComponent<SheetGridView> grid, int row, int col) =>
        grid.Find($"tr[data-row='{row}'] td[data-col='{col}']");

    private static void Key(IRenderedComponent<SheetGridView> grid, string key, bool shift = false, bool ctrl = false, bool alt = false) =>
        grid.Find(".wb-grid").KeyDown(new KeyboardEventArgs { Key = key, ShiftKey = shift, CtrlKey = ctrl, AltKey = alt });

    /// <summary>Keeps rows like the real service: creates insert at their order, moves renumber, deletes take descendants.</summary>
    private sealed class StatefulItemService : IItemService
    {
        private readonly List<ItemDto> _items = [];

        public List<CreateItemRequest> Creates { get; } = [];

        public List<(Guid ItemId, UpdateItemRequest Request)> Updates { get; } = [];

        public List<(Guid ItemId, MoveItemRequest Request)> Moves { get; } = [];

        public IReadOnlyList<ItemDto> All
        {
            get
            {
                lock (_items)
                {
                    return _items.ToList();
                }
            }
        }

        public void Seed(params string[] titles)
        {
            foreach (var title in titles)
            {
                _items.Add(new ItemDto(Guid.NewGuid(), null, _items.Count, title, null, null, null, new Dictionary<string, string?>()));
            }
        }

        public void Set(string title, ColumnDto column, string? value)
        {
            var item = Find(title)!;
            Replace(item with { Values = new Dictionary<string, string?>(item.Values) { [column.Id.ToString()] = value } });
        }

        public ItemDto? Find(string title) => All.FirstOrDefault(i => i.Title == title);

        public string? Value(string title, ColumnDto column) =>
            Find(title)?.Values.TryGetValue(column.Id.ToString(), out var value) == true ? value : null;

        public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) => Task.FromResult(All);

        /// <summary>A change made by the bulk fake: not counted in <see cref="Updates"/> (which are row-by-row saves).</summary>
        public Task<ItemDto> UpdateSilentlyAsync(Guid itemId, string title, Dictionary<string, string?> values)
        {
            lock (_items)
            {
                var updated = _items.Single(i => i.Id == itemId) with { Title = title, Values = values };
                Replace(updated);
                return Task.FromResult(updated);
            }
        }

        public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
        {
            lock (_items)
            {
                Creates.Add(request);
                var siblings = Siblings(request.ParentId);
                var order = Math.Clamp(request.Order ?? siblings.Count, 0, siblings.Count);
                var item = new ItemDto(Guid.NewGuid(), request.ParentId, order, request.Title, request.Start, request.Finish, null,
                    new Dictionary<string, string?>(request.Values ?? new Dictionary<string, string?>()), request.IsMilestone);
                siblings.Insert(order, item);
                _items.Add(item);
                Renumber(siblings);
                return Task.FromResult(_items.Single(i => i.Id == item.Id));
            }
        }

        public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
        {
            lock (_items)
            {
                Updates.Add((itemId, request));
                var item = _items.Single(i => i.Id == itemId);
                var updated = item with
                {
                    Title = request.Title, Start = request.Start, Finish = request.Finish, CostpointWbsId = request.CostpointWbsId,
                    Values = new Dictionary<string, string?>(request.Values), IsMilestone = request.IsMilestone ?? item.IsMilestone,
                };
                Replace(updated);
                return Task.FromResult(updated);
            }
        }

        public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
        {
            lock (_items)
            {
                Moves.Add((itemId, request));
                var item = _items.Single(i => i.Id == itemId);
                var old = Siblings(item.ParentId).Where(i => i.Id != itemId).ToList();
                Renumber(old);
                var target = Siblings(request.ParentId).Where(i => i.Id != itemId).ToList();
                var moved = _items.Single(i => i.Id == itemId) with { ParentId = request.ParentId };
                target.Insert(Math.Clamp(request.Order, 0, target.Count), moved);
                Replace(moved);
                Renumber(target);
                return Task.FromResult(_items.Single(i => i.Id == itemId));
            }
        }

        public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
        {
            lock (_items)
            {
                var doomed = new HashSet<Guid> { itemId };
                for (var added = true; added;)
                {
                    added = _items.Where(i => i.ParentId is { } p && doomed.Contains(p)).Select(i => doomed.Add(i.Id)).ToList().Any(a => a);
                }

                var parent = _items.Single(i => i.Id == itemId).ParentId;
                _items.RemoveAll(i => doomed.Contains(i.Id));
                Renumber(Siblings(parent));
                return Task.CompletedTask;
            }
        }

        private List<ItemDto> Siblings(Guid? parentId) => _items.Where(i => i.ParentId == parentId).OrderBy(i => i.Order).ToList();

        private void Renumber(List<ItemDto> siblings)
        {
            for (var i = 0; i < siblings.Count; i++)
            {
                Replace(_items.Single(x => x.Id == siblings[i].Id) with { Order = i });
            }
        }

        private void Replace(ItemDto item)
        {
            lock (_items)
            {
                _items[_items.FindIndex(i => i.Id == item.Id)] = item;
            }
        }
    }

    private sealed class FakeViewService(SheetViewDto defaultView) : ISheetViewService
    {
        public Task<SheetViewsDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SheetViewsDto(sheetId, [defaultView], defaultView.Id, null, false));

        public Task<SheetViewDto> CreateAsync(Guid sheetId, SaveSheetViewRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<SheetViewDto> UpdateAsync(Guid sheetId, Guid viewId, SaveSheetViewRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task SetMyDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SetSheetDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ReportFileDto> ExportAsync(Guid sheetId, SheetExportRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ReportFileDto("plan.xlsx", "application/octet-stream", [1]));
    }

    /// <summary>Applies bulk requests to the stateful items, recording each request.</summary>
    private sealed class FakeBulk(StatefulItemService items) : ISheetBulkEditService
    {
        public List<SheetBulkEditRequest> Requests { get; } = [];

        public async Task<SheetBulkEditResult> ApplyAsync(Guid sheetId, SheetBulkEditRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            var changes = new List<BulkCellChange>();
            var updated = new List<ItemDto>();
            foreach (var row in request.Rows)
            {
                var item = items.All.Single(i => i.Id == row.ItemId);
                var values = new Dictionary<string, string?>(item.Values);
                var title = item.Title;
                foreach (var cell in row.Cells)
                {
                    var before = cell.Field == SheetFields.Title ? item.Title : values.GetValueOrDefault(cell.Field);
                    changes.Add(new BulkCellChange(item.Id, cell.Field, before, cell.Value));
                    if (cell.Field == SheetFields.Title)
                    {
                        title = cell.Value!;
                    }
                    else
                    {
                        values[cell.Field] = cell.Value;
                    }
                }

                updated.Add(await items.UpdateSilentlyAsync(item.Id, title, values));
            }

            var created = new List<ItemDto>();
            foreach (var row in request.NewRows ?? [])
            {
                created.Add(await items.CreateAsync(sheetId, new CreateItemRequest("New row", Values: row.Cells.ToDictionary(c => c.Field, c => c.Value))));
            }

            foreach (var id in request.DeleteItemIds ?? [])
            {
                await items.DeleteAsync(sheetId, id);
            }

            return new SheetBulkEditResult(updated, created, changes, [], request.DeleteItemIds?.Count ?? 0);
        }
    }

    private sealed class FakeBaselines(BaselineComparison comparison) : IBaselineService
    {
        public Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BaselineSummary>>([comparison.Baseline]);

        public Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<BaselineComparison> CompareAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default) => Task.FromResult(comparison);

        public Task DeleteAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoColumns : IColumnService
    {
        public Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

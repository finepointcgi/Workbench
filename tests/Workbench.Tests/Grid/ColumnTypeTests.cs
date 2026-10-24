using System.Globalization;
using System.Text;
using ClosedXML.Excel;
using Workbench.Application.Sheets;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Formulas;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;
using Workbench.Ui.Grid;

namespace Workbench.Tests.Grid;

/// <summary>
/// The newer column types (multi-select, link, symbols, progress, auto-number, created/modified by and date): what typing or
/// pasting stores, how they show, sort, filter, save in one paste, read in formulas and export.
/// </summary>
public sealed class ColumnTypeTests
{
    private static ColumnDto Column(ColumnKind kind, params string[] options) => new(Guid.NewGuid(), kind.ToString(), kind, 0, options, null);

    private static string? Normalize(string text, ColumnDto column) =>
        CellFormat.TryNormalize(text, new GridColumn(column.Id.ToString(), column.Name, GridColumnKind.Custom, column, 150), out var value, out var error)
            ? value
            : throw new ArgumentException(error);

    [Fact]
    public void Multi_select_takes_commas_or_lines_and_the_options_spelling()
    {
        var tags = Column(ColumnKind.MultiSelect, "Design", "Build", "Test, QA");
        Assert.Equal("Design\nBuild", Normalize("design, BUILD", tags));
        Assert.Equal("Design\nTest, QA", Normalize("Design\ntest, qa", tags));
        Assert.Equal("Test, QA", Normalize("Test, QA", tags)); // a choice with a comma stays whole
        Assert.Equal("Design", Normalize("Design, design", tags)); // no duplicates
        Assert.Equal("Design, Build", CellFormat.Display("Design\nBuild", ColumnKind.MultiSelect));
        Assert.Equal("Design, Build", CellFormat.EditText("Design\nBuild", ColumnKind.MultiSelect));
        Assert.Null(Normalize("  ", tags));
    }

    [Fact]
    public void Links_keep_safe_addresses_and_their_text()
    {
        var link = Column(ColumnKind.Hyperlink);
        Assert.Equal("https://example.com/plan", Normalize("https://example.com/plan", link));
        Assert.Equal("https://www.example.com", Normalize("www.example.com", link));
        Assert.Equal("mailto:pm@example.com", Normalize("pm@example.com", link));
        Assert.Equal("[Plan](https://example.com/plan)", Normalize("[Plan](https://example.com/plan)", link));
        Assert.Equal("Plan", CellFormat.Display("[Plan](https://example.com/plan)", ColumnKind.Hyperlink));
        Assert.Equal(("https://example.com/plan", "Plan"), ColumnValues.Link("[Plan](https://example.com/plan)"));
        Assert.Throws<ArgumentException>(() => Normalize("javascript:alert(1)", link));
        Assert.Throws<ArgumentException>(() => Normalize("[x](javascript:alert(1))", link));
        Assert.Throws<ArgumentException>(() => Normalize("not a link", link));
        Assert.Null(ColumnValues.Link("data:text/html,hi"));
        Assert.Equal("[Plan](https://example.com/plan)", CellFormat.CopyText("[Plan](https://example.com/plan)", ColumnKind.Hyperlink)); // pastes back the same
    }

    [Fact]
    public void Symbols_store_their_label_and_always_show_it()
    {
        var rag = Column(ColumnKind.Symbol, SymbolSets.Rag);
        var stars = Column(ColumnKind.Symbol, SymbolSets.Stars);
        var flag = Column(ColumnKind.Symbol, SymbolSets.Flag);
        var priority = Column(ColumnKind.Symbol, SymbolSets.Priority);
        Assert.Equal("Red", Normalize("red", rag));
        Assert.Equal("Yellow", Normalize("amber", rag));
        Assert.Equal("Green", Normalize("G", rag));
        Assert.Throws<ArgumentException>(() => Normalize("High", rag)); // not in this column's set
        Assert.Equal("3", Normalize("★★★☆☆", stars));
        Assert.Equal("4", Normalize("4 stars", stars));
        Assert.Equal("Flagged", Normalize("yes", flag));
        Assert.Null(Normalize("no", flag));
        Assert.Equal("High", Normalize("high", priority));

        // Every symbol has a text label, and the label is what shows, copies and exports.
        Assert.All(SymbolSets.All.SelectMany(SymbolSets.Options), s => Assert.False(string.IsNullOrWhiteSpace(s.Label)));
        Assert.Equal("3 stars", CellFormat.Display("3", ColumnKind.Symbol));
        Assert.Equal("Red", CellFormat.Display("Red", ColumnKind.Symbol));
        Assert.Equal(["Red", "Yellow", "Green"], ColumnValues.Choices(rag));
    }

    [Fact]
    public void Progress_is_a_percentage_from_0_to_100()
    {
        var progress = Column(ColumnKind.Progress);
        Assert.Equal("40", Normalize("40", progress));
        Assert.Equal("40", Normalize("40%", progress));
        Assert.Equal("40", Normalize("0.4", progress)); // what Excel copies for 40%
        Assert.Equal("1", Normalize("1", progress));
        Assert.Equal("12.5", Normalize("12.5 %", progress));
        Assert.Throws<ArgumentException>(() => Normalize("140", progress));
        Assert.Throws<ArgumentException>(() => Normalize("lots", progress));
        Assert.Equal("40%", CellFormat.Display("40", ColumnKind.Progress));
    }

    [Fact]
    public void Automatic_columns_cant_be_typed_into()
    {
        foreach (var kind in new[] { ColumnKind.AutoNumber, ColumnKind.CreatedBy, ColumnKind.CreatedDate, ColumnKind.ModifiedBy, ColumnKind.ModifiedDate })
        {
            Assert.False(CellFormat.TryNormalize("x", kind, out _, out var error));
            Assert.NotNull(error);
            Assert.True(new GridColumn("k", "K", GridColumnKind.Custom, Column(kind), 100).ReadOnly);
        }

        Assert.Equal("Created date", CellFormat.KindName(ColumnKind.CreatedDate));
        Assert.Equal("REQ-007", ColumnValues.AutoNumber(["REQ-", "3", "1"], 7));
        Assert.Equal("12", ColumnValues.AutoNumber([], 12));
        var stamp = ColumnValues.Stamp(new DateTime(2026, 10, 22, 14, 5, 0, DateTimeKind.Utc));
        Assert.Equal("2026-10-22T14:05:00Z", stamp);
        Assert.Equal(new DateTime(2026, 10, 22, 14, 5, 0, DateTimeKind.Utc), ColumnValues.ParseStamp(stamp));
    }

    [Fact]
    public void Sorting_and_filtering_read_each_type_by_meaning()
    {
        var tags = Column(ColumnKind.MultiSelect, "Design", "Build", "Test");
        var rag = Column(ColumnKind.Symbol, SymbolSets.Rag);
        var stars = Column(ColumnKind.Symbol, SymbolSets.Stars);
        var progress = Column(ColumnKind.Progress);
        var number = Column(ColumnKind.AutoNumber, "REQ-");
        var created = Column(ColumnKind.CreatedDate);
        IReadOnlyList<ColumnDto> columns = [tags, rag, stars, progress, number, created];
        ItemDto Row(string title, int order, string? tag, string? color, string? star, string? done, string? id, DateTime createdAt) =>
            new(Guid.NewGuid(), null, order, title, null, null, null, new Dictionary<string, string?>
            {
                [tags.Id.ToString()] = tag, [rag.Id.ToString()] = color, [stars.Id.ToString()] = star, [progress.Id.ToString()] = done,
                [number.Id.ToString()] = id, [created.Id.ToString()] = ColumnValues.Stamp(createdAt),
            });
        var a = Row("A", 0, "Test", "Green", "2", "100", "REQ-10", new DateTime(2026, 10, 20, 9, 0, 0, DateTimeKind.Utc));
        var b = Row("B", 1, "Design\nTest", "Red", "5", "9", "REQ-9", new DateTime(2026, 10, 1, 9, 0, 0, DateTimeKind.Utc));
        var c = Row("C", 2, "Build", "Yellow", null, "40", "REQ-11", new DateTime(2026, 10, 15, 9, 0, 0, DateTimeKind.Utc));
        IReadOnlyList<ItemDto> items = [a, b, c];
        string[] Sorted(ColumnDto column, bool descending = false) =>
            SheetViewEngine.Apply(items, columns, new SheetViewDefinition([new ListSort(column.Id.ToString(), descending)])).Select(r => r.Item.Title).ToArray();
        string[] Filtered(ColumnDto column, ListFilterOperator op, string? value = null) =>
            SheetViewEngine.Apply(items, columns, new SheetViewDefinition(Filters: [new ListFilterCondition(column.Id.ToString(), op, value)])).Select(r => r.Item.Title).ToArray();

        Assert.Equal(["B", "C", "A"], Sorted(rag)); // Red, Yellow, Green: the set's order
        Assert.Equal(["B", "A", "C"], Sorted(stars, descending: true)); // 5, 2, blank last
        Assert.Equal(["B", "C", "A"], Sorted(progress)); // 9 < 40 < 100 (not as text)
        Assert.Equal(["B", "A", "C"], Sorted(number)); // REQ-9 before REQ-10
        Assert.Equal(["B", "C", "A"], Sorted(created));
        Assert.Equal(["B", "C", "A"], Sorted(tags)); // by the first choice in the column's order

        Assert.Equal(["A", "B"], Filtered(tags, ListFilterOperator.Equals, "Test")); // has any of
        Assert.Equal(["C"], Filtered(tags, ListFilterOperator.NotEquals, "Test\nDesign"));
        Assert.Equal(["B", "C"], Filtered(rag, ListFilterOperator.Equals, "Red\nYellow"));
        Assert.Equal(["B", "C"], Filtered(progress, ListFilterOperator.LessThan, "100"));
        Assert.Equal(["A", "C"], Filtered(created, ListFilterOperator.GreaterOrEqual, "2026-10-15")); // a system date against a day
        Assert.Equal(["C"], Filtered(stars, ListFilterOperator.IsEmpty));
        Assert.Contains(ListFilterOperator.LessThan, SheetFilterText.OperatorsFor(ColumnKind.Progress));
    }

    [Fact]
    public void A_board_can_be_grouped_by_a_symbol_column_with_lanes_in_the_symbols_colors()
    {
        var health = Column(ColumnKind.Symbol, SymbolSets.Rag);
        var row = new ItemDto(Guid.NewGuid(), null, 0, "Row", null, null, null, new Dictionary<string, string?> { [health.Id.ToString()] = "Yellow" });
        var lanes = Workbench.Ui.Board.BoardLanes.Group(health, [row]);
        Assert.Equal(["Unassigned", "Red", "Yellow", "Green"], lanes.Select(l => l.Label));
        Assert.Equal("Row", Assert.Single(lanes[2].Items).Title);
        Assert.Equal(MudBlazor.Color.Error, Workbench.Ui.Board.BoardLanes.ColorFor(health, "Red"));
        Assert.Equal(MudBlazor.Color.Success, Workbench.Ui.Board.BoardLanes.ColorFor(health, "Green"));
        Assert.Equal("3 stars", Workbench.Ui.Board.BoardLanes.Group(Column(ColumnKind.Symbol, SymbolSets.Stars), []).Single(l => l.Value == "3").Label);
    }

    [Fact]
    public void Formulas_read_the_new_types_sensibly()
    {
        Assert.Equal(FormulaValue.FromNumber(0.4), FormulaValue.FromCell("40", ColumnType.Progress));
        Assert.Equal(FormulaValue.FromText("Design, Build"), FormulaValue.FromCell("Design\nBuild", ColumnType.MultiSelect));
        Assert.Equal(FormulaValue.FromText("https://example.com"), FormulaValue.FromCell("[Site](https://example.com)", ColumnType.Hyperlink));
        Assert.Equal(FormulaValue.FromNumber(3), FormulaValue.FromCell("3", ColumnType.Symbol));
        Assert.Equal(FormulaValue.FromText("Red"), FormulaValue.FromCell("Red", ColumnType.Symbol));
        var created = FormulaValue.FromCell("2026-10-22T12:00:00Z", ColumnType.CreatedDate);
        Assert.Equal(FormulaValueKind.Date, created.Kind);
    }

    [Fact]
    public async Task Rows_get_auto_numbers_and_stamps_whatever_saves_them()
    {
        await using var host = await SeededHost.CreateAsync();
        var sheetId = host.SheetId;
        IItemService Items(string email) => host.As(email).Host.Get<IItemService>();
        var columns = host.As(SeededHost.Owner).Host.Get<IColumnService>();
        var first = (await Items(SeededHost.Owner).ListAsync(sheetId)).Single(); // "Kickoff", added by the seed

        // A new auto-number numbers the rows already there, in order; system columns fill in from the stamps.
        var number = await columns.CreateAsync(sheetId, new CreateColumnRequest("ID", ColumnKind.AutoNumber, ["REQ-", "3", "7"]));
        var createdBy = await columns.CreateAsync(sheetId, new CreateColumnRequest("Created by", ColumnKind.CreatedBy));
        var created = await columns.CreateAsync(sheetId, new CreateColumnRequest("Created", ColumnKind.CreatedDate));
        var modifiedBy = await columns.CreateAsync(sheetId, new CreateColumnRequest("Modified by", ColumnKind.ModifiedBy));
        var modified = await columns.CreateAsync(sheetId, new CreateColumnRequest("Modified", ColumnKind.ModifiedDate));
        await Assert.ThrowsAsync<ArgumentException>(() => columns.CreateAsync(sheetId, new CreateColumnRequest("Bad", ColumnKind.AutoNumber, Formula: "=1")));
        string? Value(ItemDto item, ColumnDto column) => item.Values.GetValueOrDefault(column.Id.ToString());
        var rows = await Items(SeededHost.Owner).ListAsync(sheetId);
        Assert.Equal("REQ-007", Value(rows.Single(), number));
        Assert.Null(Value(rows.Single(), createdBy)); // the seed said nobody
        Assert.NotNull(ColumnValues.ParseStamp(Value(rows.Single(), created)));
        var modifiedBefore = Value(rows.Single(), modified);

        // New rows take the next numbers and their author; values sent for automatic columns are ignored.
        var second = await Items(SeededHost.Owner).CreateAsync(sheetId, new CreateItemRequest("Second", Values: new Dictionary<string, string?> { [number.Id.ToString()] = "HACK-1" }));
        Assert.Equal("REQ-008", Value(second, number));
        Assert.Equal(SeededHost.Owner, Value(second, createdBy));
        Assert.Equal(SeededHost.Owner, Value(second, modifiedBy));

        // A change by someone else stamps Modified by/date and leaves Created and the number alone.
        await Task.Delay(1100);
        var changed = await Items(SeededHost.Editor).UpdateAsync(sheetId, first.Id, new UpdateItemRequest("Kickoff (renamed)", null, null, null,
            new Dictionary<string, string?> { [number.Id.ToString()] = "REQ-999", [createdBy.Id.ToString()] = "mallory@example.com" }));
        Assert.Equal("REQ-007", Value(changed, number));
        Assert.Null(Value(changed, createdBy));
        Assert.Equal(SeededHost.Editor, Value(changed, modifiedBy));
        Assert.True(string.CompareOrdinal(Value(changed, modified), modifiedBefore) > 0);

        // Paste: automatic cells are skipped with a reason; new pasted rows are numbered.
        var bulk = host.As(SeededHost.Editor).Host.Get<ISheetBulkEditService>();
        var result = await bulk.ApplyAsync(sheetId, new SheetBulkEditRequest(
            [new BulkRowEdit(second.Id, [new(number.Id.ToString(), "REQ-1")])],
            [new BulkNewRow([new(SheetFields.Title, "Pasted")])]));
        Assert.Contains(result.Skipped, s => s.Field == number.Id.ToString() && s.Reason.Contains("automatically"));
        Assert.Equal("REQ-009", Value(result.Created.Single(), number));
        Assert.Equal(SeededHost.Editor, Value(result.Created.Single(), createdBy));

        // Deleting a row doesn't hand its number out again.
        await Items(SeededHost.Editor).DeleteAsync(sheetId, result.Created.Single().Id);
        Assert.Equal("REQ-010", Value(await Items(SeededHost.Editor).CreateAsync(sheetId, new CreateItemRequest("After delete")), number));

        // Adding a column doesn't count as changing the rows.
        var before = (await Items(SeededHost.Owner).ListAsync(sheetId)).ToDictionary(i => i.Id, i => Value(i, modified));
        await columns.CreateAsync(sheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        Assert.All(await Items(SeededHost.Owner).ListAsync(sheetId), i => Assert.Equal(before[i.Id], Value(i, modified)));
    }

    [Fact]
    public async Task Paste_saves_the_new_types_and_skips_what_doesnt_fit()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var tags = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Tags", ColumnKind.MultiSelect, ["Design", "Build"], OptionColors: ["blue", "teal"]));
        var link = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Link", ColumnKind.Hyperlink));
        var health = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Health", ColumnKind.Symbol, ["rag"]));
        var done = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Done", ColumnKind.Progress));
        Assert.Equal(["blue", "teal"], tags.OptionColors);
        await Assert.ThrowsAsync<ArgumentException>(() => host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Bad", ColumnKind.Symbol, ["moons"])));
        var row = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Row"));

        var result = await host.Get<ISheetBulkEditService>().ApplyAsync(sheetId, new SheetBulkEditRequest(
            [new BulkRowEdit(row.Id, [new(tags.Id.ToString(), "build, design"), new(link.Id.ToString(), "example.com"), new(health.Id.ToString(), "amber"),
                new(done.Id.ToString(), "75%")])],
            [new BulkNewRow([new(SheetFields.Title, "Bad"), new(link.Id.ToString(), "javascript:alert(1)"), new(health.Id.ToString(), "High"), new(done.Id.ToString(), "200")])]));

        var saved = (await host.Items.ListAsync(sheetId)).Single(i => i.Id == row.Id);
        Assert.Equal("Build\nDesign", saved.Values[tags.Id.ToString()]);
        Assert.Equal("https://example.com", saved.Values[link.Id.ToString()]);
        Assert.Equal("Yellow", saved.Values[health.Id.ToString()]);
        Assert.Equal("75", saved.Values[done.Id.ToString()]);
        Assert.Equal(3, result.Skipped.Count);
        Assert.False(result.Created.Single().Values.ContainsKey(link.Id.ToString()));
    }

    [Fact]
    public async Task Export_writes_labels_links_percentages_and_days()
    {
        await using var host = await SeededHost.CreateAsync();
        var columns = host.As(SeededHost.Admin).Host.Get<IColumnService>();
        var items = host.As(SeededHost.Admin).Host.Get<IItemService>();
        var tags = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Tags", ColumnKind.MultiSelect, ["Design", "Build"]));
        var link = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Link", ColumnKind.Hyperlink));
        var stars = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Rating", ColumnKind.Symbol, ["stars"]));
        var done = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Done", ColumnKind.Progress));
        var number = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("ID", ColumnKind.AutoNumber, ["T-", "2", "1"]));
        var created = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Created", ColumnKind.CreatedDate));
        await items.UpdateAsync(host.SheetId, host.ItemId, new UpdateItemRequest("Kickoff", null, null, null, new Dictionary<string, string?>
        {
            [tags.Id.ToString()] = "Design\nBuild", [link.Id.ToString()] = "[Charter](https://example.com/charter)", [stars.Id.ToString()] = "4", [done.Id.ToString()] = "40",
        }));

        var exports = host.As(SeededHost.Viewer).Host.Get<ISheetViewService>();
        var xlsx = await exports.ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Xlsx));
        using var workbook = new XLWorkbook(new MemoryStream(xlsx.Content));
        var sheet = workbook.Worksheets.First();
        var header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Title");
        var names = header.CellsUsed().Select(c => c.GetString()).ToList();
        IXLCell Cell(string name) => sheet.Cell(header.RowNumber() + 1, names.IndexOf(name) + 1);
        Assert.Equal("Design, Build", Cell("Tags").GetString());
        Assert.Equal("Charter", Cell("Link").GetString());
        Assert.Equal("https://example.com/charter", Cell("Link").GetHyperlink().ExternalAddress.ToString());
        Assert.Equal("4 stars", Cell("Rating").GetString());
        Assert.Equal(0.4, Cell("Done").GetDouble(), 3);
        Assert.Equal("0.0%", Cell("Done").Style.NumberFormat.Format);
        Assert.Equal("T-01", Cell("ID").GetString());
        Assert.Equal(XLDataType.DateTime, Cell("Created").DataType);

        var pdf = Encoding.ASCII.GetString((await exports.ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Pdf))).Content);
        Assert.Contains("(4 stars)", pdf);
        Assert.Contains("(40%)", pdf);
        Assert.Contains("(Charter)", pdf);
    }
}

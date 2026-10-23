using System.Text;
using ClosedXML.Excel;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Grid;

/// <summary>Saved sheet views (personal, shared, defaults), who may change them, and sheet exports.</summary>
public sealed class SheetViewServiceTests
{
    private static SaveSheetViewRequest View(string name, bool shared = false, SheetViewDefinition? definition = null) =>
        new(name, shared, definition ?? new SheetViewDefinition([new ListSort(SheetFields.Title, Descending: true)]));

    [Fact]
    public async Task Personal_views_are_their_owners_and_shared_views_are_everyones_on_the_sheet()
    {
        await using var host = await SeededHost.CreateAsync();
        ISheetViewService editor() => host.As(SeededHost.Editor).Host.Get<ISheetViewService>();
        ISheetViewService viewer() => host.As(SeededHost.Viewer).Host.Get<ISheetViewService>();
        await editor().CreateAsync(host.SheetId, View("Editor's own"));
        var shared = await editor().CreateAsync(host.SheetId, View("Late tasks", shared: true));
        var mine = await viewer().CreateAsync(host.SheetId, View("Val's own"));

        var seen = await viewer().ListAsync(host.SheetId);
        Assert.Equal(["Late tasks", "Val's own"], seen.Views.Select(v => v.Name)); // shared first
        Assert.False(seen.CanShare);
        Assert.False(seen.Views[0].CanEdit);
        Assert.True(seen.Views[1].CanEdit);
        Assert.Equal(SheetFields.Title, Assert.Single(seen.Views[0].Definition.Sort!).Field);
        Assert.True((await editor().ListAsync(host.SheetId)).CanShare);

        // Viewers can't share or change a shared view, and someone else's personal view isn't there for them.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer().CreateAsync(host.SheetId, View("Shared by Val", shared: true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer().UpdateAsync(host.SheetId, mine.Id, View("Now shared", shared: true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer().DeleteAsync(host.SheetId, shared.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => editor().DeleteAsync(host.SheetId, mine.Id));

        // Someone who can't see the sheet gets nothing.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(SeededHost.Outsider).Host.Get<ISheetViewService>().ListAsync(host.SheetId));

        await viewer().DeleteAsync(host.SheetId, mine.Id);
        Assert.Equal(["Late tasks"], (await viewer().ListAsync(host.SheetId)).Views.Select(v => v.Name));
    }

    [Fact]
    public async Task The_sheet_opens_with_my_default_else_the_sheets_default()
    {
        await using var host = await SeededHost.CreateAsync();
        ISheetViewService editor() => host.As(SeededHost.Editor).Host.Get<ISheetViewService>();
        ISheetViewService viewer() => host.As(SeededHost.Viewer).Host.Get<ISheetViewService>();
        var team = await editor().CreateAsync(host.SheetId, View("Team view", shared: true));
        var personal = await editor().CreateAsync(host.SheetId, View("Editor's own"));

        Assert.Null((await viewer().ListAsync(host.SheetId)).DefaultView); // the standard view

        await Assert.ThrowsAsync<ArgumentException>(() => editor().SetSheetDefaultAsync(host.SheetId, new SetDefaultSheetViewRequest(personal.Id)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => viewer().SetSheetDefaultAsync(host.SheetId, new SetDefaultSheetViewRequest(team.Id)));
        await editor().SetSheetDefaultAsync(host.SheetId, new SetDefaultSheetViewRequest(team.Id));
        Assert.Equal("Team view", (await viewer().ListAsync(host.SheetId)).DefaultView?.Name);

        var mine = await viewer().CreateAsync(host.SheetId, View("Val's own"));
        await viewer().SetMyDefaultAsync(host.SheetId, new SetDefaultSheetViewRequest(mine.Id));
        Assert.Equal("Val's own", (await viewer().ListAsync(host.SheetId)).DefaultView?.Name);
        Assert.Equal("Team view", (await editor().ListAsync(host.SheetId)).DefaultView?.Name); // mine is mine only

        await viewer().SetMyDefaultAsync(host.SheetId, new SetDefaultSheetViewRequest(null));
        Assert.Equal("Team view", (await viewer().ListAsync(host.SheetId)).DefaultView?.Name);

        // Deleting the default view goes back to the standard view.
        await editor().DeleteAsync(host.SheetId, team.Id);
        Assert.Null((await viewer().ListAsync(host.SheetId)).DefaultView);
    }

    [Fact]
    public async Task Views_are_validated()
    {
        await using var host = await SeededHost.CreateAsync();
        ISheetViewService editor() => host.As(SeededHost.Editor).Host.Get<ISheetViewService>();
        await Assert.ThrowsAsync<ArgumentException>(() => editor().CreateAsync(host.SheetId, View(" ")));
        await Assert.ThrowsAsync<ArgumentException>(() => editor().CreateAsync(host.SheetId,
            View("Bad", definition: new SheetViewDefinition(Filters: [new ListFilterCondition("title", (ListFilterOperator)99, "x")]))));

        // Title can't be hidden, and blank sorts are dropped.
        var saved = await editor().CreateAsync(host.SheetId,
            View("Tidy", definition: new SheetViewDefinition([new ListSort(" ")], null, [SheetFields.Title, SheetFields.Wbs, SheetFields.Wbs])));
        Assert.Empty(saved.Definition.Sort!);
        Assert.Equal([SheetFields.Wbs], saved.Definition.HiddenColumns);
    }

    [Fact]
    public async Task Export_writes_the_view_as_excel_and_pdf_without_columns_the_caller_cant_see()
    {
        await using var host = await SeededHost.CreateAsync();
        var columns = host.As(SeededHost.Admin).Host.Get<IColumnService>();
        var items = host.As(SeededHost.Admin).Host.Get<IItemService>();
        var status = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Not started", "Working on it", "Done"]));
        var hours = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Hours", ColumnKind.Number));
        var rate = await columns.CreateAsync(host.SheetId, new CreateColumnRequest("Rate", ColumnKind.Currency, IsRestricted: true));
        Dictionary<string, string?> Values(string s, string h) => new() { [status.Id.ToString()] = s, [hours.Id.ToString()] = h, [rate.Id.ToString()] = "150" };
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Draft plan", Values: Values("Done", "12.5")));
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Review (with PM)", Values: Values("Working on it", "4")));
        await items.CreateAsync(host.SheetId, new CreateItemRequest("Ship", Values: Values("Not started", "")));

        var view = new SheetViewDefinition(
            [new ListSort(hours.Id.ToString(), Descending: true)],
            [new ListFilterCondition(status.Id.ToString(), ListFilterOperator.NotEquals, "Not started")],
            [SheetFields.Wbs]);
        var exports = host.As(SeededHost.Viewer).Host.Get<ISheetViewService>();
        var xlsx = await exports.ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Xlsx, view, "In flight"));

        Assert.EndsWith(".xlsx", xlsx.FileName);
        using var workbook = new XLWorkbook(new MemoryStream(xlsx.Content));
        var sheet = workbook.Worksheets.First();
        var header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Title");
        var headers = header.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Status", headers);
        Assert.Contains("Hours", headers);
        Assert.DoesNotContain("Rate", headers); // restricted: the viewer never sees it
        Assert.DoesNotContain("WBS", headers); // hidden by the view
        var rows = sheet.RowsUsed().Where(r => r.RowNumber() > header.RowNumber()).Select(r => r.Cell(1).GetString()).Where(t => t.Length > 0).ToList();
        Assert.Equal(["Draft plan", "Review (with PM)"], rows.Take(2)); // filtered, sorted by hours descending
        var hoursCell = sheet.Cell(header.RowNumber() + 1, headers.IndexOf("Hours") + 1);
        Assert.Equal(12.5, hoursCell.GetDouble()); // a number, not text

        var pdf = await exports.ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Pdf, view, "In flight"));
        Assert.Equal("application/pdf", pdf.ContentType);
        var text = Encoding.ASCII.GetString(pdf.Content);
        Assert.StartsWith("%PDF-1.4", text);
        Assert.Contains("(Draft plan)", text);
        Assert.Contains(@"(Review \(with PM\))", text);
        Assert.DoesNotContain("(Ship)", text);
        Assert.DoesNotContain("(Rate)", text);
        Assert.Contains("Page 1 of 1", text);

        // The whole sheet (no view) has every row.
        var all = await exports.ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Pdf));
        Assert.Contains("(Ship)", Encoding.ASCII.GetString(all.Content));

        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            host.As(SeededHost.Outsider).Host.Get<ISheetViewService>().ExportAsync(host.SheetId, new SheetExportRequest(SheetExportFormat.Xlsx)));
    }

    [Fact]
    public async Task Status_label_colors_are_kept_per_option_and_follow_renames()
    {
        await using var host = await SeededHost.CreateAsync();
        IColumnService columns() => host.As(SeededHost.Editor).Host.Get<IColumnService>();
        var created = await columns().CreateAsync(host.SheetId,
            new CreateColumnRequest("Status", ColumnKind.Dropdown, ["Queued", "Done", "Mystery"], OptionColors: ["purple", "not-a-color"]));

        // Valid colors stay; unknown or missing ones get the suggested color for the label.
        Assert.Equal(["purple", "green", "gray"], created.OptionColors);
        Assert.Equal(StatusColors.Purple, created.ColorOf("queued"));

        // Renaming without sending colors keeps each surviving label's color; a new label gets its suggestion.
        var updated = await columns().UpdateAsync(host.SheetId, created.Id, new UpdateColumnRequest("Status", ["Done", "Queued", "Blocked"], null, created.Order));
        Assert.Equal(["green", "purple", "red"], updated.OptionColors);

        var recolored = await columns().UpdateAsync(host.SheetId, created.Id,
            new UpdateColumnRequest("Status", ["Done", "Queued", "Blocked"], null, created.Order, OptionColors: ["teal", "purple", "red"]));
        Assert.Equal(["teal", "purple", "red"], recolored.OptionColors);

        var reread = (await host.As(SeededHost.Viewer).Host.Get<ISheetService>().GetAsync(host.SheetId))!.Columns.Single(c => c.Id == created.Id);
        Assert.Equal(["teal", "purple", "red"], reread.OptionColors);

        // Other column types have no label colors.
        Assert.Null((await columns().CreateAsync(host.SheetId, new CreateColumnRequest("Notes", ColumnKind.Text))).OptionColors);
    }
}

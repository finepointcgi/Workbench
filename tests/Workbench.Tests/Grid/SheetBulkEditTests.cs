using ClosedXML.Excel;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Automation;
using Workbench.Tests.Identity;
using Workbench.Tests.Scheduling;

namespace Workbench.Tests.Grid;

/// <summary>Paste, fill and clear as one save: per-cell checks, one reflow, one activity entry, automations as one batch.</summary>
public sealed class SheetBulkEditTests
{
    [Fact]
    public async Task One_request_saves_every_valid_cell_skips_the_rest_and_adds_rows()
    {
        await using var host = new AutomationHost();
        var bulk = host.Get<ISheetBulkEditService>();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId); // Open, Blocked, Done
        var hours = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Hours", ColumnKind.Number));
        var twice = await host.Columns.CreateAsync(sheetId, new CreateColumnRequest("Twice", ColumnKind.Number, Formula: "=[Hours] * 2"));
        var a = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Design"));
        var b = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Build", Start: new(2026, 10, 19), Finish: new(2026, 10, 23)));
        var phase = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Phase", Start: new(2026, 10, 19), Finish: new(2026, 10, 20)));
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("Step", phase.Id, Start: new(2026, 10, 19), Finish: new(2026, 10, 20)));
        var activityBefore = (await host.Activity.ListAsync(sheetId)).Count;

        var result = await bulk.ApplyAsync(sheetId, new SheetBulkEditRequest(
            [
                new BulkRowEdit(a.Id, [new(SheetFields.Title, " Design v2 "), new(status.Id.ToString(), "done"), new(hours.Id.ToString(), "$1,200.50")]),
                new BulkRowEdit(b.Id, [new(hours.Id.ToString(), "lots"), new(SheetFields.Finish, "2026-10-01"), new(twice.Id.ToString(), "9"),
                    new("no-such-column", "x"), new(status.Id.ToString(), "Blocked")]),
                new BulkRowEdit(phase.Id, [new(SheetFields.Start, "10/05/2026")]),
            ],
            [new BulkNewRow([new(SheetFields.Title, "Test"), new(hours.Id.ToString(), "(4)")]), new BulkNewRow([new(status.Id.ToString(), "Open")])],
            Source: "Paste"));

        var rows = (await host.Items.ListAsync(sheetId)).ToDictionary(i => i.Id);
        Assert.Equal("Design v2", rows[a.Id].Title);
        Assert.Equal(("Done", "1200.5", "2401"), (rows[a.Id].Values[status.Id.ToString()], rows[a.Id].Values[hours.Id.ToString()], rows[a.Id].Values[twice.Id.ToString()]));
        Assert.Equal("Blocked", rows[b.Id].Values[status.Id.ToString()]);
        Assert.Equal(new DateOnly(2026, 10, 23), rows[b.Id].Finish); // finish before start: skipped
        Assert.Equal(new DateOnly(2026, 10, 19), rows[phase.Id].Start); // a summary's dates come from its subtasks

        Assert.Equal(["Test", "New row"], result.Created.Select(i => i.Title));
        Assert.Equal("-4", result.Created[0].Values[hours.Id.ToString()]);
        Assert.Equal([0, 1, 2, 3, 4], rows.Values.Where(i => i.ParentId is null).OrderBy(i => i.Order).Select(i => i.Order));
        Assert.Equal(result.Created.Select(i => i.Id), rows.Values.Where(i => i.ParentId is null).OrderBy(i => i.Order).TakeLast(2).Select(i => i.Id));

        Assert.Equal(4, result.Changes.Count); // title, status, hours on Design; status on Build
        Assert.Contains(result.Changes, c => c.ItemId == a.Id && c.Field == SheetFields.Title && c.Before == "Design" && c.After == "Design v2");
        Assert.Equal("2401", result.Updated.Single(i => i.Id == a.Id).Values[twice.Id.ToString()]); // formulas worked out
        var reasons = result.Skipped.ToDictionary(s => (s.ItemId, s.Field), s => s.Reason);
        Assert.Equal(5, reasons.Count);
        Assert.Contains("isn't a number", reasons[(b.Id, hours.Id.ToString())]);
        Assert.Contains("Finish can't be before start", reasons[(b.Id, SheetFields.Finish)]);
        Assert.Contains("worked out from other cells", reasons[(b.Id, twice.Id.ToString())]);
        Assert.Contains("no such column", reasons[(b.Id, "no-such-column")]);
        Assert.Contains("subtasks", reasons[(phase.Id, SheetFields.Start)]);

        // One activity entry (and so one live refresh) for the whole paste.
        var activity = (await host.Activity.ListAsync(sheetId)).Skip(0).ToList();
        Assert.Equal(activityBefore + 1, activity.Count);
        Assert.Contains(activity, e => e.Kind == SheetBulkEditService.BulkEditedKind && e.Summary == "Paste: changed 4 cells in 2 rows, added 2 rows");

        // Undo is one request too: the old values back, the added rows gone.
        var undo = await bulk.ApplyAsync(sheetId, new SheetBulkEditRequest(
            result.Changes.GroupBy(c => c.ItemId).Select(g => new BulkRowEdit(g.Key, g.Select(c => new BulkCellEdit(c.Field, c.Before)).ToList())).ToList(),
            DeleteItemIds: result.Created.Select(i => i.Id).ToList(), Source: "Undo paste"));
        rows = (await host.Items.ListAsync(sheetId)).ToDictionary(i => i.Id);
        Assert.Equal(2, undo.Deleted);
        Assert.Equal(4, rows.Count);
        Assert.Equal("Design", rows[a.Id].Title);
        Assert.False(rows[a.Id].Values.ContainsKey(hours.Id.ToString()));
    }

    [Fact]
    public async Task Automations_run_once_for_the_batch_with_one_notification_per_automation_and_person()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var status = await host.StatusColumnAsync(sheetId);
        var items = new List<ItemDto>();
        foreach (var title in new[] { "Design", "Build", "Test" })
        {
            items.Add(await host.Items.CreateAsync(sheetId, new CreateItemRequest(title)));
        }

        await host.Get<IAutomationService>().CreateAsync(new SaveAutomationRequest("Blocked work",
            new AutomationDefinition(new AutomationTrigger(AutomationTriggerKind.FieldChanged, FieldKey: status.Id.ToString()) { SheetId = sheetId }, null,
                [new AutomationAction(AutomationActionKind.Notify, [new AutomationRecipient(RecipientKind.Person, "pm@example.com")], "{Title} changed")])));
        var before = (await host.AllNotificationsAsync()).Count;

        // A fill down of "Blocked" over three rows.
        await host.Get<ISheetBulkEditService>().ApplyAsync(sheetId, new SheetBulkEditRequest(
            items.Select(i => new BulkRowEdit(i.Id, [new BulkCellEdit(status.Id.ToString(), "Blocked")])).ToList(), Source: "Fill down"));

        var sent = (await host.AllNotificationsAsync()).Skip(before).ToList();
        var summary = Assert.Single(sent);
        Assert.Equal("Blocked work: 3 rows from a fill down in the grid", summary.Title);
        Assert.Contains("\"Design\"", summary.Body);
    }

    [Fact]
    public async Task Bulk_edits_need_edit_access_and_never_touch_columns_the_editor_cant_see()
    {
        await using var host = await SeededHost.CreateAsync();
        var rate = await host.As(SeededHost.Admin).Host.Get<IColumnService>().CreateAsync(host.SheetId, new CreateColumnRequest("Rate", ColumnKind.Currency, IsRestricted: true));
        var note = await host.As(SeededHost.Admin).Host.Get<IColumnService>().CreateAsync(host.SheetId, new CreateColumnRequest("Note", ColumnKind.Text));
        await host.As(SeededHost.Admin).Host.Get<IItemService>().UpdateAsync(host.SheetId, host.ItemId,
            new UpdateItemRequest("Kickoff", null, null, null, new Dictionary<string, string?> { [rate.Id.ToString()] = "150" }));
        var request = new SheetBulkEditRequest([new BulkRowEdit(host.ItemId, [new(rate.Id.ToString(), "1"), new(note.Id.ToString(), "Agenda sent")])]);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(SeededHost.Viewer).Host.Get<ISheetBulkEditService>().ApplyAsync(host.SheetId, request));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(SeededHost.Outsider).Host.Get<ISheetBulkEditService>().ApplyAsync(host.SheetId, request));

        var result = await host.As(SeededHost.Editor).Host.Get<ISheetBulkEditService>().ApplyAsync(host.SheetId, request);
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal((rate.Id.ToString(), "There's no such column on this sheet."), (skipped.Field, skipped.Reason));
        var updated = Assert.Single(result.Updated);
        Assert.Equal("Agenda sent", updated.Values[note.Id.ToString()]);
        Assert.False(updated.Values.ContainsKey(rate.Id.ToString()));

        var stored = (await host.As(SeededHost.Admin).Host.Get<IItemService>().ListAsync(host.SheetId)).Single(i => i.Id == host.ItemId);
        Assert.Equal("150", stored.Values[rate.Id.ToString()]);
    }

    [Fact]
    public async Task A_view_compared_to_a_baseline_exports_the_baseline_columns()
    {
        var kit = new PlanKit();
        var sheetId = await kit.SheetAsync();
        var design = await kit.TaskAsync(sheetId, "Design", PlanKit.Mon, 5);
        var approved = await kit.Baselines.CaptureAsync(sheetId, new CaptureBaselineRequest("Approved"));
        await kit.Plan.SetDurationAsync(sheetId, design.Id, 7);
        var views = new SheetViewService(kit.Factory, new Collaboration.TestUser(), new SheetService(kit.Factory, TimeProvider.System), kit.Items,
            TimeProvider.System, baselines: kit.Baselines);

        var file = await views.ExportAsync(sheetId, new SheetExportRequest(SheetExportFormat.Xlsx, new SheetViewDefinition(BaselineId: approved.Id)));

        using var book = new XLWorkbook(new MemoryStream(file.Content));
        var sheet = book.Worksheets.First();
        var header = sheet.RowsUsed().First(r => r.Cell(1).GetString() == "Title");
        var headers = header.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Equal(["Title", "Start", "Finish", "Baseline start", "Baseline finish", "Finish variance (working days)", "WBS"], headers.Take(7));
        Assert.Equal(2, sheet.Cell(header.RowNumber() + 1, 6).GetDouble());
        Assert.Contains(sheet.CellsUsed(), c => c.GetString() == "Compared to the baseline \"Approved\"");
    }
}

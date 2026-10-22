using ClosedXML.Excel;
using Workbench.Application;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Identity;
using Workbench.Domain.Tasks;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>Excel export and import of the plan: an unchanged export changes nothing, edits preview before they apply.</summary>
public sealed class PlanImportTests
{
    private static readonly DateOnly Mon = new(2026, 10, 5);

    private readonly IWorkbenchDbContextFactory _factory = TestDb.CreateFactory();
    private readonly ItemService _items;
    private readonly PlanScheduleService _plan;

    public PlanImportTests()
    {
        _items = new ItemService(_factory, TimeProvider.System);
        _plan = new PlanScheduleService(_factory, _items, TimeProvider.System);
    }

    private sealed record Plan(Guid SheetId, ItemDto Phase, ItemDto Design, ItemDto Build, ItemDto Review, ItemDto Ship, ColumnDto Percent, ColumnDto Notes);

    /// <summary>
    /// 1 Phase 1 (WBS 1001.01) › 1.1 Design (5 d, 50%, Pat, a note), 1.2 Build (3 d, after Design), 1.3 Review (milestone,
    /// after Build); 2 Ship (1 d, 2SS+1d).
    /// </summary>
    private async Task<Plan> SeedAsync()
    {
        var sheetId = (await new SheetService(_factory, TimeProvider.System).CreateAsync(new CreateSheetRequest("Apollo plan", null, true))).Id;
        var columns = new ColumnService(_factory, TimeProvider.System);
        var percent = await columns.CreateAsync(sheetId, new CreateColumnRequest("% Complete", ColumnKind.Number));
        var notes = await columns.CreateAsync(sheetId, new CreateColumnRequest("Notes", ColumnKind.Text));
        await using (var db = await _factory.CreateAsync())
        {
            db.Set<AppUser>().AddRange(new AppUser { Email = "pat@example.com", DisplayName = "Pat Lee" }, new AppUser { Email = "sam@example.com", DisplayName = "Sam Ortiz" });
            await db.SaveChangesAsync();
        }

        var phase = await _items.CreateAsync(sheetId, new CreateItemRequest("Phase 1"));
        await _items.UpdateAsync(sheetId, phase.Id, new UpdateItemRequest("Phase 1", null, null, "1001.01", new Dictionary<string, string?>()));
        var design = await _items.CreateAsync(sheetId, new CreateItemRequest("Design", phase.Id, Start: Mon, Finish: Mon.AddDays(4),
            Values: new Dictionary<string, string?> { [percent.Id.ToString()] = "50", [notes.Id.ToString()] = "Agree the layout first." }));
        var build = await _items.CreateAsync(sheetId, new CreateItemRequest("Build", phase.Id, Start: Mon.AddDays(7), Finish: Mon.AddDays(9)));
        var review = await _items.CreateAsync(sheetId, new CreateItemRequest("Review", phase.Id, Start: Mon.AddDays(9), Finish: Mon.AddDays(9), IsMilestone: true));
        var ship = await _items.CreateAsync(sheetId, new CreateItemRequest("Ship", Start: Mon.AddDays(8), Finish: Mon.AddDays(8)));
        await _plan.SetPredecessorsAsync(sheetId, build.Id, "2");
        await _plan.SetPredecessorsAsync(sheetId, review.Id, "3");
        await _plan.SetPredecessorsAsync(sheetId, ship.Id, "2SS+1d");
        await using (var db = await _factory.CreateAsync())
        {
            db.Set<ItemAssignee>().Add(new ItemAssignee { SheetId = sheetId, ItemId = design.Id, Email = "pat@example.com", AssignedBy = "test" });
            await db.SaveChangesAsync();
        }

        return new Plan(sheetId, phase, design, build, review, ship, percent, notes);
    }

    private async Task<string> StateAsync(Guid sheetId)
    {
        var items = await _items.ListAsync(sheetId);
        var plan = await _plan.GetAsync(sheetId);
        await using var db = await _factory.CreateAsync();
        var people = db.Set<ItemAssignee>().Where(a => a.SheetId == sheetId).Select(a => a.ItemId + a.Email).OrderBy(x => x).ToList();
        return string.Join("\n", plan.Tasks.Select(t =>
        {
            var i = items.Single(x => x.Id == t.ItemId);
            return $"{t.OutlineNumber} {i.Title} {i.Start} {i.Finish} {i.IsMilestone} {i.CostpointWbsId} {t.Predecessors} {t.PercentComplete} "
                + string.Join(",", i.Values.OrderBy(v => v.Key).Select(v => v.Key + "=" + v.Value));
        })) + "\n" + string.Join(",", people);
    }

    private static byte[] Edit(byte[] workbook, Action<IXLWorksheet> change)
    {
        using var book = new XLWorkbook(new MemoryStream(workbook));
        change(book.Worksheet("Plan"));
        using var stream = new MemoryStream();
        book.SaveAs(stream);
        return stream.ToArray();
    }

    private static IXLRow Row(IXLWorksheet sheet, string name) => sheet.RowsUsed().Single(r => r.Cell(5).GetString() == name);

    [Fact]
    public async Task Export_shows_the_hierarchy_and_reimporting_it_unchanged_changes_nothing()
    {
        var plan = await SeedAsync();
        var file = await _plan.ExportAsync(plan.SheetId);
        Assert.Equal("Apollo plan plan.xlsx", file.FileName);

        using (var book = new XLWorkbook(new MemoryStream(file.Content)))
        {
            var sheet = book.Worksheet("Plan");
            Assert.Equal(PlanWorkbook.Headers, sheet.Row(1).Cells(1, PlanWorkbook.Headers.Length).Select(c => c.GetString()));
            Assert.Equal(1, Row(sheet, "Design").Cell(5).Style.Alignment.Indent);
            Assert.Equal(1, Row(sheet, "Design").OutlineLevel);
            Assert.True(Row(sheet, "Phase 1").Cell(5).Style.Font.Bold);
            Assert.Equal("1.1", Row(sheet, "Design").Cell(2).GetString());
            Assert.Equal(5, Row(sheet, "Design").Cell(6).GetValue<int>());
            Assert.Equal(Mon.ToDateTime(TimeOnly.MinValue), Row(sheet, "Design").Cell(7).GetDateTime());
            Assert.Equal("Pat Lee", Row(sheet, "Design").Cell(11).GetString());
            Assert.Equal("Agree the layout first.", Row(sheet, "Design").Cell(12).GetString());
            Assert.Equal(0, Row(sheet, "Review").Cell(6).GetValue<int>());
            Assert.Equal("3", Row(sheet, "Review").Cell(9).GetString());
            Assert.Equal("2SS+1d", Row(sheet, "Ship").Cell(9).GetString());
            Assert.Equal("1001.01", Row(sheet, "Phase 1").Cell(4).GetString());
            Assert.NotNull(book.Worksheet("How to import"));
        }

        var before = await StateAsync(plan.SheetId);
        var request = new PlanImportRequest(file.FileName, file.Content);
        var preview = await _plan.PreviewImportAsync(plan.SheetId, request);
        Assert.Empty(preview.Issues);
        Assert.Empty(preview.Changes);
        Assert.Equal(5, preview.Rows);
        Assert.Equal(5, preview.Unchanged);

        await _plan.ApplyImportAsync(plan.SheetId, request);
        Assert.Equal(before, await StateAsync(plan.SheetId));
    }

    [Fact]
    public async Task Import_previews_additions_changes_and_removals_then_applies_them()
    {
        var plan = await SeedAsync();
        var file = await _plan.ExportAsync(plan.SheetId);
        var edited = Edit(file.Content, sheet =>
        {
            Row(sheet, "Design").Cell(6).Value = 8; // longer: Finish follows from Start
            Row(sheet, "Build").Cell(11).Value = "Sam Ortiz; pat@example.com";
            Row(sheet, "Build").Cell(5).Value = "Build it";
            Row(sheet, "Ship").Delete();
            var test = sheet.Row(6); // where Ship was: after Review, now under Phase 1
            test.Cell(1).Value = 9;
            test.Cell(3).Value = 2;
            test.Cell(5).Value = "Test";
            test.Cell(6).Value = 2;
            test.Cell(7).Value = new DateTime(2026, 10, 5);
            test.Cell(9).Value = "3";
            test.Cell(10).Value = 10;
        });
        var request = new PlanImportRequest("Apollo plan plan.xlsx", edited);
        var before = await StateAsync(plan.SheetId);

        var preview = await _plan.PreviewImportAsync(plan.SheetId, request);

        Assert.False(preview.HasErrors, string.Join("; ", preview.Issues.Select(i => i.Message)));
        var added = Assert.Single(preview.Changes, c => c.Kind == PlanImportChangeKind.Added);
        Assert.Equal("Test", added.Title);
        var removed = Assert.Single(preview.Changes, c => c.Kind == PlanImportChangeKind.Removed);
        Assert.Equal(plan.Ship.Id, removed.ItemId);
        var design = Assert.Single(preview.Changes, c => c.ItemId == plan.Design.Id);
        Assert.Contains(design.Details, d => d.StartsWith("Finish", StringComparison.Ordinal));
        var build = Assert.Single(preview.Changes, c => c.ItemId == plan.Build.Id);
        Assert.Contains("Renamed from \"Build\"", build.Details);
        Assert.Contains(build.Details, d => d.StartsWith("Resources", StringComparison.Ordinal));
        Assert.Contains("Start 10/12/2026 → 10/15/2026", build.Details); // pushed by the longer Design, as it will be on import
        Assert.Contains("10/20/2026", Assert.Single(added.Details)); // Test follows Build, not the 10/05 typed in the file
        Assert.Contains("Start 10/14/2026 → 10/19/2026", Assert.Single(preview.Changes, c => c.ItemId == plan.Review.Id).Details); // follows Build
        Assert.Equal(1, preview.Unchanged); // Phase 1 (a summary)
        Assert.Equal(before, await StateAsync(plan.SheetId)); // nothing applied yet

        await _plan.ApplyImportAsync(plan.SheetId, request);

        var items = await _items.ListAsync(plan.SheetId);
        Assert.DoesNotContain(items, i => i.Id == plan.Ship.Id);
        var designNow = items.Single(i => i.Id == plan.Design.Id);
        Assert.Equal(Mon.AddDays(9), designNow.Finish); // 8 working days from Mon 5 Oct
        var buildNow = items.Single(i => i.Id == plan.Build.Id);
        Assert.Equal("Build it", buildNow.Title);
        Assert.Equal(Mon.AddDays(10), buildNow.Start); // pushed: it still follows Design
        var testTask = items.Single(i => i.Title == "Test");
        Assert.Equal(plan.Phase.Id, testTask.ParentId);
        Assert.Equal(Mon.AddDays(15), testTask.Start); // after Build (Thu 15 – Mon 19 Oct)
        Assert.Equal("10", testTask.Values[plan.Percent.Id.ToString()]);
        var outline = await _plan.GetAsync(plan.SheetId);
        Assert.Equal(["Phase 1", "Design", "Build it", "Review", "Test"], outline.Tasks.Select(t => items.Single(i => i.Id == t.ItemId).Title));
        Assert.Equal("3", outline.For(testTask.Id)!.Predecessors);
        await using var db = await _factory.CreateAsync();
        Assert.Equal(["pat@example.com", "sam@example.com"], db.Set<ItemAssignee>().Where(a => a.ItemId == plan.Build.Id).Select(a => a.Email).OrderBy(e => e).ToList());
    }

    [Fact]
    public async Task Import_lists_row_errors_and_refuses_to_apply_them()
    {
        var plan = await SeedAsync();
        var before = await StateAsync(plan.SheetId);
        var content = PlanWorkbook.Write("x", []);
        content = Edit(content, sheet =>
        {
            object?[][] rows =
            [
                [1, null, 1, null, "Phase", null, null, null, null, null, null, null, null],
                [2, null, 3, null, "Too deep", 2, new DateTime(2026, 10, 5), null, null, null, null, null, null],
                [3, null, 2, null, "Bad dates", null, "next Tuesday", new DateTime(2026, 10, 1), null, null, null, null, null],
                [4, null, 2, null, "Backwards", null, new DateTime(2026, 10, 9), new DateTime(2026, 10, 5), null, null, null, null, null],
                [5, null, 2, null, "Waits on nobody", 1, new DateTime(2026, 10, 5), null, "42", null, "Nobody Here", null, null],
                [6, null, 2, null, "Loop A", 1, new DateTime(2026, 10, 5), null, "7", 150, null, null, null],
                [7, null, 2, null, "Loop B", 1, new DateTime(2026, 10, 5), null, "6", null, null, null, null],
                [7, null, 2, null, "", 1, new DateTime(2026, 10, 5), null, "1", null, null, null, null],
            ];
            for (var r = 0; r < rows.Length; r++)
            {
                for (var c = 0; c < rows[r].Length; c++)
                {
                    if (rows[r][c] is { } value)
                    {
                        sheet.Cell(r + 2, c + 1).Value = XLCellValue.FromObject(value);
                    }
                }
            }
        });
        var request = new PlanImportRequest("broken.xlsx", content);

        var preview = await _plan.PreviewImportAsync(plan.SheetId, request);

        Assert.True(preview.HasErrors);
        string Errors(int row) => string.Join(" | ", preview.Issues.Where(i => i.Row == row && i.IsError).Select(i => i.Message));
        Assert.Contains("more than one level below", Errors(3));
        Assert.Contains("\"next Tuesday\" isn't a date", Errors(4));
        Assert.Contains("is before Start", Errors(5));
        Assert.Contains("Predecessor 42 isn't an ID", Errors(6));
        Assert.Contains("\"Nobody Here\" isn't an active Workbench user", Errors(6));
        Assert.Contains("isn't between 0 and 100", Errors(7));
        Assert.Contains("loop", Errors(8));
        Assert.Contains("Task name is empty", Errors(9));
        Assert.Contains("ID 7 is used by an earlier row", Errors(9));
        Assert.Contains("summary or subtask", Errors(9)); // 1 is its summary

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => _plan.ApplyImportAsync(plan.SheetId, request));
        Assert.Contains("nothing was changed", ex.Message);
        Assert.Equal(before, await StateAsync(plan.SheetId));
    }

    [Fact]
    public async Task The_blank_template_has_the_columns_and_imports_into_an_empty_plan()
    {
        var template = await _plan.TemplateAsync();
        var sheetId = (await new SheetService(_factory, TimeProvider.System).CreateAsync(new CreateSheetRequest("New", null, true))).Id;

        var empty = await _plan.PreviewImportAsync(sheetId, new PlanImportRequest(template.FileName, template.Content));
        Assert.True(empty.HasErrors); // no tasks

        var filled = Edit(template.Content, sheet =>
        {
            sheet.Cell(2, 3).Value = 1;
            sheet.Cell(2, 5).Value = "Kickoff";
            sheet.Cell(2, 6).Value = "0";
            sheet.Cell(2, 7).Value = "Mon 10/5/26";
            sheet.Cell(3, 3).Value = 1;
            sheet.Cell(3, 5).Value = "Requirements";
            sheet.Cell(3, 6).Value = "2w";
            sheet.Cell(3, 7).Value = "10/5/2026";
            sheet.Cell(3, 9).Value = "1";
            sheet.Cell(3, 12).Value = "Talk to the customer";
        });
        await _plan.ApplyImportAsync(sheetId, new PlanImportRequest("plan.xlsx", filled));

        var items = await _items.ListAsync(sheetId);
        var kickoff = items.Single(i => i.Title == "Kickoff");
        Assert.True(kickoff.IsMilestone);
        Assert.Equal(Mon, kickoff.Start);
        var requirements = items.Single(i => i.Title == "Requirements");
        Assert.Equal(Mon, requirements.Start); // a milestone's successor may start that day
        Assert.Equal(Mon.AddDays(11), requirements.Finish); // 10 working days
        var sheet = await new SheetService(_factory, TimeProvider.System).GetAsync(sheetId);
        var notes = Assert.Single(sheet!.Columns, c => c.Name == "Notes");
        Assert.Equal("Talk to the customer", requirements.Values[notes.Id.ToString()]);
    }

    [Fact]
    public async Task Only_people_who_can_edit_the_plan_can_import_it()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var plan = seeded.Host.Get<IPlanScheduleService>();
        var file = await seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().ExportAsync(seeded.SheetId);
        var request = new PlanImportRequest(file.FileName, file.Content);

        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().PreviewImportAsync(seeded.SheetId, request));
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() => seeded.As(SeededHost.Commenter).Host.Get<IPlanScheduleService>().ApplyImportAsync(seeded.SheetId, request));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(SeededHost.Outsider).Host.Get<IPlanScheduleService>().ExportAsync(seeded.SheetId));

        var applied = await seeded.As(SeededHost.Editor).Host.Get<IPlanScheduleService>().ApplyImportAsync(seeded.SheetId, request);
        Assert.Empty(applied.Changes);
        _ = plan;
    }
}

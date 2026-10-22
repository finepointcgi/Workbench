using System.Net;
using System.Text;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Imports.Smartsheet;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Imports;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Imports;

public sealed class SmartsheetImportTests
{
    private const string SheetId = "4583173393803140";

    private static void RecordedApi(ImportHost host)
    {
        host.Smartsheet.Json("2.0/sheets?includeAll=true", Samples.Fixture("smartsheet-sheets.json"));
        host.Smartsheet.Json($"2.0/sheets/{SheetId}?include=objectValue&level=2", Samples.Fixture("smartsheet-sheet-website.json"));
        host.Smartsheet.Json($"2.0/sheets/{SheetId}/discussions?include=comments&includeAll=true", Samples.Fixture("smartsheet-discussions.json"));
        host.Smartsheet.Json($"2.0/sheets/{SheetId}/attachments?includeAll=true", Samples.Fixture("smartsheet-attachments.json"));
        host.Smartsheet.Json($"2.0/sheets/{SheetId}/attachments/3333333333333333", Samples.Fixture("smartsheet-attachment-3333333333333333.json"));
        host.Smartsheet.Bytes("https://smartsheet-files.s3.amazonaws.com/attachments/wireframes.pdf?X-Amz-Signature=abc123", Encoding.ASCII.GetBytes("%PDF-1.4 fake"));
    }

    private static async Task<ImportJobDetailDto> ConnectAndPickAsync(ImportHost host, ImportOptionsDto? options = null)
    {
        var connected = await host.Imports.ConnectSmartsheetAsync(new ConnectSmartsheetRequest(ImportHost.Token, options));
        Assert.Equal(["Website Relaunch", "Risk Register"], connected.AvailableSheets.Select(s => s.Name));
        return await host.Imports.SelectSmartsheetSheetsAsync(connected.Job.Id, new SelectSmartsheetSheetsRequest([SheetId]));
    }

    private static async Task<ImportJobDetailDto> RunAsync(ImportHost host, ImportJobDetailDto job, ImportOptionsDto options)
    {
        await host.Imports.SaveMappingAsync(job.Job.Id, new SaveImportMappingRequest(job.Mapping, options));
        await host.Imports.ValidateAsync(job.Job.Id);
        var dry = await host.Imports.DryRunAsync(job.Job.Id);
        return await host.Imports.ImportAsync(dry.Job.Id);
    }

    [Fact]
    public async Task Api_sheet_maps_types_hierarchy_values_and_predecessors_with_lag()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        var job = await ConnectAndPickAsync(host);

        // Suggested mapping: Gantt-tagged columns become fields, the rest columns with converted types.
        Assert.Equal("Task Name", job.Mapping.Fields["Title"]);
        Assert.Equal("Start", job.Mapping.Fields["Start"]);
        Assert.Equal("Finish", job.Mapping.Fields["Finish"]);
        Assert.Equal("Duration", job.Mapping.Fields["Duration"]);
        Assert.Equal("Predecessors", job.Mapping.Fields["Predecessors"]);
        Assert.Equal("WBS", job.Mapping.Fields["WbsCode"]);
        var columns = job.Mapping.Columns.ToDictionary(c => c.Source);
        Assert.Equal(ColumnKind.Dropdown, columns["Status"].Type);
        Assert.Equal(["Not Started", "In Progress", "Complete", "Blocked"], columns["Status"].Options);
        Assert.Equal(ColumnKind.Person, columns["Assigned To"].Type);
        Assert.Equal(ColumnKind.Checkbox, columns["Done"].Type);
        Assert.Equal(ColumnKind.Currency, columns["Budget"].Type);
        Assert.False(columns["Modified"].Include);
        Assert.DoesNotContain("Duration", columns.Keys);

        var done = await RunAsync(host, job, job.Options);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        Assert.False(done.Job.HasToken);

        var sheet = Assert.Single(await host.Get<ISheetService>().ListAsync());
        Assert.Equal("Website Relaunch", sheet.Name);
        Assert.True(sheet.SchedulingEnabled);
        var detail = (await host.Get<ISheetService>().GetAsync(sheet.Id))!;
        Assert.Equal(["Status", "Assigned To", "Done", "Budget"], detail.Columns.Select(c => c.Name));
        var status = detail.Columns.Single(c => c.Name == "Status");
        Assert.Contains("Blocked", status.Options);

        var items = await host.Get<IItemService>().ListAsync(sheet.Id);
        Assert.Equal(6, items.Count);
        var byTitle = items.ToDictionary(i => i.Title);
        Assert.Null(byTitle["Design"].ParentId);
        Assert.Equal(byTitle["Design"].Id, byTitle["Wireframes"].ParentId);
        Assert.Equal(byTitle["Front end"].Id, byTitle["Accessibility review"].ParentId);
        var wireframes = byTitle["Wireframes"];
        Assert.Equal((new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 9)), (wireframes.Start!.Value, wireframes.Finish!.Value));
        Assert.Equal("jane.smith@example.com", wireframes.Values[detail.Columns.Single(c => c.Name == "Assigned To").Id.ToString()]);
        Assert.Equal("Complete", wireframes.Values[status.Id.ToString()]);
        Assert.Equal("true", wireframes.Values[detail.Columns.Single(c => c.Name == "Done").Id.ToString()]);
        Assert.Equal("1200", wireframes.Values[detail.Columns.Single(c => c.Name == "Budget").Id.ToString()]);
        Assert.Equal("3001.01.01", wireframes.CostpointWbsId);
        // No finish in Smartsheet: start + 3 working days (Wed–Fri).
        Assert.Equal(new DateOnly(2026, 10, 16), byTitle["Visual design"].Finish);

        var dependencies = await host.Get<IDependencyService>().ListAsync(sheet.Id);
        Assert.Equal(4, dependencies.Count);
        var visual = dependencies.Single(d => d.SuccessorId == byTitle["Visual design"].Id);
        Assert.Equal((wireframes.Id, DependencyKind.FinishToStart, 2), (visual.PredecessorId, visual.Type, visual.LagDays));
        var lead = dependencies.Single(d => d.SuccessorId == byTitle["Front end"].Id && d.PredecessorId == wireframes.Id);
        Assert.Equal((DependencyKind.StartToStart, -1), (lead.Type, lead.LagDays));
        var weekLag = dependencies.Single(d => d.SuccessorId == byTitle["Accessibility review"].Id);
        Assert.Equal((DependencyKind.FinishToFinish, 5), (weekLag.Type, weekLag.LagDays));

        // Comments and attachments were off.
        Assert.DoesNotContain(host.Smartsheet.Requests, r => r.Path.Contains("discussions") || r.Path.Contains("attachments"));
    }

    [Fact]
    public async Task Api_token_is_stored_encrypted_and_discarded_after_the_import()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        var job = await ConnectAndPickAsync(host);
        Assert.True(job.Job.HasToken);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var stored = await db.Set<ImportJob>().SingleAsync();
            Assert.NotNull(stored.EncryptedToken);
            Assert.DoesNotContain(ImportHost.Token, stored.EncryptedToken);
        }

        Assert.All(host.Smartsheet.Requests, r => Assert.Equal($"Bearer {ImportHost.Token}", r.Auth));
        await RunAsync(host, job, job.Options);
        await using (var db = await host.Get<IWorkbenchDbContextFactory>().CreateAsync())
        {
            Assert.Null((await db.Set<ImportJob>().SingleAsync()).EncryptedToken);
        }
    }

    [Fact]
    public async Task Comments_and_attachments_import_when_asked()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        var job = await ConnectAndPickAsync(host);
        var done = await RunAsync(host, job, job.Options with { IncludeComments = true, IncludeAttachments = true });
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        Assert.Contains(done.Errors, e => e.IsWarning && e.Message.Contains("Figma board"));

        var sheet = Assert.Single(await host.Get<ISheetService>().ListAsync());
        var wireframes = (await host.Get<IItemService>().ListAsync(sheet.Id)).Single(i => i.Title == "Wireframes");
        var comments = await host.Get<ICommentService>().ListAsync(sheet.Id, wireframes.Id);
        Assert.Equal(2, comments.Count);
        Assert.Contains(comments, c => c.Body.Contains("Approved by the customer") && c.Body.Contains("Jane Smith"));
        var attachment = Assert.Single(await host.Get<IAttachmentService>().ListAsync(sheet.Id, wireframes.Id));
        Assert.Equal("wireframes.pdf", attachment.FileName);

        // The pre-signed download link gets no token.
        Assert.Contains(host.Smartsheet.Requests, r => r.Path.StartsWith("https://smartsheet-files", StringComparison.Ordinal) && r.Auth is null);
    }

    [Fact]
    public async Task Repeat_api_import_matches_rows_by_smartsheet_id()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        var first = await ConnectAndPickAsync(host);
        await RunAsync(host, first, first.Options);

        // The sheet changed in Smartsheet: a row was renamed.
        host.Smartsheet.Json($"2.0/sheets/{SheetId}?include=objectValue&level=2",
            Samples.Fixture("smartsheet-sheet-website.json").Replace("\"Visual design\"", "\"Visual design v2\""));
        var second = await ConnectAndPickAsync(host);
        var done = await RunAsync(host, second, second.Options);
        Assert.Equal(1, done.Job.Counts.Updates);
        Assert.Equal(0, done.Job.Counts.Creates);

        var sheet = Assert.Single(await host.Get<ISheetService>().ListAsync());
        var items = await host.Get<IItemService>().ListAsync(sheet.Id);
        Assert.Equal(6, items.Count);
        Assert.Contains(items, i => i.Title == "Visual design v2");
        Assert.Equal(4, (await host.Get<IDependencyService>().ListAsync(sheet.Id)).Count);
    }

    [Fact]
    public async Task Rate_limited_requests_are_retried_with_backoff()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        host.Smartsheet.Throttle.Enqueue(HttpStatusCode.TooManyRequests);
        host.Smartsheet.Throttle.Enqueue(HttpStatusCode.TooManyRequests);
        var connected = await host.Imports.ConnectSmartsheetAsync(new ConnectSmartsheetRequest(ImportHost.Token));
        Assert.Equal(2, connected.AvailableSheets.Count);
        Assert.Equal(3, host.Smartsheet.Requests.Count);

        var policy = new SmartsheetRetryPolicy();
        Assert.Equal(TimeSpan.FromSeconds(7), policy.DelayFor(0, new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7))));
        Assert.InRange(policy.DelayFor(3, null).TotalSeconds, 6.4, 9.6);
        Assert.InRange(policy.DelayFor(20, null).TotalSeconds, 48, 72);
    }

    [Fact]
    public async Task Bad_token_is_refused()
    {
        await using var host = await ImportHost.CreateAsync();
        RecordedApi(host);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => host.Imports.ConnectSmartsheetAsync(new ConnectSmartsheetRequest("wrong")));
        Assert.Contains("token", ex.Message);
    }

    [Fact]
    public void Predecessor_and_duration_text_is_parsed()
    {
        var parsed = SmartsheetSources.ParsePredecessors("3FS +2d, 4SS-1d; 7, 9FF +1w, 12SF +4h");
        Assert.Equal([(3, "FS", 2), (4, "SS", -1), (7, "FS", 0), (9, "FF", 5), (12, "SF", 1)],
            parsed.Select(p => (p.RowNumber!.Value, p.Type, p.LagDays)));
        Assert.Equal(10m, SmartsheetSources.ParseDuration("2w"));
        Assert.Equal(3m, SmartsheetSources.ParseDuration("~3d"));
        Assert.Equal(0.5m, SmartsheetSources.ParseDuration("4h"));
    }

    [Fact]
    public async Task Xlsx_export_imports_with_outline_hierarchy_and_links_rows_to_the_project_wbs()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportAllCostpointSamplesAsync();
        var project = (await host.Get<IProjectService>().ListProjectsAsync()).Single(p => p.Code == "2001");
        var options = new ImportOptionsDto(ProjectId: project.Id);

        var dry = await host.PrepareAsync(ImportSource.SmartsheetExcel, ImportKind.Sheets, [Samples.File("samples/import/smartsheet/project-plan.xlsx")], options);
        Assert.Equal("Task Name", dry.Mapping.Fields["Title"]);
        Assert.Equal("WBS", dry.Mapping.Fields["WbsCode"]);
        Assert.Equal("Predecessors", dry.Mapping.Fields["Predecessors"]);
        Assert.Contains(dry.Plan, p => p.EntityType == "WBS link" && p.Key == "2001.02.02");
        Assert.Contains(dry.Plan, p => p.EntityType == "Plan sheet");
        var columns = dry.Mapping.Columns.ToDictionary(c => c.Source);
        Assert.Equal(ColumnKind.Person, columns["Assigned To"].Type);
        Assert.Equal(ColumnKind.Dropdown, columns["Status"].Type);
        Assert.Equal(ColumnKind.Checkbox, columns["At Risk"].Type);

        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        Assert.Equal(0, done.Job.Counts.Errors);

        project = (await host.Get<IProjectService>().GetProjectAsync(project.Id))!;
        Assert.NotNull(project.SheetId);
        var items = await host.Get<IItemService>().ListAsync(project.SheetId!.Value);
        Assert.Equal(10, items.Count);
        var byTitle = items.ToDictionary(i => i.Title);
        Assert.Equal(byTitle["Development"].Id, byTitle["Ingest pipeline"].ParentId);
        Assert.Equal(byTitle["Data Platform"].Id, byTitle["Development"].ParentId);
        Assert.Null(byTitle["Data Platform"].ParentId);
        // Ingest pipeline has no finish: 55 working days from Mon 2026-11-16.
        Assert.Equal(new DateOnly(2027, 1, 29), byTitle["Ingest pipeline"].Finish);

        var wbs = await host.Get<IProjectService>().ListWbsAsync(project.Id);
        Assert.Equal(byTitle["Development"].Id, wbs.Single(w => w.Code == "2001.02.02").ItemId);
        Assert.Equal(byTitle["Program Management"].Id, wbs.Single(w => w.Code == "2001.01").ItemId);
        Assert.Null(wbs.Single(w => w.Code == "2001.03").ItemId);

        var dependencies = await host.Get<IDependencyService>().ListAsync(project.SheetId.Value);
        Assert.Equal(5, dependencies.Count);
        Assert.Contains(dependencies, d => d.SuccessorId == byTitle["Analytics API"].Id && d.Type == DependencyKind.StartToStart && d.LagDays == 10);
        Assert.Contains(dependencies, d => d.SuccessorId == byTitle["Testing and Deployment"].Id && d.PredecessorId == byTitle["Analytics API"].Id && d.LagDays == 2);

        // Again: nothing new.
        var again = await host.PrepareAsync(ImportSource.SmartsheetExcel, ImportKind.Sheets, [Samples.File("samples/import/smartsheet/project-plan.xlsx")], options);
        Assert.Equal(0, again.Job.Counts.Creates);
        Assert.Equal(0, again.Job.Counts.Updates);
    }

    [Fact]
    public async Task Xlsx_hierarchy_can_come_from_indentation()
    {
        await using var host = await ImportHost.CreateAsync();
        using var workbook = new XLWorkbook();
        var ws = workbook.AddWorksheet("Plan");
        ws.Cell(1, 1).Value = "Task Name";
        ws.Cell(1, 2).Value = "Start Date";
        ws.Cell(1, 3).Value = "End Date";
        string[] titles = ["Phase A", "Task A1", "Sub A1a", "Task A2", "Phase B"];
        int[] indents = [0, 1, 2, 1, 0];
        for (var i = 0; i < titles.Length; i++)
        {
            ws.Cell(i + 2, 1).Value = titles[i];
            ws.Cell(i + 2, 1).Style.Alignment.Indent = indents[i];
            ws.Cell(i + 2, 2).Value = new DateTime(2026, 11, 2).AddDays(i);
            ws.Cell(i + 2, 3).Value = new DateTime(2026, 11, 6).AddDays(i);
        }

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        var done = await host.ImportAsync(ImportSource.SmartsheetExcel, ImportKind.Sheets, [new UploadFileRequest("Indented Plan.xlsx", stream.ToArray())]);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);

        var sheet = Assert.Single(await host.Get<ISheetService>().ListAsync());
        Assert.Equal("Indented Plan", sheet.Name);
        Assert.True(sheet.SchedulingEnabled);
        var items = (await host.Get<IItemService>().ListAsync(sheet.Id)).ToDictionary(i => i.Title);
        Assert.Equal(items["Task A1"].Id, items["Sub A1a"].ParentId);
        Assert.Equal(items["Phase A"].Id, items["Task A2"].ParentId);
        Assert.Null(items["Phase B"].ParentId);
        // Task A1 is a summary on a scheduled sheet: its dates roll up from Sub A1a (Nov 4 – Nov 8).
        Assert.Equal(new DateOnly(2026, 11, 4), items["Task A1"].Start);
    }
}

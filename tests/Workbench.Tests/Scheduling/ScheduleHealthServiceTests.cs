using System.Text;
using ClosedXML.Excel;
using Workbench.Application.Scheduling;
using Workbench.Application.Sheets;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Domain.Tasks;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Scheduling;

/// <summary>The schedule health check over a real plan: what it loads (progress, people, baselines, calendar) and its exports.</summary>
public sealed class ScheduleHealthServiceTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;

    private static async Task<(PlanKit Kit, Guid Sheet, ItemDto Design, ItemDto Build, ItemDto Test, ItemDto Summary)> PlanAsync()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var percent = await new ColumnService(kit.Factory, TimeProvider.System).CreateAsync(sheet, new CreateColumnRequest("% Complete", ColumnKind.Number));
        var summary = await kit.TaskAsync(sheet, "Phase 1", Mon, 1);
        var design = await kit.TaskAsync(sheet, "Design", Mon, 5, summary.Id);
        var build = await kit.TaskAsync(sheet, "Build", Mon.AddDays(7), 10, summary.Id);
        var test = await kit.TaskAsync(sheet, "Test", Mon.AddDays(21), 5, summary.Id);
        await kit.LinkAsync(sheet, design.Id, build.Id);
        await kit.LinkAsync(sheet, build.Id, test.Id);
        await kit.Items.UpdateAsync(sheet, design.Id, new UpdateItemRequest(design.Title, design.Start, design.Finish, null,
            new Dictionary<string, string?> { [percent.Id.ToString()] = "100" }));

        // Design and Build have people; Test doesn't.
        await using (var db = await kit.Factory.CreateAsync())
        {
            foreach (var item in new[] { design, build })
            {
                db.Set<ItemAssignee>().Add(new ItemAssignee { SheetId = sheet, ItemId = item.Id, Email = "dana@example.com", AssignedBy = "test", AssignedAtUtc = DateTime.UtcNow });
            }

            await db.SaveChangesAsync();
        }

        return (kit, sheet, design, build, test, summary);
    }

    [Fact]
    public async Task Assesses_the_detail_tasks_with_their_progress_people_and_the_newest_baseline()
    {
        var (kit, sheet, design, build, test, summary) = await PlanAsync();
        await kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Approved plan"));
        var service = new ScheduleHealthService(kit.Factory, TimeProvider.System);

        // Status date: Build should have started (Mon 10-12) and Design is due (finished Fri 10-09).
        var health = await service.GetAsync(sheet, new ScheduleHealthRequest(Mon.AddDays(8)));

        Assert.Equal(3, health.DetailTasks); // the summary only groups them
        Assert.Equal(2, health.IncompleteTasks);
        Assert.Equal(2, health.Links);
        Assert.Equal("Approved plan", health.BaselineName);
        Assert.Equal(14, health.Checks.Count);
        Assert.DoesNotContain(health.Checks.SelectMany(c => c.Tasks), t => t.ItemId == summary.Id);

        var resources = health.Check("resources")!;
        Assert.Equal(test.Id, Assert.Single(resources.Tasks).ItemId);
        Assert.Equal(HealthCheckStatus.Fail, resources.Status);

        var invalid = health.Check("invalid-dates")!;
        Assert.Equal(build.Id, Assert.Single(invalid.Tasks).ItemId);
        Assert.Equal("Not started, but its start date has passed", invalid.Tasks[0].Detail);

        // Design was due and is done: BEI 1.00, nothing missed.
        Assert.Equal(1.00m, health.Check("bei")!.Value);
        Assert.Equal(HealthCheckStatus.Pass, health.Check("missed-tasks")!.Status);
        Assert.Equal(HealthCheckStatus.Pass, health.Check("critical-path-test")!.Status);
        Assert.Empty(health.Check("logic")!.Tasks); // Design starts the plan and Test ends it
        Assert.Empty(health.Notes);
        Assert.Contains(health.Checks.SelectMany(c => c.Tasks), t => t.ItemId == build.Id && t.TaskNumber == 3 && t.OutlineNumber == "1.2");
        Assert.DoesNotContain(health.Check("resources")!.Tasks, t => t.ItemId == design.Id);
    }

    [Fact]
    public async Task Without_a_baseline_or_percent_column_it_says_so()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var a = await kit.TaskAsync(sheet, "A", Mon, 3);
        var b = await kit.TaskAsync(sheet, "B", Mon.AddDays(3), 3);
        await kit.LinkAsync(sheet, a.Id, b.Id);
        await kit.Items.CreateAsync(sheet, new CreateItemRequest("No dates yet"));

        var health = await new ScheduleHealthService(kit.Factory, TimeProvider.System).GetAsync(sheet);

        Assert.Equal(HealthCheckStatus.NotApplicable, health.Check("bei")!.Status);
        Assert.Equal(HealthCheckStatus.NotApplicable, health.Check("missed-tasks")!.Status);
        Assert.Contains(health.Notes, n => n.Contains("no % Complete column", StringComparison.Ordinal));
        Assert.Contains(health.Notes, n => n.Contains("no baseline", StringComparison.Ordinal));
        Assert.Contains(health.Notes, n => n.StartsWith("1 task has no dates", StringComparison.Ordinal));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() =>
            new ScheduleHealthService(kit.Factory, TimeProvider.System).GetAsync(sheet, new ScheduleHealthRequest(BaselineId: Guid.NewGuid())));
    }

    [Fact]
    public async Task Exports_a_pdf_and_a_workbook_with_every_check_and_flagged_task()
    {
        var (kit, sheet, _, _, test, _) = await PlanAsync();
        var service = new ScheduleHealthService(kit.Factory, TimeProvider.System);
        var request = new ScheduleHealthRequest(Mon.AddDays(8));

        var pdf = await service.ExportPdfAsync(sheet, request);
        Assert.Equal("application/pdf", pdf.ContentType);
        Assert.EndsWith(".pdf", pdf.FileName, StringComparison.Ordinal);
        var text = Encoding.ASCII.GetString(pdf.Content);
        Assert.StartsWith("%PDF-1.4", text, StringComparison.Ordinal);
        Assert.Contains("Critical path length index", text, StringComparison.Ordinal);
        Assert.Contains("(Fail)", text, StringComparison.Ordinal);

        var excel = await service.ExportExcelAsync(sheet, request);
        Assert.EndsWith(".xlsx", excel.FileName, StringComparison.Ordinal);
        using var book = new XLWorkbook(new MemoryStream(excel.Content));
        var summary = book.Worksheet("Schedule health");
        var checkNames = summary.Column(2).CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Logic", checkNames);
        Assert.Contains("Baseline execution index (BEI)", checkNames);
        var flagged = book.Worksheet("Flagged tasks");
        Assert.Contains(flagged.RowsUsed(), r => r.Cell(5).GetString() == test.Title && r.Cell(2).GetString() == "Resources");
    }

    [Fact]
    public async Task Float_lists_total_and_free_float_for_every_detail_task()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();
        var a = await kit.TaskAsync(sheet, "A", Mon, 5);
        var b = await kit.TaskAsync(sheet, "B", Mon, 2);
        var c = await kit.TaskAsync(sheet, "C", Mon.AddDays(7), 1);
        await kit.LinkAsync(sheet, a.Id, c.Id);
        await kit.LinkAsync(sheet, b.Id, c.Id);

        var floats = (await new ScheduleHealthService(kit.Factory, TimeProvider.System).GetFloatAsync(sheet)).ToDictionary(f => f.ItemId);

        Assert.Equal((0, 0, true), (floats[a.Id].TotalFloatDays, floats[a.Id].FreeFloatDays, floats[a.Id].IsCritical));
        Assert.Equal((3, 3, false), (floats[b.Id].TotalFloatDays, floats[b.Id].FreeFloatDays, floats[b.Id].IsCritical));
    }

    [Fact]
    public async Task Viewers_can_check_a_plan_and_outsiders_cant_see_it()
    {
        await using var seeded = await CreateAsync();

        var health = await seeded.As(Viewer).Host.Get<IScheduleHealthService>().GetAsync(seeded.SheetId);
        Assert.Equal(14, health.Checks.Count);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => seeded.As(Outsider).Host.Get<IScheduleHealthService>().GetAsync(seeded.SheetId));
    }
}

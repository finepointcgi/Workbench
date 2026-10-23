using UglyToad.PdfPig;
using Workbench.Contracts.Scheduling;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>The Gantt as a PDF: a readable document with the task table, the bars' labels, a date range and pages.</summary>
public sealed class GanttPdfTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;

    private readonly PlanKit _kit = new();

    private async Task<Guid> PlanAsync(int tasks = 4)
    {
        var sheet = await _kit.SheetAsync("Office (move) & fit-out");
        var phase = await _kit.TaskAsync(sheet, "Phase 1", null, 0);
        Guid? previous = null;
        for (var i = 0; i < tasks; i++)
        {
            var task = await _kit.TaskAsync(sheet, $"Task {i + 1}", Mon.AddDays(i * 7), 5, phase.Id);
            if (previous is { } p)
            {
                await _kit.LinkAsync(sheet, p, task.Id);
            }

            previous = task.Id;
        }

        await _kit.TaskAsync(sheet, "Go live", Mon.AddDays(tasks * 7), 0);
        return sheet;
    }

    private static string Text(byte[] pdf, out int pages)
    {
        using var doc = PdfDocument.Open(pdf);
        pages = doc.NumberOfPages;
        return string.Join("\n", doc.GetPages().Select(p => string.Join(" ", p.GetWords().Select(w => w.Text))));
    }

    [Fact]
    public async Task Fits_the_plan_on_one_page_with_its_task_table()
    {
        var sheet = await PlanAsync();
        var baseline = await _kit.Baselines.CaptureAsync(sheet, new CaptureBaselineRequest("Approved"));

        var file = await _kit.Plan.ExportGanttPdfAsync(sheet, new GanttPdfRequest(BaselineId: baseline.Id));

        Assert.Equal("Office (move) & fit-out Gantt.pdf", file.FileName);
        Assert.Equal("application/pdf", file.ContentType);
        var text = Text(file.Content, out var pages);
        Assert.Equal(1, pages);
        Assert.Contains("Office (move) & fit-out", text);
        foreach (var expected in new[] { "ID", "Task", "name", "Duration", "Start", "Finish", "Phase", "Go", "live", "5", "days", "10/05/26", "Critical", "Baseline", "\"Approved\"", "October" })
        {
            Assert.Contains(expected, text);
        }
    }

    [Fact]
    public async Task A_date_range_leaves_out_tasks_outside_it()
    {
        var sheet = await PlanAsync();

        var file = await _kit.Plan.ExportGanttPdfAsync(sheet, new GanttPdfRequest(From: Mon.AddDays(7), To: Mon.AddDays(11), IncludeTable: true));

        var text = Text(file.Content, out _);
        Assert.Contains("Task 2", text);
        Assert.DoesNotContain("Task 1 ", text + " ");
        Assert.DoesNotContain("Task 3", text);
        Assert.Contains("Oct 12, 2026 to Oct 16, 2026", text);
    }

    [Fact]
    public async Task Without_the_table_only_the_timeline_is_printed()
    {
        var sheet = await PlanAsync();

        var text = Text((await _kit.Plan.ExportGanttPdfAsync(sheet, new GanttPdfRequest(IncludeTable: false))).Content, out _);

        Assert.DoesNotContain("Duration", text);
        Assert.Contains("Task 1", text); // bar labels
    }

    [Fact]
    public async Task Without_fit_to_page_a_long_plan_continues_on_more_pages_and_marks_conflicts()
    {
        var sheet = await PlanAsync(tasks: 30);
        var middle = (await _kit.Items.ListAsync(sheet)).Single(i => i.Title == "Task 10");
        await _kit.Plan.SetConstraintAsync(sheet, middle.Id, new SetPlanConstraintRequest(PlanConstraintKind.FinishNoLaterThan, Mon));

        var file = await _kit.Plan.ExportGanttPdfAsync(sheet, new GanttPdfRequest(FitToPage: false, Paper: GanttPaperSize.Tabloid));

        var text = Text(file.Content, out var pages);
        Assert.True(pages > 1);
        Assert.Contains("Page 1 of", text);
        Assert.Contains("1 constraint conflict", text);
        Assert.Contains("[FNLT", text);
    }

    [Fact]
    public async Task A_backwards_range_is_refused_and_viewers_can_print()
    {
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await _kit.Plan.ExportGanttPdfAsync(await PlanAsync(), new GanttPdfRequest(From: Mon.AddDays(5), To: Mon)));

        await using var seeded = await SeededHost.CreateAsync();
        var file = await seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().ExportGanttPdfAsync(seeded.SheetId, new GanttPdfRequest());
        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(file.Content, 0, 5));
    }
}

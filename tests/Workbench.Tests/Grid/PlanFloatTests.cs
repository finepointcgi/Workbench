using System.Text;
using ClosedXML.Excel;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Automation;

namespace Workbench.Tests.Grid;

/// <summary>Total and free float on a sample plan, and the "critical tasks only" filter (which keeps the tasks' parents).</summary>
public sealed class PlanFloatTests
{
    /// <summary>
    /// Mon 10/19 – Tue 10/27, 2026, weekdays:
    /// <code>
    /// Build (summary)
    ///   A  Mon–Tue ─┬─► B  Wed–Fri ──────────┐
    ///               ├─► C  Wed ──────────────┼─► D  Mon–Tue
    ///               └─► F  Wed ──► G  Thu ───┘
    /// </code>
    /// A, B and D are critical. C can slip 2 days (Thu, Fri) without moving anything; F can slip 1 day overall, but any slip moves G.
    /// </summary>
    private static async Task<(AutomationHost Host, Guid SheetId, Dictionary<string, Guid> Ids)> PlanAsync()
    {
        var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var phase = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Build"));
        var ids = new Dictionary<string, Guid> { ["Build"] = phase.Id };
        async Task Task(string name, int startDay, int finishDay) =>
            ids[name] = (await host.Items.CreateAsync(sheetId, new CreateItemRequest(name, phase.Id, Start: new(2026, 10, startDay), Finish: new(2026, 10, finishDay)))).Id;
        await Task("A", 19, 20);
        await Task("B", 21, 23);
        await Task("C", 21, 21);
        await Task("F", 21, 21);
        await Task("G", 22, 22);
        await Task("D", 26, 27);
        var dependencies = host.Get<IDependencyService>();
        foreach (var (from, to) in new[] { ("A", "B"), ("A", "C"), ("A", "F"), ("B", "D"), ("C", "D"), ("F", "G"), ("G", "D") })
        {
            await dependencies.AddAsync(sheetId, new CreateDependencyRequest(ids[from], ids[to]));
        }

        return (host, sheetId, ids);
    }

    [Fact]
    public async Task Total_and_free_float_for_each_task()
    {
        var (host, sheetId, ids) = await PlanAsync();
        await using var _ = host;

        var plan = await host.Get<IPlanFloatService>().GetAsync(sheetId);

        (int Total, int Free, bool Critical) Of(string name) => plan.For(ids[name]) is { } t ? (t.TotalFloatDays, t.FreeFloatDays, t.IsCritical) : throw new KeyNotFoundException(name);
        Assert.Equal((0, 0, true), Of("A"));
        Assert.Equal((0, 0, true), Of("B"));
        Assert.Equal((0, 0, true), Of("D"));
        Assert.Equal((2, 2, false), Of("C"));
        Assert.Equal((1, 0, false), Of("F"));
        Assert.Equal((1, 1, false), Of("G"));
        Assert.Null(plan.For(ids["Build"])); // a summary row spans its subtasks
    }

    [Fact]
    public async Task The_schedule_health_check_and_the_grid_agree_on_float()
    {
        var (host, sheetId, ids) = await PlanAsync();
        await using var _ = host;

        var grid = await host.Get<IPlanFloatService>().GetAsync(sheetId);
        var health = await host.Get<IScheduleHealthService>().GetFloatAsync(sheetId);

        foreach (var name in new[] { "A", "B", "C", "D", "F", "G" })
        {
            var fromGrid = grid.For(ids[name]);
            var fromHealth = health.SingleOrDefault(t => t.ItemId == ids[name]);
            Assert.True(fromGrid == fromHealth, $"{name}: grid {fromGrid}, schedule health {fromHealth}");
        }
    }

    [Fact]
    public async Task Critical_only_keeps_critical_tasks_with_their_parents_and_exports_the_float()
    {
        var (host, sheetId, ids) = await PlanAsync();
        await using var _ = host;
        var plan = await host.Get<IPlanFloatService>().GetAsync(sheetId);
        var critical = plan.Tasks.Where(t => t.IsCritical).Select(t => t.ItemId).ToHashSet();
        var sheet = (await host.Sheets.GetAsync(sheetId))!;
        var items = await host.Items.ListAsync(sheetId);

        var rows = SheetViewEngine.Apply(items, sheet.Columns, new SheetViewDefinition(CriticalOnly: true), critical);
        Assert.Equal(["Build", "A", "B", "D"], rows.Select(r => r.Item.Title));

        var view = new SheetViewDefinition(ShowFloat: true, CriticalOnly: true);
        Assert.False(view.IsStandard);
        var xlsx = await host.Get<ISheetViewService>().ExportAsync(sheetId, new SheetExportRequest(SheetExportFormat.Xlsx, view, "Critical path"));
        using var workbook = new XLWorkbook(new MemoryStream(xlsx.Content));
        var ws = workbook.Worksheets.First();
        var header = ws.RowsUsed().First(r => r.Cell(1).GetString() == "Title");
        var names = header.CellsUsed().Select(c => c.GetString()).ToList();
        Assert.Contains("Total float (working days)", names);
        Assert.Contains("Free float (working days)", names);
        var titles = ws.RowsUsed().Where(r => r.RowNumber() > header.RowNumber()).Select(r => r.Cell(1).GetString()).Where(t => t.Length > 0).ToList();
        Assert.Equal(["Build", "A", "B", "D"], titles.Take(4));
        Assert.Contains(ws.CellsUsed(), c => c.GetString().Contains("Critical tasks only"));

        // Without "critical only", every task, with its float.
        var all = await host.Get<ISheetViewService>().ExportAsync(sheetId, new SheetExportRequest(SheetExportFormat.Pdf, new SheetViewDefinition(ShowFloat: true)));
        var text = Encoding.ASCII.GetString(all.Content);
        Assert.Contains("(Total float", text);
        Assert.Contains("(G)", text);
    }

    [Fact]
    public void Free_float_never_exceeds_total_float_or_drops_below_zero()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        // A deadline made B late: negative total float; its free float is 0, not negative.
        var analysis = new ScheduleAnalysis(new(2026, 10, 19), new(2026, 10, 21), [a, b],
        [
            new ItemSlack(a, new(2026, 10, 19), new(2026, 10, 19), -2, true),
            new ItemSlack(b, new(2026, 10, 20), new(2026, 10, 21), -2, true),
        ]);
        var plan = Workbench.Application.Scheduling.PlanFloatService.Compute(analysis,
            [new DependencyDto(Guid.NewGuid(), a, b, DependencyKind.FinishToStart, 0)], new HashSet<Guid>());
        Assert.All(plan.Tasks, t => Assert.Equal(0, t.FreeFloatDays));
        Assert.All(plan.Tasks, t => Assert.Equal(-2, t.TotalFloatDays));
    }
}

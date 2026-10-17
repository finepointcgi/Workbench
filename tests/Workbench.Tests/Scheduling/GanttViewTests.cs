using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Gantt;
using Workbench.Ui.Views;

namespace Workbench.Tests.Scheduling;

public sealed class GanttViewTests
{
    private static readonly DateOnly Monday = new(2026, 10, 5);

    [Fact]
    public async Task Renders_rows_bars_milestones_summaries_and_critical_links()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var sheetId = Guid.NewGuid();
        var phase = Item("Phase 1", null, 0, null, null);
        var kickoff = Item("Kickoff", phase.Id, 0, Monday, Monday);
        var design = Item("Design", phase.Id, 1, Monday.AddDays(1), Monday.AddDays(4));
        var docs = Item("Docs", null, 1, Monday, Monday.AddDays(1));
        var link = new DependencyDto(Guid.NewGuid(), kickoff.Id, design.Id, DependencyKind.FinishToStart, 0);
        var analysis = new ScheduleAnalysis(Monday, Monday.AddDays(4), [kickoff.Id, design.Id],
        [
            new ItemSlack(kickoff.Id, Monday, Monday, 0, true),
            new ItemSlack(design.Id, Monday.AddDays(1), Monday.AddDays(4), 0, true),
            new ItemSlack(docs.Id, Monday, Monday.AddDays(1), 3, false),
        ]);
        ctx.Services.AddSingleton<IItemService>(new NoItemService());
        ctx.Services.AddSingleton<IDependencyService>(new FakeDependencyService([link], analysis));
        ctx.Services.AddSingleton<IBaselineService>(new FakeBaselineService());
        var sheet = new SheetDetail(
            new SheetSummary(sheetId, "IMS", null, "Program Office", 4, true, null, DateTime.UtcNow), []);

        var view = ctx.Render<SheetGanttView>(p => p
            .Add(v => v.Sheet, sheet)
            .Add(v => v.Items, [phase, kickoff, design, docs]));

        view.WaitForAssertion(() => Assert.Single(view.FindAll(".gantt-link.critical")));
        var names = view.FindAll(".gantt-task-link").Select(a => a.TextContent).ToList();
        Assert.Equal(["Phase 1", "Kickoff", "Design", "Docs"], names);
        Assert.Equal($"sheets/{sheetId}?item={design.Id}", view.FindAll(".gantt-task-link")[2].GetAttribute("href"));
        Assert.Single(view.FindAll($".gantt-summary[data-item='{phase.Id}']"));
        Assert.Single(view.FindAll($".gantt-milestone.critical[data-item='{kickoff.Id}']"));
        Assert.Single(view.FindAll($".gantt-task.critical[data-item='{design.Id}']"));
        Assert.DoesNotContain("critical", view.Find($".gantt-task[data-item='{docs.Id}']").ClassName);
        Assert.Single(view.FindAll(".gantt-today"));
        Assert.Contains("Critical path: 2 tasks", view.Markup);

        // Collapsing the summary hides its children in both panes.
        await view.Find(".gantt-toggle").ClickAsync(new());
        Assert.Equal(["Phase 1", "Docs"], view.FindAll(".gantt-task-link").Select(a => a.TextContent));
        Assert.Empty(view.FindAll(".gantt-link"));
    }

    [Fact]
    public void Summary_rows_span_their_children_and_scale_snaps_to_mondays()
    {
        var phase = Item("Phase", null, 0, Monday.AddDays(30), Monday.AddDays(31));
        var a = Item("A", phase.Id, 0, Monday.AddDays(2), Monday.AddDays(3));
        var b = Item("B", phase.Id, 1, Monday.AddDays(8), Monday.AddDays(9));

        var rows = GanttLayout.BuildRows([b, phase, a], new HashSet<Guid>());
        var scale = GanttScale.Create(GanttZoom.Week, rows.SelectMany(r => new[] { r.Start, r.Finish }).OfType<DateOnly>(), Monday);

        Assert.Equal(["Phase", "A", "B"], rows.Select(r => r.Item.Title));
        Assert.Equal((Monday.AddDays(2), Monday.AddDays(9)), (rows[0].Start, rows[0].Finish));
        Assert.Equal([0, 1, 1], rows.Select(r => r.Depth));
        Assert.Equal(DayOfWeek.Monday, scale.Start.DayOfWeek);
        Assert.Equal(DayOfWeek.Monday, scale.End.DayOfWeek);
    }

    private static ItemDto Item(string title, Guid? parentId, int order, DateOnly? start, DateOnly? finish) =>
        new(Guid.NewGuid(), parentId, order, title, start, finish, null, new Dictionary<string, string?>());

    private sealed class FakeDependencyService(IReadOnlyList<DependencyDto> dependencies, ScheduleAnalysis analysis) : IDependencyService
    {
        public Task<IReadOnlyList<DependencyDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) => Task.FromResult(dependencies);

        public Task<DependencyDto> AddAsync(Guid sheetId, CreateDependencyRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task RemoveAsync(Guid sheetId, Guid dependencyId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ScheduleAnalysis> AnalyzeAsync(Guid sheetId, CancellationToken cancellationToken = default) => Task.FromResult(analysis);
    }

    private sealed class FakeBaselineService : IBaselineService
    {
        public Task<BaselineSummary> CaptureAsync(Guid sheetId, CaptureBaselineRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<BaselineSummary>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BaselineSummary>>([]);

        public Task<BaselineDetail> GetAsync(Guid sheetId, Guid baselineId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoItemService : IItemService
    {
        public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

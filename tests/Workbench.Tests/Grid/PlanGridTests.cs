using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Views;

namespace Workbench.Tests.Grid;

/// <summary>The grid as the MS Project-style plan editor: task IDs, outline numbers, Duration and Predecessors, Tab to indent.</summary>
public sealed class PlanGridTests
{
    private static readonly Guid SheetId = Guid.NewGuid();
    private static readonly DateOnly Mon = new(2026, 10, 5);

    private readonly ItemDto _phase = new(Guid.NewGuid(), null, 0, "Phase 1", Mon, Mon.AddDays(4), null, new Dictionary<string, string?>());
    private readonly ItemDto _design;
    private readonly ItemDto _build;

    public PlanGridTests()
    {
        _design = new(Guid.NewGuid(), _phase.Id, 0, "Design", Mon, Mon.AddDays(4), null, new Dictionary<string, string?>());
        _build = new(Guid.NewGuid(), null, 1, "Build", Mon.AddDays(7), Mon.AddDays(9), null, new Dictionary<string, string?>());
    }

    private PlanScheduleDto Plan() => new(SheetId,
    [
        new PlanTaskDto(_phase.Id, 1, "1", 1, true, 5, "", null),
        new PlanTaskDto(_design.Id, 2, "1.1", 2, false, 5, "", null),
        new PlanTaskDto(_build.Id, 3, "2", 1, false, 3, "2", null),
    ], []);

    private IRenderedComponent<SheetGridView> Render(BunitContext ctx) =>
        ctx.Render<SheetGridView>(p => p
            .Add(v => v.Sheet, new SheetDetail(new SheetSummary(SheetId, "Plan", null, "Program Office", 3, true, null, DateTime.UtcNow), []))
            .Add(v => v.Items, [_phase, _design, _build])
            .AddCascadingValue(Plan()));

    [Fact]
    public async Task Shows_task_ids_outline_numbers_durations_and_predecessors()
    {
        await using var ctx = CreateContext(out _, out _);
        var grid = Render(ctx);

        grid.WaitForAssertion(() => Assert.Equal(3, grid.FindAll("tr.wb-row").Count));
        Assert.Equal(["1", "1.1", "2"], grid.FindAll("[data-testid='outline-number']").Select(e => e.TextContent));
        Assert.Equal(["1", "2", "3"], grid.FindAll(".wb-rownum").Select(e => e.TextContent));
        var build = grid.FindAll("tr.wb-row")[2];
        Assert.Equal("3 days", build.QuerySelector("td[data-col='3']")!.TextContent.Trim());
        Assert.Equal("2", build.QuerySelector("td[data-col='4']")!.TextContent.Trim());

        // The summary's dates and duration are rolled up, so they're read-only.
        var phase = grid.FindAll("tr.wb-row")[0];
        Assert.Contains("wb-readonly", phase.QuerySelector("td[data-col='1']")!.ClassName);
        Assert.Contains("wb-readonly", phase.QuerySelector("td[data-col='3']")!.ClassName);
        Assert.DoesNotContain("wb-readonly", build.QuerySelector("td[data-col='1']")!.ClassName);
    }

    [Fact]
    public async Task Tab_in_the_title_indents_and_shift_tab_outdents_through_the_plan_service()
    {
        await using var ctx = CreateContext(out _, out var plan);
        var grid = Render(ctx);

        grid.WaitForAssertion(() => Assert.Equal(3, grid.FindAll("tr.wb-row").Count));
        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='0']")!.Click();
        grid.Find(".wb-grid").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        grid.WaitForAssertion(() => Assert.Single(plan.Calls));
        Assert.Equal(("indent", _build.Id), plan.Calls[0]);

        grid.FindAll("tr.wb-row")[1].QuerySelector("td[data-col='0']")!.Click();
        grid.Find(".wb-grid").KeyDown(new KeyboardEventArgs { Key = "Tab", ShiftKey = true });
        grid.WaitForAssertion(() => Assert.Equal(2, plan.Calls.Count));
        Assert.Equal(("outdent", _design.Id), plan.Calls[1]);

        // Elsewhere Tab still moves between cells.
        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='1']")!.Click();
        grid.Find(".wb-grid").KeyDown(new KeyboardEventArgs { Key = "Tab" });
        Assert.Equal(2, plan.Calls.Count);
    }

    [Fact]
    public async Task Typing_a_duration_or_predecessors_saves_them_and_insert_adds_a_row_above()
    {
        await using var ctx = CreateContext(out var items, out var plan);
        var grid = Render(ctx);

        grid.WaitForAssertion(() => Assert.Equal(3, grid.FindAll("tr.wb-row").Count));
        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='3']")!.DoubleClick();
        grid.Find("input.wb-editor").Input("2w");
        grid.Find(".wb-grid").KeyDown("Enter");
        grid.WaitForAssertion(() => Assert.Contains(("duration:10", _build.Id), plan.Calls));

        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='4']")!.DoubleClick();
        grid.Find("input.wb-editor").Input("1, 2SS+1d");
        grid.Find(".wb-grid").KeyDown("Enter");
        grid.WaitForAssertion(() => Assert.Contains(("predecessors:1, 2SS+1d", _build.Id), plan.Calls));

        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='0']")!.Click();
        grid.Find(".wb-grid").KeyDown(new KeyboardEventArgs { Key = "Insert" });
        grid.WaitForAssertion(() => Assert.Single(items.Creates));
        var created = items.Creates[0];
        Assert.Equal((null, 1), (created.ParentId, created.Order)); // where Build is, pushing it down
        Assert.Equal((_build.Start, _build.Start), (created.Start, created.Finish)); // a one-day task starting with Build
    }

    [Fact]
    public async Task Typing_a_list_of_tasks_needs_no_dialogs()
    {
        await using var ctx = CreateContext(out var items, out _);
        var grid = Render(ctx);
        grid.WaitForAssertion(() => Assert.Equal(3, grid.FindAll("tr.wb-row").Count));

        // The quick-add box: type a name, press Enter.
        grid.Find("[data-testid='grid-quick-add']").Input("Deliver");
        grid.Find("[data-testid='grid-quick-add']").KeyDown("Enter");
        grid.WaitForAssertion(() => Assert.Single(items.Creates));
        Assert.Equal(("Deliver", (Guid?)null), (items.Creates[0].Title, items.Creates[0].ParentId));

        // Enter after renaming the last row starts the next one below it.
        grid.FindAll("tr.wb-row")[2].QuerySelector("td[data-col='0']")!.DoubleClick();
        grid.Find("input.wb-editor").Input("Build it");
        grid.Find(".wb-grid").KeyDown("Enter");
        grid.WaitForAssertion(() => Assert.Equal(2, items.Creates.Count));
        Assert.Equal((null, 2), (items.Creates[1].ParentId, items.Creates[1].Order));

        // Add subtask under the selected row.
        grid.FindAll("tr.wb-row")[0].QuerySelector("td[data-col='0']")!.Click();
        grid.Find("[data-testid='grid-add-child']").Click();
        grid.WaitForAssertion(() => Assert.Equal(3, items.Creates.Count));
        Assert.Equal(_phase.Id, items.Creates[2].ParentId);
    }

    private static BunitContext CreateContext(out FakeItems items, out FakePlan plan)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        items = new FakeItems();
        plan = new FakePlan();
        ctx.Services.AddSingleton<IItemService>(items);
        ctx.Services.AddSingleton<IColumnService>(new NoColumns());
        ctx.Services.AddSingleton<IPlanScheduleService>(plan);
        return ctx;
    }

    private sealed class FakeItems : IItemService
    {
        public List<CreateItemRequest> Creates { get; } = [];

        public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
        {
            Creates.Add(request);
            return Task.FromResult(new ItemDto(Guid.NewGuid(), request.ParentId, request.Order ?? 0, request.Title, request.Start, request.Finish, null, new Dictionary<string, string?>()));
        }

        public Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new ItemDto(itemId, null, 0, request.Title, request.Start, request.Finish, null, request.Values));

        public Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class NoColumns : IColumnService
    {
        public Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class FakePlan : IPlanScheduleService
    {
        public List<(string Call, Guid ItemId)> Calls { get; } = [];

        public Task<PlanScheduleDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task IndentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => Record("indent", itemId);

        public Task OutdentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) => Record("outdent", itemId);

        public Task SetDurationAsync(Guid sheetId, Guid itemId, int days, CancellationToken cancellationToken = default) => Record($"duration:{days}", itemId);

        public Task SetPredecessorsAsync(Guid sheetId, Guid itemId, string? predecessors, CancellationToken cancellationToken = default) =>
            Record($"predecessors:{predecessors}", itemId);

        public Task<PlanFile> ExportAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanFile> TemplateAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanImportPreview> PreviewImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanImportPreview> ApplyImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<PlanConflictDto>> SetConstraintAsync(Guid sheetId, Guid itemId, SetPlanConstraintRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<PlanCalendarDto> GetCalendarAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanCalendarDto> SetCalendarAsync(Guid sheetId, SetPlanCalendarRequest? request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanFile> ExportMsProjectAsync(Guid sheetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<PlanFile> ExportGanttPdfAsync(Guid sheetId, GanttPdfRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        private Task Record(string call, Guid itemId)
        {
            Calls.Add((call, itemId));
            return Task.CompletedTask;
        }
    }
}

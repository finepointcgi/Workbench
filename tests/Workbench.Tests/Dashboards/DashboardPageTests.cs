using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Pages;

namespace Workbench.Tests.Dashboards;

public sealed class DashboardPageTests
{
    private static readonly Guid DashboardId = Guid.NewGuid();
    private static readonly Guid SheetId = Guid.NewGuid();

    private static readonly WidgetDto Metric = new(Guid.NewGuid(), DashboardId, WidgetType.Metric, "Total budget", 0, 3, WidgetHeight.Small,
        new WidgetConfig(SheetId, MetricAggregate.Sum, Guid.NewGuid()));
    private static readonly WidgetDto Text = new(Guid.NewGuid(), DashboardId, WidgetType.RichText, "Notes", 1, 12, WidgetHeight.Small,
        new WidgetConfig(Markdown: "**Hello** <script>alert(1)</script>"));
    private static readonly WidgetDto List = new(Guid.NewGuid(), DashboardId, WidgetType.ItemList, "Open tasks", 2, 6, WidgetHeight.Medium,
        new WidgetConfig(SheetId));

    private static async Task<(BunitContext Ctx, FakeDashboards Dashboards, FakeWidgetData Data, FakeLiveUpdates Live)> SetUpAsync()
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var dashboards = new FakeDashboards([Metric, Text, List]);
        var data = new FakeWidgetData();
        var live = new FakeLiveUpdates();
        ctx.Services.AddSingleton<IDashboardService>(dashboards);
        ctx.Services.AddSingleton<IWidgetDataService>(data);
        ctx.Services.AddSingleton<ISheetLiveUpdates>(live);
        ctx.Services.AddSingleton<ISheetService>(new NoSheets());
        await ctx.Render<MudBlazor.MudPopoverProvider>().InvokeAsync(() => { });
        return (ctx, dashboards, data, live);
    }

    [Fact]
    public async Task Renders_widgets_with_their_data()
    {
        var (ctx, _, _, _) = await SetUpAsync();
        await using var _ = ctx;

        var page = ctx.Render<DashboardPage>(p => p.Add(x => x.DashboardId, DashboardId));

        page.WaitForAssertion(() => Assert.Contains("$16,200", page.Markup));
        Assert.Contains("Program Office overview", page.Markup);
        Assert.Equal(["Total budget", "Notes", "Open tasks"], page.FindAll("[data-widget]").Select(e => e.GetAttribute("data-widget")));
        Assert.Contains("<strong>Hello</strong>", page.Markup);
        Assert.DoesNotContain("<script>", page.Markup); // shown as text, never as markup
        Assert.Contains("&lt;script&gt;", page.Markup);
        Assert.Contains($"sheets/{SheetId}?item=", page.Markup);
        Assert.Contains("Kickoff meeting", page.Markup);
        Assert.Empty(page.FindAll("[aria-label='Move up']")); // not in edit mode
    }

    [Fact]
    public async Task Edit_mode_moves_widgets()
    {
        var (ctx, dashboards, _, _) = await SetUpAsync();
        await using var _ = ctx;
        var page = ctx.Render<DashboardPage>(p => p.Add(x => x.DashboardId, DashboardId));
        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-widget]")));

        page.Find("[data-action='edit']").Click();
        page.FindAll("button[aria-label='Move down']")[0].Click();

        page.WaitForAssertion(() =>
            Assert.Equal(["Notes", "Total budget", "Open tasks"], page.FindAll("[data-widget]").Select(e => e.GetAttribute("data-widget"))));
        Assert.Equal([Text.Id, Metric.Id, List.Id], dashboards.LastReorder);
        Assert.True(page.Find("button[aria-label='Move up']").HasAttribute("disabled")); // first widget can't move up
    }

    [Fact]
    public async Task Live_changes_to_a_sheet_refresh_its_widgets()
    {
        var (ctx, _, data, live) = await SetUpAsync();
        await using var _ = ctx;
        var page = ctx.Render<DashboardPage>(p => p
            .Add(x => x.DashboardId, DashboardId)
            .Add(x => x.LiveDebounce, TimeSpan.FromMilliseconds(10)));
        page.WaitForAssertion(() => Assert.True(live.Handlers.ContainsKey(SheetId)));
        var before = data.Calls.Count(id => id == Metric.Id);

        await live.Handlers[SheetId](new SheetChange(SheetId, SheetChangeKind.ItemUpdated, Guid.NewGuid(), null, "x", DateTime.UtcNow));

        page.WaitForAssertion(() => Assert.True(data.Calls.Count(id => id == Metric.Id) > before));
        Assert.Equal(1, data.Calls.Count(id => id == Text.Id)); // rich text has no sheet, so it isn't recomputed
    }

    private sealed class FakeDashboards(List<WidgetDto> widgets) : IDashboardService
    {
        public List<Guid>? LastReorder { get; private set; }

        private DashboardDto Dto() => new(
            new DashboardSummary(DashboardId, "Program Office overview", "Sample", "pm@example.com", null, widgets.Count, DateTime.UtcNow, DateTime.UtcNow),
            widgets.ToList());

        public Task<DashboardDto> GetAsync(Guid dashboardId, CancellationToken cancellationToken = default) =>
            dashboardId == DashboardId ? Task.FromResult(Dto()) : throw WorkbenchNotFoundException.For("Dashboard", dashboardId);

        public Task<DashboardDto> ReorderWidgetsAsync(Guid dashboardId, ReorderWidgetsRequest request, CancellationToken cancellationToken = default)
        {
            LastReorder = request.WidgetIds.ToList();
            var reordered = request.WidgetIds.Select((id, i) => widgets.Single(w => w.Id == id) with { Order = i }).ToList();
            widgets.Clear();
            widgets.AddRange(reordered);
            return Task.FromResult(Dto());
        }

        public Task<IReadOnlyList<DashboardSummary>> ListAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSummary> CreateAsync(CreateDashboardRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<DashboardSummary> UpdateAsync(Guid dashboardId, UpdateDashboardRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteAsync(Guid dashboardId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WidgetDto> AddWidgetAsync(Guid dashboardId, SaveWidgetRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WidgetDto> UpdateWidgetAsync(Guid dashboardId, Guid widgetId, SaveWidgetRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task DeleteWidgetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakeWidgetData : IWidgetDataService
    {
        public System.Collections.Concurrent.ConcurrentQueue<Guid> Calls { get; } = new();

        public Task<WidgetDataDto> GetAsync(Guid dashboardId, Guid widgetId, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue(widgetId);
            WidgetDataDto data = widgetId == Metric.Id
                ? new(widgetId, WidgetType.Metric, SheetId, "Schedule", null, Metric: new MetricData(16200m, "Sum of Budget", 4, true))
                : widgetId == Text.Id
                    ? new(widgetId, WidgetType.RichText, null, null, null, Markdown: Text.Config.Markdown)
                    : new(widgetId, WidgetType.ItemList, SheetId, "Schedule", null,
                        ItemList: new ItemListData([new WidgetItemRow(Guid.NewGuid(), "Kickoff meeting", new DateOnly(2026, 10, 20), "Done")], 1));
            return Task.FromResult(data);
        }
    }

    private sealed class FakeLiveUpdates : ISheetLiveUpdates
    {
        public System.Collections.Concurrent.ConcurrentDictionary<Guid, Func<SheetChange, Task>> Handlers { get; } = new();

        public Task<IAsyncDisposable> SubscribeAsync(Guid sheetId, Func<SheetChange, Task> handler, CancellationToken cancellationToken = default)
        {
            Handlers[sheetId] = handler;
            return Task.FromResult<IAsyncDisposable>(new Nothing());
        }

        private sealed class Nothing : IAsyncDisposable
        {
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class NoSheets : ISheetService
    {
        public Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<SheetSummary>>([]);
        public Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default) => Task.FromResult<SheetDetail?>(null);
        public Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

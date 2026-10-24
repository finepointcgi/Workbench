using Bunit;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Grid;
using Workbench.Ui.Views;
using static Workbench.Tests.Grid.SheetGridSpreadsheetTests;

namespace Workbench.Tests.Grid;

/// <summary>The grid shows the newer column types, conditional formatting and plan float.</summary>
public sealed class ColumnTypeGridTests
{
    private static readonly Guid SheetId = Guid.NewGuid();
    private static readonly ColumnDto Tags = new(Guid.NewGuid(), "Tags", ColumnKind.MultiSelect, 0, ["Design", "Build"], null, OptionColors: ["blue", "teal"]);
    private static readonly ColumnDto Link = new(Guid.NewGuid(), "Link", ColumnKind.Hyperlink, 1, [], null);
    private static readonly ColumnDto Health = new(Guid.NewGuid(), "Health", ColumnKind.Symbol, 2, [SymbolSets.Rag], null);
    private static readonly ColumnDto Done = new(Guid.NewGuid(), "Done", ColumnKind.Progress, 3, [], null);
    private static readonly ColumnDto Number = new(Guid.NewGuid(), "ID", ColumnKind.AutoNumber, 4, ["T-", "2", "1"], null);

    // Columns: 0 Title, 1 Tags, 2 Link, 3 Health, 4 Done, 5 ID, 6 WBS.
    private static SheetDetail Sheet(bool scheduled = false) => new(
        new SheetSummary(SheetId, "Program plan", null, "Program Office", 0, scheduled, null, DateTime.UtcNow), [Tags, Link, Health, Done, Number]);

    private static BunitContext Context(out StatefulItemService items)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        items = new StatefulItemService();
        ctx.Services.AddSingleton<IItemService>(items);
        ctx.Services.AddSingleton<IColumnService>(new NoColumns());
        return ctx;
    }

    private static AngleSharp.Dom.IElement Cell(IRenderedComponent<SheetGridView> grid, int row, int col) =>
        grid.Find($"tr[data-row='{row}'] td[data-col='{col}']");

    [Fact]
    public async Task Cells_show_pills_links_symbols_with_labels_and_progress_bars()
    {
        await using var ctx = Context(out var items);
        items.Seed("Design review");
        items.Set("Design review", Tags, "Design\nBuild");
        items.Set("Design review", Link, "[Charter](https://example.com/charter)");
        items.Set("Design review", Health, "Red");
        items.Set("Design review", Done, "40");
        items.Set("Design review", Number, "T-01");
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, items.All));

        Assert.Equal(["Design", "Build"], grid.FindAll("[data-testid=multi-pill]").Select(p => p.TextContent));
        var link = grid.Find("[data-testid=link-cell]");
        Assert.Equal("https://example.com/charter", link.GetAttribute("href"));
        Assert.Equal("noopener noreferrer", link.GetAttribute("rel"));
        Assert.Equal("Charter", link.TextContent);
        Assert.Contains("Red", grid.Find("[data-testid=symbol-cell]").TextContent); // the label, not only a red dot
        var bar = grid.Find("[data-testid=progress-cell]");
        Assert.Equal("40", bar.GetAttribute("aria-valuenow"));
        Assert.Contains("40%", bar.TextContent);
        Assert.Contains("wb-readonly", Cell(grid, 0, 5).ClassName); // auto-number

        // Pasting text puts each type in its stored form; the auto-number is skipped.
        Cell(grid, 0, 1).Click();
        await grid.InvokeAsync(() => grid.FindComponent<SheetGrid>().Instance.PasteAsync("build\tnot a link\tgreen\t75%\tT-99"));
        grid.WaitForAssertion(() => Assert.Equal("Build", items.Value("Design review", Tags)));
        Assert.Equal("[Charter](https://example.com/charter)", items.Value("Design review", Link)); // not a link: skipped
        Assert.Equal("Green", items.Value("Design review", Health));
        Assert.Equal("75", items.Value("Design review", Done));
        Assert.Equal("T-01", items.Value("Design review", Number));
    }

    [Fact]
    public async Task Formatting_rules_color_cells_and_explain_themselves()
    {
        await using var ctx = Context(out var items);
        items.Seed("Stuck one", "Fine");
        items.Set("Stuck one", Health, "Red");
        items.Set("Fine", Health, "Green");
        var rule = new SheetFormatRuleDefinition([new ListFilterCondition(Health.Id.ToString(), ListFilterOperator.Equals, "Red")], Color: "red", Bold: true);
        ctx.Services.AddSingleton<ISheetFormatRuleService>(new FakeRules([new SheetFormatRuleDto(Guid.NewGuid(), rule)]));
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet()).Add(v => v.Items, items.All));

        grid.WaitForAssertion(() => Assert.Contains("background:#C62828", Cell(grid, 0, 0).GetAttribute("style")));
        Assert.Contains("font-weight:600", Cell(grid, 0, 3).GetAttribute("style"));
        Assert.True(string.IsNullOrEmpty(Cell(grid, 1, 0).GetAttribute("style")));
        Assert.Equal("When Health is Red, color the whole row red, bold", grid.Find("tr[data-row='0'] td.wb-gutter").GetAttribute("title"));
        Assert.Contains("Formatting (1)", grid.Find("[data-testid=grid-formatting]").TextContent);
    }

    [Fact]
    public async Task A_plan_shows_float_and_can_show_critical_tasks_only()
    {
        await using var ctx = Context(out var items);
        items.Seed("Critical task", "Slack task");
        var critical = items.Find("Critical task")!;
        var slack = items.Find("Slack task")!;
        ctx.Services.AddSingleton<IPlanFloatService>(new FakeFloat(new PlanFloatDto(
            [new TaskFloatDto(critical.Id, 0, 0, true), new TaskFloatDto(slack.Id, 3, 1, false)])));
        var view = new SheetViewDto(Guid.NewGuid(), SheetId, "Float", true, "pm@example.com", new SheetViewDefinition(ShowFloat: true), false, false, DateTime.UtcNow);
        ctx.Services.AddSingleton<ISheetViewService>(new FakeViewService(view));
        var grid = ctx.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet(scheduled: true)).Add(v => v.Items, items.All));

        grid.WaitForAssertion(() => Assert.Contains("Total float", grid.FindAll("th .wb-th-name").Select(t => t.TextContent)));
        Assert.Contains("Free float", grid.FindAll("th .wb-th-name").Select(t => t.TextContent));
        grid.WaitForAssertion(() => Assert.Equal(4, grid.FindAll("[data-testid=float-cell]").Count));
        Assert.Equal(["0 days", "0 days", "3 days", "1 day"], grid.FindAll("[data-testid=float-cell]").Select(c => c.TextContent));
        Assert.Single(grid.FindAll("[data-testid=critical-mark]")); // spelled out, not only colored

        // A view with "critical tasks only" shows just the critical task (and would keep its parents).
        await using var critOnly = Context(out var sameItems);
        sameItems.Seed("Critical task", "Slack task");
        var plan = new PlanFloatDto([new TaskFloatDto(sameItems.Find("Critical task")!.Id, 0, 0, true), new TaskFloatDto(sameItems.Find("Slack task")!.Id, 3, 1, false)]);
        critOnly.Services.AddSingleton<IPlanFloatService>(new FakeFloat(plan));
        critOnly.Services.AddSingleton<ISheetViewService>(new FakeViewService(view with { Definition = new SheetViewDefinition(CriticalOnly: true) }));
        var filtered = critOnly.Render<SheetGridView>(p => p.Add(v => v.Sheet, Sheet(scheduled: true)).Add(v => v.Items, sameItems.All));
        filtered.WaitForAssertion(() => Assert.Single(filtered.FindAll("tr.wb-row")));
        Assert.Contains("Critical task", filtered.Find("tr.wb-row").TextContent);
        Assert.NotNull(filtered.Find("[data-testid=critical-chip]"));
        Assert.DoesNotContain("Total float", filtered.FindAll("th .wb-th-name").Select(t => t.TextContent));
    }

    private sealed class FakeRules(IReadOnlyList<SheetFormatRuleDto> rules) : ISheetFormatRuleService
    {
        public Task<SheetFormatRulesDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SheetFormatRulesDto(rules, true));

        public Task<SheetFormatRulesDto> SaveAsync(Guid sheetId, SaveSheetFormatRulesRequest request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeFloat(PlanFloatDto plan) : IPlanFloatService
    {
        public Task<PlanFloatDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default) => Task.FromResult(plan);
    }
}

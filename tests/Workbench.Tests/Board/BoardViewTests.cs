using Workbench.Tests.Accessibility;
using AngleSharp.Dom;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Sheets;
using Workbench.Ui.Board;
using Workbench.Ui.Views;

namespace Workbench.Tests.Board;

public sealed class BoardViewTests
{
    [Fact]
    public async Task Groups_items_into_lanes_by_the_status_column()
    {
        var status = ViewFakes.StatusColumn();
        var items = new RecordingItemService();
        await using var ctx = ViewFakes.CreateContext(items);

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status))
            .Add(v => v.Items, [
                ViewFakes.Item("Write plan", status, "In progress"),
                ViewFakes.Item("Kickoff", status, "Done"),
                ViewFakes.Item("Review", status, "In progress", order: 1),
            ]));

        Assert.Equal(["Unassigned", "Not started", "In progress", "Done"], LaneNames(board));
        Assert.Equal(["Write plan", "Review"], CardTitles(Lane(board, "In progress")));
        Assert.Equal(["Kickoff"], CardTitles(Lane(board, "Done")));
        Assert.Empty(CardTitles(Lane(board, "Not started")));
    }

    [Fact]
    public async Task Items_without_a_value_go_in_the_unassigned_lane()
    {
        var status = ViewFakes.StatusColumn();
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status))
            .Add(v => v.Items, [ViewFakes.Item("Loose end"), ViewFakes.Item("Blank", status, ""), ViewFakes.Item("Done thing", status, "Done")]));

        Assert.Equal(["Loose end", "Blank"], CardTitles(Lane(board, "Unassigned")));
    }

    [Fact]
    public void Prefers_a_column_named_status_over_other_dropdowns()
    {
        var priority = ViewFakes.StatusColumn("Priority") with { Order = 0, Options = ["High", "Low"] };
        var status = ViewFakes.StatusColumn() with { Order = 1 };
        var sheet = ViewFakes.Sheet(false, priority, status);

        Assert.Equal(status.Id, BoardLanes.DefaultColumn(sheet)?.Id);
        Assert.Equal(priority.Id, BoardLanes.DefaultColumn(ViewFakes.Sheet(false, priority))?.Id);
    }

    [Fact]
    public async Task Dropping_a_card_on_another_lane_updates_the_item_with_all_its_values()
    {
        var status = ViewFakes.StatusColumn();
        var owner = new ColumnDto(Guid.NewGuid(), "Owner", ColumnKind.Person, 1, [], null);
        var item = ViewFakes.Item("Write plan", status, "Not started") with
        {
            Start = new DateOnly(2026, 10, 1),
            Values = new Dictionary<string, string?> { [status.Id.ToString()] = "Not started", [owner.Id.ToString()] = "pm@example.com" },
        };
        var items = new RecordingItemService();
        var changed = 0;
        await using var ctx = ViewFakes.CreateContext(items);

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status, owner))
            .Add(v => v.Items, [item])
            .Add(v => v.Changed, EventCallback.Factory.Create(this, () => changed++)));

        await Lane(board, "Not started").QuerySelector(".mud-drop-item")!.TriggerEventAsync("ondragstart", new Microsoft.AspNetCore.Components.Web.DragEventArgs());
        await Lane(board, "Done").QuerySelector(".mud-drop-zone")!.TriggerEventAsync("ondrop", new Microsoft.AspNetCore.Components.Web.DragEventArgs());

        board.WaitForAssertion(() => Assert.Single(items.Updates));
        var (itemId, request) = items.Updates[0];
        Assert.Equal(item.Id, itemId);
        Assert.Equal("Write plan", request.Title);
        Assert.Equal(item.Start, request.Start);
        Assert.Equal("Done", request.Values[status.Id.ToString()]);
        Assert.Equal("pm@example.com", request.Values[owner.Id.ToString()]);
        Assert.Equal(1, changed);
        Assert.Equal(["Write plan"], CardTitles(Lane(board, "Done")));
    }

    [Fact]
    public async Task Leaf_toggle_hides_parent_items()
    {
        var status = ViewFakes.StatusColumn();
        var parent = ViewFakes.Item("Phase 1", status, "In progress");
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status))
            .Add(v => v.Items, [parent, ViewFakes.Item("Task", status, "In progress", parentId: parent.Id)]));

        Assert.Equal(["Phase 1", "Task"], CardTitles(Lane(board, "In progress")));

        board.Find("input[type=checkbox]").Change(true);

        Assert.Equal(["Task"], CardTitles(Lane(board, "In progress")));
    }

    [Fact]
    public async Task Card_shows_initials_wbs_and_opens_the_item()
    {
        var status = ViewFakes.StatusColumn();
        var owner = new ColumnDto(Guid.NewGuid(), "Owner", ColumnKind.Person, 1, [], null);
        var item = ViewFakes.Item("Write plan") with
        {
            CostpointWbsId = "1001.01.02",
            Finish = new DateOnly(2020, 1, 1),
            Values = new Dictionary<string, string?> { [owner.Id.ToString()] = "jane.doe@example.com" },
        };
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService());

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status, owner))
            .Add(v => v.Items, [item]));

        var card = board.Find(".board-card");
        Assert.Contains("JD", card.TextContent);
        Assert.Contains("1001.01.02", card.TextContent);
        Assert.Contains("mud-chip-color-error", card.QuerySelector(".board-card-finish")!.ClassName);
        A11yAssert.Check(board);

        card.Click();

        Assert.EndsWith($"?item={item.Id}", ctx.Services.GetRequiredService<NavigationManager>().Uri); // details open on the page showing the sheet
    }

    [Fact]
    public async Task Quick_add_creates_an_item_in_that_lane()
    {
        var status = ViewFakes.StatusColumn();
        var items = new RecordingItemService();
        await using var ctx = ViewFakes.CreateContext(items);

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false, status))
            .Add(v => v.Items, []));

        Lane(board, "Done").QuerySelector("button[aria-label='Add item to Done']")!.Click();
        var input = board.Find("input[aria-label='New item title in Done']");
        input.Input("Ship it");
        input.KeyDown("Enter");

        board.WaitForAssertion(() => Assert.Single(items.Creates));
        Assert.Equal("Ship it", items.Creates[0].Title);
        Assert.Equal("Done", items.Creates[0].Values![status.Id.ToString()]);
    }

    [Fact]
    public async Task Sheet_without_a_dropdown_offers_to_create_a_status_column()
    {
        var columns = new RecordingColumnService();
        await using var ctx = ViewFakes.CreateContext(new RecordingItemService(), columns);

        var board = ctx.Render<SheetBoardView>(p => p
            .Add(v => v.Sheet, ViewFakes.Sheet(false))
            .Add(v => v.Items, [ViewFakes.Item("Anything")]));

        board.FindAll("button").Single(b => b.TextContent.Contains("Add Status column")).Click();

        board.WaitForAssertion(() => Assert.Single(columns.Creates));
        Assert.Equal("Status", columns.Creates[0].Name);
        Assert.Equal(ColumnKind.Dropdown, columns.Creates[0].Type);
        Assert.Equal(["Not started", "In progress", "Blocked", "Done"], columns.Creates[0].Options);
    }

    private static IEnumerable<string> LaneNames(IRenderedComponent<SheetBoardView> board) =>
        board.FindAll("[data-lane]").Select(l => l.GetAttribute("data-lane")!);

    private static IElement Lane(IRenderedComponent<SheetBoardView> board, string name) =>
        board.FindAll("[data-lane]").Single(l => l.GetAttribute("data-lane") == name);

    private static IEnumerable<string> CardTitles(IElement lane) =>
        lane.QuerySelectorAll(".board-card-title").Select(t => t.TextContent.Trim());
}

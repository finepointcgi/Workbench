using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Application.MyWork;
using Workbench.Contracts.MyWork;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Board;
using Workbench.Ui.Home;
using static Workbench.Tests.MyWork.MyWorkCalculatorTests;

namespace Workbench.Tests.MyWork;

public sealed class MyWorkPageTests
{
    private static readonly DateOnly Today = new(2026, 10, 14);
    private static readonly ColumnDto Status = new(Guid.NewGuid(), "Status", ColumnKind.Dropdown, 0, ["Open", "Done"], null);
    private static readonly ColumnDto Owner = new(Guid.NewGuid(), "Owner", ColumnKind.Person, 1, [], null);

    private sealed class FakeMyWork(MyWorkDto work) : IMyWorkService
    {
        public Task<MyWorkDto> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(work);
    }

    [Fact]
    public async Task Sheet_items_show_groups_and_save_status_inline()
    {
        var sheet = Sheet(true, Status, Owner);
        var me = (Owner, (string?)"pm@example.com");
        ItemDto[] items =
        [
            Item("Late report", new DateOnly(2026, 10, 10), me, (Status, "Open")),
            Item("Friday review", new DateOnly(2026, 10, 16), me),
            Item("Shipped", new DateOnly(2026, 10, 13), me, (Status, "Done")),
        ];
        var work = MyWorkCalculator.Summarize("pm@example.com", Today, MyWorkCalculator.ItemsFor(sheet, items, "pm@example.com", Today));

        var itemService = new RecordingItemService();
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IMyWorkService>(new FakeMyWork(work));
        ctx.Services.AddSingleton<IItemService>(itemService);
        var popovers = ctx.Render<MudBlazor.MudPopoverProvider>();

        var page = ctx.Render<MySheetItems>(p => p.Add(x => x.InitiallyExpanded, true));

        page.WaitForAssertion(() => Assert.Contains("Late report", page.Markup));
        Assert.Equal(["Overdue", "DueThisWeek"], page.FindAll("[data-bucket]").Select(e => e.GetAttribute("data-bucket")));
        Assert.DoesNotContain("Shipped", page.Markup); // done items are hidden until "Show done"
        Assert.Contains("2 open", page.Markup);
        Assert.Contains($"sheets/{sheet.Summary.Id}?item={items[0].Id}", page.Markup);

        page.Find($"tr[data-item='{items[0].Id}'] .mud-select .mud-input-control").MouseDown();
        popovers.WaitForAssertion(() => Assert.Contains("Done", popovers.Markup));
        popovers.FindAll(".mud-list-item").Single(e => e.TextContent.Trim() == "Done").Click();

        page.WaitForAssertion(() => Assert.Single(itemService.Updates));
        var (itemId, request) = itemService.Updates[0];
        Assert.Equal(items[0].Id, itemId);
        Assert.Equal("Done", request.Values[Status.Id.ToString()]);
        Assert.Equal("pm@example.com", request.Values[Owner.Id.ToString()]); // full values are sent
    }
}

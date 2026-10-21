using Workbench.Application.Customization;
using Workbench.Contracts;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Customization;

public sealed class HomePageTests
{
    private static SaveWidgetRequest Kpi(string title, KpiKind kind, bool pinned = false) =>
        new(title, WidgetType.Kpi, 3, WidgetHeight.Small, new WidgetConfig(Kpi: kind, Pinned: pinned));

    [Fact]
    public async Task Each_role_gets_its_home_page_and_everyone_else_the_built_in_one()
    {
        await using var host = await SeededHost.CreateAsync();
        IHomePageService Admin() => host.As(SeededHost.Admin).Host.Get<IHomePageService>();
        var created = await Admin().CreateRoleHomePageAsync("Finance");
        Assert.Equal(HomePageService.BuiltInWidgets.Select(w => w.Type), created.Widgets.Select(w => w.Widget.Type)); // starts from the built-in page
        await Admin().AddWidgetAsync("Finance", Kpi("AR aging", KpiKind.ArAging));

        var forFinance = await host.As(SeededHost.Finance).Host.Get<IHomePageService>().GetMineAsync();
        Assert.Equal("Finance", forFinance.RoleKey);
        Assert.Equal("AR aging", forFinance.Widgets[^1].Widget.Title);
        Assert.False(forFinance.CanManage);

        var forViewer = await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetMineAsync();
        Assert.True(forViewer.IsBuiltIn);
        Assert.Equal(["To do", "This week", "Waiting on you", "My sheet items"], forViewer.Widgets.Select(w => w.Widget.Title));

        // Everyone's home page is the fallback for roles without one.
        await Admin().CreateRoleHomePageAsync(CustomizationRoles.Everyone);
        await Admin().AddWidgetAsync(CustomizationRoles.Everyone, new SaveWidgetRequest("Welcome", WidgetType.Announcement, 12, WidgetHeight.Small, new WidgetConfig(Markdown: "Hi")));
        Assert.Equal(CustomizationRoles.Everyone, (await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetMineAsync()).RoleKey);
        Assert.Equal("Finance", (await host.As(SeededHost.Finance).Host.Get<IHomePageService>().GetMineAsync()).RoleKey);
        Assert.Equal(CustomizationRoles.Everyone, (await Admin().PreviewAsync("Hr")).RoleKey);

        // Role home pages aren't listed with ordinary dashboards.
        Assert.Empty(await host.As(SeededHost.Admin).Host.Get<IDashboardService>().ListAsync());
        Assert.Equal(["Everyone", "Finance"], (await Admin().ListRoleHomePagesAsync()).Select(h => h.RoleKey == CustomizationRoles.Everyone ? "Everyone" : h.RoleKey));
    }

    [Fact]
    public async Task People_rearrange_and_hide_widgets_within_what_their_role_allows()
    {
        await using var host = await SeededHost.CreateAsync();
        IHomePageService Admin() => host.As(SeededHost.Admin).Host.Get<IHomePageService>();
        await Admin().CreateRoleHomePageAsync("Finance");
        var pinned = await Admin().AddWidgetAsync("Finance", Kpi("Funding", KpiKind.FundingRemaining, pinned: true));

        IHomePageService Finance() => host.As(SeededHost.Finance).Host.Get<IHomePageService>();
        var mine = await Finance().GetMineAsync();
        var ids = mine.Widgets.Select(w => w.Widget.Id).Reverse().ToList();
        var todo = mine.Widgets.First(w => w.Widget.Type == WidgetType.MyTodo).Widget.Id;

        var saved = await Finance().SavePreferencesAsync(new SaveHomePreferencesRequest(ids, [todo]));
        Assert.True(saved.Personalized);
        Assert.Equal(ids, saved.Widgets.Select(w => w.Widget.Id));
        Assert.True(saved.Widgets.Single(w => w.Widget.Id == todo).Hidden);

        var hidePinned = await Assert.ThrowsAsync<ArgumentException>(() => Finance().SavePreferencesAsync(new SaveHomePreferencesRequest(ids, [pinned.Id])));
        Assert.Contains("pinned", hidePinned.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => Finance().SavePreferencesAsync(new SaveHomePreferencesRequest(ids.Skip(1).ToList(), [])));

        // A widget added later shows up after the person's own order; their changes don't touch anyone else's page.
        await Admin().AddWidgetAsync("Finance", Kpi("Pipeline", KpiKind.Pipeline));
        var after = await host.As(SeededHost.Finance).Host.Get<IHomePageService>().GetMineAsync();
        Assert.Equal("Pipeline", after.Widgets[^1].Widget.Title);
        Assert.Equal(ids, after.Widgets.Take(ids.Count).Select(w => w.Widget.Id));

        var reset = await host.As(SeededHost.Finance).Host.Get<IHomePageService>().ResetPreferencesAsync();
        Assert.False(reset.Personalized);
        Assert.Equal(WidgetType.MyTodo, reset.Widgets[0].Widget.Type);

        // The built-in page can be personalized too.
        var builtIn = await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetMineAsync();
        var hidden = builtIn.Widgets.Single(w => w.Widget.Type == WidgetType.MySheetItems).Widget.Id;
        var personal = await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().SavePreferencesAsync(
            new SaveHomePreferencesRequest(builtIn.Widgets.Select(w => w.Widget.Id).ToList(), [hidden]));
        Assert.True(personal.IsBuiltIn && personal.Personalized);
    }

    [Fact]
    public async Task Only_those_who_manage_layouts_design_role_home_pages()
    {
        await using var host = await SeededHost.CreateAsync();
        IHomePageService Finance() => host.As(SeededHost.Finance).Host.Get<IHomePageService>();
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Finance().CreateRoleHomePageAsync("Finance"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Finance().ListRoleHomePagesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => Finance().AddWidgetAsync("Finance", Kpi("x", KpiKind.Pipeline)));

        IHomePageService Admin() => host.As(SeededHost.Admin).Host.Get<IHomePageService>();
        await Assert.ThrowsAsync<ArgumentException>(() => Admin().CreateRoleHomePageAsync("NoSuchRole"));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => Admin().AddWidgetAsync("Finance", Kpi("x", KpiKind.Pipeline))); // no page yet
        await Admin().CreateRoleHomePageAsync("Finance");
        await Assert.ThrowsAsync<ArgumentException>(() => Admin().AddWidgetAsync("Finance", new SaveWidgetRequest("Kpi", WidgetType.Kpi, 3, WidgetHeight.Small, new WidgetConfig())));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin().CreateRoleHomePageAsync("Finance"));
        await Assert.ThrowsAsync<ArgumentException>(() => Admin().AddWidgetAsync("Finance",
            new SaveWidgetRequest("Links", WidgetType.Shortcuts, 6, WidgetHeight.Small, new WidgetConfig(Shortcuts: [new WidgetShortcut("Evil", "https://example.com")]))));

        await Admin().DeleteRoleHomePageAsync("Finance");
        Assert.True((await host.As(SeededHost.Finance).Host.Get<IHomePageService>().GetMineAsync()).IsBuiltIn);
    }

    [Fact]
    public async Task Kpi_tiles_show_only_what_the_viewer_may_see()
    {
        await using var host = await SeededHost.CreateAsync();
        IHomePageService Admin() => host.As(SeededHost.Admin).Host.Get<IHomePageService>();
        await Admin().CreateRoleHomePageAsync(CustomizationRoles.Everyone);
        var ar = await Admin().AddWidgetAsync(CustomizationRoles.Everyone, Kpi("AR aging", KpiKind.ArAging));
        var note = await Admin().AddWidgetAsync(CustomizationRoles.Everyone, new SaveWidgetRequest("Note", WidgetType.Announcement, 12, WidgetHeight.Small, new WidgetConfig(Markdown: "Close **Friday**")));

        var forViewer = await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetWidgetDataAsync(ar.Id);
        Assert.NotNull(forViewer.Error);
        Assert.Null(forViewer.Kpi);

        var forFinance = await host.As(SeededHost.Finance).Host.Get<IHomePageService>().GetWidgetDataAsync(ar.Id);
        Assert.Null(forFinance.Error);
        Assert.Equal(KpiKind.ArAging, forFinance.Kpi!.Kind);
        Assert.Equal("Close **Friday**", (await host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetWidgetDataAsync(note.Id)).Markdown);

        // A widget that isn't on your home page isn't yours to read.
        await Admin().CreateRoleHomePageAsync("Finance");
        var financeOnly = await Admin().AddWidgetAsync("Finance", Kpi("Pipeline", KpiKind.Pipeline));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.As(SeededHost.Viewer).Host.Get<IHomePageService>().GetWidgetDataAsync(financeOnly.Id));
    }
}

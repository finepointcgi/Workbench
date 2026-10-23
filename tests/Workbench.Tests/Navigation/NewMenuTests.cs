using System.Reflection;
using Workbench.Tests.Accessibility;
using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Navigation;

/// <summary>The app bar's "+ New" menu: what each role can start, and how each item opens.</summary>
public sealed class NewMenuTests
{
    private static readonly string[] Everyday = ["time", "expense-report", "receipt", "travel", "leave", "requisition", "sheet"];

    private static NavAccess Access(OrgRoles roles) => new(new CurrentUserInfo("a@example.com", "A", roles, null));

    private static async Task<(IRenderedComponent<MudPopoverProvider> Popovers, IRenderedComponent<NewMenu> Menu)> OpenAsync(BunitContext ctx, OrgRoles roles)
    {
        var popovers = ctx.Render<MudPopoverProvider>();
        await popovers.InvokeAsync(() => { });
        var menu = ctx.Render<NewMenu>(p => p.Add(m => m.Access, Access(roles)));
        menu.Find("[data-testid='new-button']").Click();
        popovers.WaitForAssertion(() => Assert.NotEmpty(popovers.FindAll("[data-testid='new-time']")));
        return (popovers, menu);
    }

    private static List<string> Items(IRenderedComponent<MudPopoverProvider> popovers) =>
        popovers.FindAll("[data-testid^='new-']").Select(e => e.GetAttribute("data-testid")!["new-".Length..]).ToList();

    public static TheoryData<OrgRoles, string[], string[]> Roles => new()
    {
        { OrgRoles.Employee, [], ["project", "opportunity", "invoice", "journal", "bill", "contract", "rfq", "engineering-change", "automation"] },
        { OrgRoles.ProjectManager, ["project"], ["opportunity", "invoice", "journal", "bill", "contract", "automation"] },
        { OrgRoles.Finance, ["project", "invoice", "journal", "bill", "contract", "rfq"], ["opportunity", "engineering-change", "automation"] },
        { OrgRoles.Hr, [], ["project", "invoice", "journal", "bill", "contract", "automation"] },
        { OrgRoles.Materials, ["engineering-change"], ["project", "invoice", "journal", "bill", "automation"] },
        { OrgRoles.BusinessDevelopment, ["opportunity"], ["project", "invoice", "journal", "bill", "automation"] },
        { OrgRoles.Admin, ["project", "opportunity", "invoice", "journal", "bill", "contract", "rfq", "engineering-change", "automation"], [] },
    };

    [Theory]
    [MemberData(nameof(Roles))]
    public async Task Each_role_sees_its_everyday_paperwork_first_then_only_what_it_may_create(OrgRoles roles, string[] extra, string[] hidden)
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(roles));
        var (popovers, _) = await OpenAsync(ctx, roles);

        var items = Items(popovers);
        Assert.Equal(Everyday.Take(6), items.Take(6)); // their own paperwork, in task order
        Assert.All(Everyday.Concat(extra), key => Assert.Contains(key, items));
        Assert.All(hidden, key => Assert.DoesNotContain(key, items));
        Assert.Equal(NewCatalog.For(Access(roles)).Select(a => a.Key), items);
        Assert.Single(popovers.FindAll(".mud-divider")); // between their own paperwork and the team's records
        A11yAssert.Check(popovers);
    }

    [Fact]
    public async Task Picking_a_page_item_opens_its_create_page()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Finance));
        var (popovers, _) = await OpenAsync(ctx, OrgRoles.Finance);

        popovers.Find("[data-testid='new-invoice']").Click();

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        popovers.WaitForAssertion(() => Assert.EndsWith("/billing/new", navigation.Uri));
    }

    [Fact]
    public async Task The_n_shortcut_opens_the_menu_and_ctrl_k_items_launch_through_it()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        var popovers = ctx.Render<MudPopoverProvider>();
        await popovers.InvokeAsync(() => { });
        var menu = ctx.Render<NewMenu>(p => p.Add(m => m.Access, Access(OrgRoles.Employee)));

        Assert.Contains(ctx.JSInterop.Invocations, i => i.Identifier == "registerNewShortcut");
        Assert.Equal("N", menu.Find("[data-testid='new-button']").GetAttribute("aria-keyshortcuts"));
        await menu.InvokeAsync(menu.Instance.OpenFromShortcut);
        popovers.WaitForAssertion(() => Assert.NotEmpty(popovers.FindAll("[data-testid='new-leave']")));

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        await menu.InvokeAsync(() => menu.Instance.LaunchAsync("requisition"));
        Assert.EndsWith("/purchasing/requisitions/new", navigation.Uri);

        await menu.InvokeAsync(() => menu.Instance.LaunchAsync("invoice")); // not theirs: nothing happens
        Assert.EndsWith("/purchasing/requisitions/new", navigation.Uri);
    }

    [Fact]
    public async Task Upload_a_receipt_opens_its_dialog_in_place()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.ProjectManager));
        ctx.Services.AddSingleton(Stub<Workbench.Contracts.Receipts.IReceiptLibraryService>.Create());
        var dialogs = ctx.Render<MudDialogProvider>();
        var (popovers, _) = await OpenAsync(ctx, OrgRoles.ProjectManager);

        popovers.Find("[data-testid='new-receipt']").Click();
        dialogs.WaitForAssertion(() => Assert.NotEmpty(dialogs.FindAll("[data-testid='upload-receipt-pick']")));
        popovers.WaitForAssertion(() => Assert.Empty(popovers.FindAll("[data-testid='new-time']"))); // the menu closes, not left open behind the dialog
        Assert.EndsWith("/", ctx.Services.GetRequiredService<NavigationManager>().Uri); // still where they were
    }

    [Fact]
    public void Nobody_signed_in_gets_no_menu()
    {
        Assert.Empty(NewCatalog.For(NavAccess.Anonymous));
        using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        var menu = ctx.Render<NewMenu>(p => p.Add(m => m.Access, NavAccess.Anonymous));
        Assert.Empty(menu.FindAll("[data-testid='new-button']"));
    }

    [Fact]
    public void Every_item_opens_a_real_page()
    {
        var routes = typeof(NewMenu).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(r => r.Template.TrimStart('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(NewCatalog.Actions, a => Assert.True(routes.Contains(a.Href), $"{a.Title}: no page at {a.Href}"));
        Assert.Equal(NewCatalog.Actions.Count, NewCatalog.Actions.Select(a => a.Key).Distinct().Count());
    }

    [Fact]
    public void Ctrl_k_lists_the_new_items_the_user_may_start()
    {
        var finance = PaletteSearch.Pages(Access(OrgRoles.Finance)).Concat(PaletteSearch.Creates(Access(OrgRoles.Finance))).ToList();
        var top = PaletteSearch.Search("new invoice", finance)[0];
        Assert.Equal(PaletteKind.Create, top.Kind);
        Assert.Equal("invoice", top.Action);
        Assert.DoesNotContain(finance, i => i.Href == "billing/new"); // listed once, as its "New …" item
        Assert.Contains(PaletteSearch.Search("leave", finance), i => i.Action == "leave"); // alongside the Leave page
        Assert.Contains(PaletteSearch.Search("leave", finance), i => i.Href == "leave");

        var employee = PaletteSearch.Creates(Access(OrgRoles.Employee)).ToList();
        Assert.Contains(employee, i => i.Title == "New expense report");
        Assert.DoesNotContain(employee, i => i.Action == "invoice" || i.Action == "journal");
    }

    [Fact]
    public async Task Picking_a_new_item_in_ctrl_k_hands_it_to_the_menu_instead_of_navigating()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        var provider = ctx.Render<MudDialogProvider>();
        var parameters = new DialogParameters<CommandPaletteDialog> { { d => d.Access, Access(OrgRoles.Employee) } };
        IDialogReference? dialog = null;
        await provider.InvokeAsync(async () => dialog = await ctx.Services.GetRequiredService<IDialogService>().ShowAsync<CommandPaletteDialog>("Search", parameters));

        provider.Find("input").Input("new expense");
        provider.WaitForAssertion(() => Assert.Equal("new/expense-report", provider.FindAll("[data-testid='palette-result']")[0].GetAttribute("data-href")));
        provider.Find("input").KeyDown("Enter");

        var result = await dialog!.Result;
        Assert.Equal("expense-report", Assert.IsType<PaletteItem>(result!.Data).Action);
        Assert.EndsWith("/", ctx.Services.GetRequiredService<NavigationManager>().Uri);
    }
}

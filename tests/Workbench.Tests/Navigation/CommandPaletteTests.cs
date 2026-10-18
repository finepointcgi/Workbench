using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using Workbench.Contracts.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Navigation;

public sealed class CommandPaletteTests
{
    private static NavAccess Access(OrgRoles roles) => new(new CurrentUserInfo("a@example.com", "A", roles, null));

    [Fact]
    public void Employees_only_find_pages_they_may_open()
    {
        var pages = PaletteSearch.Pages(Access(OrgRoles.Employee)).Select(p => p.Href).ToList();

        Assert.Contains("time", pages);
        Assert.Contains("notifications", pages);
        Assert.DoesNotContain("accounting/journal", pages);
        Assert.DoesNotContain("admin/users", pages);
        Assert.DoesNotContain("billing/statements", pages); // a breadcrumb parent, not a page
    }

    [Fact]
    public void Search_ranks_title_matches_first_and_needs_every_word()
    {
        var items = PaletteSearch.Pages(Access(OrgRoles.Finance)).ToList();

        var bills = PaletteSearch.Search("bills", items);
        Assert.Equal("purchasing/bills", bills[0].Href);

        // Keywords find pages by what people call them.
        Assert.Contains(PaletteSearch.Search("balance sheet", items), i => i.Href == "accounting/reports");
        Assert.Empty(PaletteSearch.Search("bills zzz", items));
    }

    [Fact]
    public void Blank_search_lists_recent_items_first()
    {
        var recent = new PaletteItem(PaletteKind.Recent, "Alpha plan", "sheets/1", PaletteSearch.IconFor(PaletteKind.Recent));
        var results = PaletteSearch.Search("", [recent, .. PaletteSearch.Pages(Access(OrgRoles.Employee))]);

        Assert.Equal(recent, results[0]);
        Assert.True(results.Count <= PaletteSearch.MaxResults);
    }

    [Fact]
    public async Task Palette_finds_projects_and_sheets_by_name_and_opens_with_enter()
    {
        var project = NavTestKit.Project("1001", "Apollo modernization");
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee, Projects: [project], Sheets: [NavTestKit.Sheet("Apollo risks")]));
        var provider = ctx.Render<MudDialogProvider>();
        var dialogs = ctx.Services.GetRequiredService<IDialogService>();
        var parameters = new DialogParameters<CommandPaletteDialog> { { d => d.Access, Access(OrgRoles.Employee) } };
        await provider.InvokeAsync(() => dialogs.ShowAsync<CommandPaletteDialog>("Search", parameters));

        provider.WaitForAssertion(() => Assert.NotEmpty(provider.FindAll("[data-testid='palette-result']")));
        provider.Find("input").Input("apollo");

        provider.WaitForAssertion(() =>
        {
            var hrefs = provider.FindAll("[data-testid='palette-result']").Select(r => r.GetAttribute("data-href")).ToList();
            Assert.Equal(2, hrefs.Count);
            Assert.Contains($"projects/{project.Id}", hrefs);
            Assert.Contains(hrefs, h => h!.StartsWith("sheets/"));
        });

        provider.Find("input").Input("apollo modern");
        provider.WaitForAssertion(() => Assert.Single(provider.FindAll("[data-testid='palette-result']")));
        provider.Find("input").KeyDown("Enter");

        var navigation = ctx.Services.GetRequiredService<NavigationManager>();
        provider.WaitForAssertion(() => Assert.EndsWith($"projects/{project.Id}", navigation.Uri));
    }

    [Fact]
    public async Task Arrow_keys_move_the_selection()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        var provider = ctx.Render<MudDialogProvider>();
        var parameters = new DialogParameters<CommandPaletteDialog> { { d => d.Access, Access(OrgRoles.Employee) } };
        await provider.InvokeAsync(() => ctx.Services.GetRequiredService<IDialogService>().ShowAsync<CommandPaletteDialog>("Search", parameters));
        provider.WaitForAssertion(() => Assert.True(provider.FindAll("[data-testid='palette-result']").Count > 2));

        provider.Find("input").KeyDown("ArrowDown");

        provider.WaitForAssertion(() =>
            Assert.Equal("true", provider.FindAll("[data-testid='palette-result']")[1].GetAttribute("aria-selected")));
    }
}

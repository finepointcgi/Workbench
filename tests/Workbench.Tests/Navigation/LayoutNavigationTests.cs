using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Identity;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Ui.Layout;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Navigation;

/// <summary>Breadcrumbs, the phone redirect and the user's name in the header.</summary>
public sealed class LayoutNavigationTests
{
    private static NavAccess Access(OrgRoles roles) => new(new CurrentUserInfo("a@example.com", "A", roles, null));

    [Fact]
    public void Breadcrumbs_show_where_a_detail_page_sits()
    {
        var crumbs = PageBreadcrumbs.Build($"purchasing/bills/{Guid.NewGuid()}/edit", Access(OrgRoles.Finance));

        Assert.Equal(["Finance", "Payables & purchasing", "Bills", "Edit bill"], crumbs.Select(c => c.Text));
        Assert.Equal("purchasing/bills", crumbs[2].Href);
        Assert.True(crumbs[^1].Disabled);
    }

    [Theory]
    [InlineData("")]
    [InlineData("time")]
    [InlineData("sheets")]
    [InlineData("expenses/0f8fad5b-d9cb-469f-a165-70867728950e")] // draws its own
    [InlineData("m/today")]
    public void Top_level_pages_have_no_breadcrumbs(string path) =>
        Assert.Empty(PageBreadcrumbs.Build(path, Access(OrgRoles.Admin)));

    [Fact]
    public void Breadcrumbs_name_new_records_and_nested_finance_pages()
    {
        Assert.Equal(["Finance", "Accounting", "Journal entries", "New journal entry"],
            PageBreadcrumbs.Build("accounting/journal/new", Access(OrgRoles.Finance)).Select(c => c.Text));
        Assert.Equal(["Setup", "Setup", "Contracts"],
            PageBreadcrumbs.Build("setup/contracts", Access(OrgRoles.Finance)).Select(c => c.Text));
        Assert.Equal(["Sheets & dashboards", "Sheets", "Sheet"],
            PageBreadcrumbs.Build($"sheets/{Guid.NewGuid()}?item=1", Access(OrgRoles.Employee)).Select(c => c.Text));
    }

    [Theory]
    [InlineData("", null, true, true)]
    [InlineData("", "mobile", true, true)]
    [InlineData("", "full", true, false)] // this device chose the full site
    [InlineData("", null, false, false)] // a desktop browser
    [InlineData("time", null, true, false)] // only the home page redirects
    public void Phones_opening_home_go_to_the_mobile_app(string path, string? preference, bool small, bool redirect) =>
        Assert.Equal(redirect, MobileRedirect.ShouldRedirect(path, preference, small));

    [Fact]
    public async Task Small_screen_redirects_home_to_m_and_full_site_choice_is_remembered()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        ctx.JSInterop.Mode = JSRuntimeMode.Strict;
        ctx.Renderer.SetRendererInfo(new RendererInfo("Server", isInteractive: true));
        var module = ctx.JSInterop.SetupModule("./_content/Workbench.Ui/navigation.js");
        module.Setup<string?>("getSitePreference").SetResult(null);
        module.Setup<bool>("isSmallScreen").SetResult(true);
        module.SetupVoid("setSitePreference", "full").SetVoidResult();
        var navigation = ctx.Services.GetRequiredService<NavigationManager>();

        ctx.Render<MobileRedirect>();
        Assert.EndsWith("/m", navigation.Uri);

        // The phone layout's "Full site" link.
        navigation.NavigateTo("?site=full");
        ctx.Render<MobileRedirect>();
        Assert.Single(module.VerifyInvoke("setSitePreference").Arguments, "full");
        Assert.DoesNotContain("/m", new Uri(navigation.Uri).AbsolutePath);
    }

    [Fact]
    public async Task Header_shows_the_display_name_not_the_email()
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Employee));
        ctx.Services.AddSingleton(Stub<IUserDirectory>.Create(("GetMeAsync", _ =>
            Task.FromResult(new UserDto("pm@example.com", "pm", OrgRoles.Finance, DateTime.UtcNow, null)))));
        ctx.Services.AddSingleton(Stub<ISignOutService>.Create());
        ctx.Services.AddSingleton(Stub<IPeopleService>.Create(("GetCurrentEmployeeAsync", _ => Task.FromResult<EmployeeDto?>(
            new EmployeeDto(Guid.NewGuid(), "E1001", "pm@example.com", "Pat Morgan", null, null, null, new DateOnly(2020, 1, 1), null, 40, true)))));

        var menu = ctx.Render<UserMenu>();

        menu.WaitForAssertion(() => Assert.Equal("Pat Morgan", menu.Find("[data-testid='user-name']").TextContent.Trim()));
        Assert.DoesNotContain("pm@example.com", menu.Find("[data-testid='user-menu']").TextContent);
    }
}

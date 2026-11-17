using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Company;
using Workbench.Contracts.Identity;
using Workbench.Ui.Layout;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Navigation;

/// <summary>Federal-only pages show for government contractors and are hidden for commercial companies unless an admin turns them on.</summary>
public sealed class GovernmentNavigationTests
{
    private static readonly string[] FederalLinks =
        ["admin/dcaa-readiness", "admin/cpsr-readiness", "admin/cmmc", "admin/cas-disclosure", "accounting/incurred-cost", "bd/leads"];

    private static NavAccess Admin(bool hideGovernment)
    {
        var info = new CurrentUserInfo("admin@example.com", "Admin", OrgRoles.Admin, null);
        return new NavAccess(info, HideGovernment: hideGovernment);
    }

    [Fact]
    public void Government_contractors_see_every_federal_page_in_the_menu_and_palette()
    {
        var menu = NavCatalog.MenuEntries(Admin(false)).Select(e => e.Href).ToList();
        var palette = NavCatalog.PaletteEntries(Admin(false)).Select(e => e.Href).ToList();

        foreach (var link in FederalLinks)
        {
            Assert.Contains(link, palette);
        }

        Assert.Contains("admin/dcaa-readiness", menu);
        Assert.Contains("admin/cmmc", menu);
    }

    [Fact]
    public void Commercial_companies_do_not_see_them_even_as_admin()
    {
        var menu = NavCatalog.MenuEntries(Admin(true)).Select(e => e.Href).ToList();
        var palette = NavCatalog.PaletteEntries(Admin(true)).Select(e => e.Href).ToList();

        foreach (var link in FederalLinks)
        {
            Assert.DoesNotContain(link, menu);
            Assert.DoesNotContain(link, palette);
        }

        Assert.Contains("setup/contracts", menu); // Agreements are still managed; only the federal pages go.
    }

    [Theory]
    [InlineData(CompanyType.GovernmentContractor, true, true)]
    [InlineData(CompanyType.ProfessionalServices, false, false)]
    [InlineData(CompanyType.ProfessionalServices, true, true)] // An admin turned them on.
    public async Task The_menu_follows_the_organizations_setting(CompanyType type, bool show, bool expectFederal)
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Admin));
        ctx.Services.AddSingleton(Stub<ICompanyProfileService>.Create(("GetAsync", _ =>
            Task.FromResult(new CompanyProfileDto(type, show, false, true, true)))));
        var nav = ctx.Render<NavMenu>();
        await Task.Yield();

        nav.WaitForAssertion(() => Assert.NotEmpty(nav.FindAll("a[href='time']")));
        var html = nav.Markup;
        Assert.Equal(expectFederal, html.Contains("admin/dcaa-readiness") || html.Contains("DCAA readiness"));
    }

    [Theory]
    [InlineData(CompanyType.GovernmentContractor, "Contracts")]
    [InlineData(CompanyType.ProfessionalServices, "Agreements")]
    public async Task The_page_tabs_use_the_companys_words_for_contracts(CompanyType type, string label)
    {
        await using var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Admin));
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("setup/contracts");
        var admin = new NavAccess(new CurrentUserInfo("admin@example.com", "Admin", OrgRoles.Admin, null), HideGovernment: type != CompanyType.GovernmentContractor);
        var tabs = ctx.Render<GroupTabs>(p => p.Add(t => t.Access, admin).AddCascadingValue<ITerms>(CompanyTerms.For(type)));

        tabs.WaitForAssertion(() => Assert.Contains(tabs.FindAll("a.wb-group-tab"), a => a.TextContent.Trim() == label));
    }
}

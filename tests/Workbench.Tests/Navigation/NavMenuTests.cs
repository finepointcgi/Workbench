using Workbench.Tests.Accessibility;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Identity;
using Workbench.Ui.Layout;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Navigation;

/// <summary>The menu is shaped by role: a few sections, each showing only what the user can use.</summary>
public sealed class NavMenuTests
{
    private static readonly string[] EmployeeLinks = ["", "time", "expenses", "receipts", "travel", "leave", "purchasing/requisitions", "projects", "hr/me", "hr/benefits/me", "hr", "hr/org-chart", "sheets", "dashboards", "reports", "reports/writer"];

    // Approval pages are reached from the one inbox; none of them is a menu link any more.
    private static readonly string[] OldApprovalLinks = ["time/approvals", "odcs/approvals", "purchasing/approvals"];

    private static async Task<(IRenderedComponent<NavMenu> Nav, BunitContext Ctx)> RenderAsync(NavUser user)
    {
        var ctx = NavTestKit.NewContext(user);
        var nav = ctx.Render<NavMenu>();
        await Task.Yield();
        nav.WaitForAssertion(() => Assert.NotEmpty(nav.FindAll("a[href='time']")), TimeSpan.FromSeconds(5));
        return (nav, ctx);
    }

    // Every page the menu reaches: plain links, and each group link's pages (its tabs).
    private static List<string> Links(IRenderedComponent<NavMenu> nav)
    {
        var groups = nav.FindAll("[data-pages]").ToList();
        var plain = nav.FindAll("a[href]").Where(a => !groups.Any(g => g == a || g.Contains(a))).Select(a => a.GetAttribute("href")!);
        return [.. plain, .. groups.SelectMany(g => g.GetAttribute("data-pages")!.Split(' '))];
    }

    // What the user actually sees in the menu: one link per page or group.
    private static int MenuLinkCount(IRenderedComponent<NavMenu> nav) => nav.FindAll("a[href]").Count;

    private static List<string> Sections(IRenderedComponent<NavMenu> nav) =>
        nav.FindAll("[data-testid^='nav-']").Where(e => !e.GetAttribute("data-testid")!.StartsWith("nav-group-") && e.GetAttribute("data-testid") != "nav-menu")
            .Select(e => e.GetAttribute("data-testid")!["nav-".Length..]).ToList();

    [Fact]
    public async Task Employee_sees_a_short_menu_of_their_own_work()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Employee));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Equal(EmployeeLinks.Order(), links.Order());
        Assert.Equal(["home", "my-work", "projects", "people", "sheets", "reports"], Sections(nav));
        Assert.Empty(nav.FindAll("[data-testid='nav-approvals']"));
        A11yAssert.Check(nav);
    }

    [Fact]
    public async Task The_materials_role_gets_the_materials_section_and_no_finance()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Materials));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Equal(["home", "my-work", "projects", "people", "sheets", "materials", "reports"], Sections(nav));
        foreach (var page in new[] { "materials", "materials/inventory", "materials/receiving", "materials/boms", "materials/work-orders", "materials/mrp", "materials/reports",
                     "materials/kits", "materials/backflush", "materials/capacity", "materials/physical-inventory", "materials/landed-cost" })
        {
            Assert.Contains(page, links);
        }

        Assert.DoesNotContain(links, l => l.StartsWith("accounting") || l.StartsWith("billing"));
        A11yAssert.Check(nav);
    }

    [Fact]
    public async Task Receiving_shows_only_for_people_named_as_receivers()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Employee, Receivable: 2));
        await using var _ = ctx;

        Assert.Contains("purchasing/receiving", Links(nav));
        Assert.Contains("Receiving (2)", nav.Markup);
    }

    [Fact]
    public async Task Project_manager_gets_approvals_resourcing_and_project_odcs_but_no_finance()
    {
        var employeeId = Guid.NewGuid();
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Employee, employeeId, Approvals: 3,
            Projects: [NavTestKit.Project("1001", "Alpha", employeeId), NavTestKit.Project("1002", "Beta")]));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Contains("approvals", links);
        Assert.Contains("resourcing", links);
        Assert.Contains("odcs", links);
        Assert.Equal("3", nav.Find("[data-testid='approvals-count']").TextContent.Trim());
        Assert.DoesNotContain(links, l => l.StartsWith("accounting") || l.StartsWith("billing") || l.StartsWith("setup"));
        Assert.Equal(["home", "my-work", "approvals", "projects", "people", "sheets", "reports"], Sections(nav));
    }

    [Fact]
    public async Task Project_manager_role_gets_projects_approvals_resourcing_and_pm_reports_even_before_managing_a_project()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.ProjectManager));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.DoesNotContain("setup/projects/new", links); // + New › Project
        Assert.Equal(["projects", "resourcing", "odcs"], nav.FindAll("[data-testid='nav-projects'] a[href]").Select(a => a.GetAttribute("href")));
        Assert.Contains("approvals", links);
        Assert.Contains("resourcing", links);
        Assert.Contains("odcs", links);
        Assert.Contains("reports/standard/project-status", links);
        Assert.Contains("reports/standard/funding-status", links);
        Assert.DoesNotContain(links, l => l.StartsWith("accounting") || l.StartsWith("billing") || l == "setup/projects" || l == "setup/people");
        Assert.Equal(["home", "my-work", "approvals", "projects", "people", "sheets", "reports"], Sections(nav));
        A11yAssert.Check(nav);
    }

    [Theory]
    [InlineData(OrgRoles.Finance)]
    [InlineData(OrgRoles.Admin)]
    [InlineData(OrgRoles.Employee)]
    public async Task Only_project_managers_get_the_pm_reports(OrgRoles roles)
    {
        var (nav, ctx) = await RenderAsync(new NavUser(roles));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.DoesNotContain("reports/standard/project-status", links);
        Assert.Equal(roles != OrgRoles.Employee, links.Contains("setup/projects")); // Finance and Admin: under Setup
    }

    [Fact]
    public async Task Supervisor_sees_team_time_and_the_approvals_inbox()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Employee, TimesheetApprover: true));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Contains("time/team", links);
        Assert.Contains("approvals", links);
        Assert.Empty(nav.FindAll("[data-testid='approvals-count']"));
    }

    [Fact]
    public async Task Finance_gets_finance_groups_one_reports_place_and_setup()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Finance));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Equal(["home", "my-work", "approvals", "projects", "people", "growth", "sheets", "finance", "materials", "reports", "admin"], Sections(nav));
        foreach (var group in new[] { "Accounting", "Billing & AR", "Payables & purchasing", "Cash", "Assets", "Revenue & compliance" })
        {
            Assert.Single(nav.FindAll($"[data-testid='nav-group-{group}']"));
        }

        Assert.Contains("payroll", links);
        Assert.Contains("accounting/reports", links);
        Assert.Single(links, l => l == "reports");
        Assert.Single(links, l => l == "setup/people");
        Assert.Single(links, l => l == "approvals");
        Assert.All(OldApprovalLinks, l => Assert.DoesNotContain(l, links));
        Assert.DoesNotContain("admin/users", links);
        Assert.DoesNotContain("notifications", links);
        Assert.Contains(">Setup<", nav.Find("[data-testid='nav-admin']").OuterHtml.Replace(" ", ""));
        Assert.True(links.Count <= 100 && links.Distinct().Count() == links.Count, $"{links.Count} links, some repeated"); // 8 under People (HR), 6 under Business development, 4 for the assistant, 12 for Wave A finance, 6 for Waves C and E
    }

    [Fact]
    public async Task Admin_gets_users_and_security_with_people_in_one_place()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Admin));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Contains("admin/users", links);
        Assert.Contains("setup/approval-workflows", links);
        Assert.Contains("setup/import", links);
        Assert.Contains("setup/organization", links);
        Assert.Single(links, l => l == "setup/people");
        Assert.Single(nav.FindAll("[data-testid='nav-group-People and access']"));
        Assert.Contains("time/team", links);
    }

    [Fact]
    public async Task Resource_manager_gets_resourcing_without_finance()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.ResourceManager));
        await using var _ = ctx;

        var links = Links(nav);
        Assert.Contains("resourcing", links);
        Assert.DoesNotContain("odcs", links);
        Assert.Empty(nav.FindAll("[data-testid='nav-finance']"));
        Assert.Empty(nav.FindAll("[data-testid='nav-admin']"));
    }

    [Fact]
    public async Task Groups_are_one_link_and_the_current_section_opens()
    {
        var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Finance));
        await using var _ = ctx;
        ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("cash/reconcile");

        var nav = ctx.Render<NavMenu>();
        nav.WaitForAssertion(() => Assert.NotEmpty(nav.FindAll("[data-testid='nav-group-Cash']")), TimeSpan.FromSeconds(5));

        Assert.True(Expanded(nav, "nav-finance"));
        Assert.False(Expanded(nav, "nav-my-work"));
        var cash = nav.Find("[data-testid='nav-group-Cash']");
        Assert.Equal("cash cash/reconcile cash/reconciliations cash/forecast cards", cash.GetAttribute("data-pages"));
        Assert.Contains("href=\"cash/reconcile\"", cash.OuterHtml); // links to the group page you're on, so it shows as current
        Assert.Empty(nav.FindAll("a[href='cash/forecast']")); // a tab, not a menu link
    }

    [Fact]
    public async Task Finance_sees_a_short_menu_that_still_reaches_every_page()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Finance));
        await using var _ = ctx;

        Assert.True(MenuLinkCount(nav) <= 40, $"{MenuLinkCount(nav)} menu links");
        Assert.True(Links(nav).Count > 80, "every page is still reachable through its group");
        Assert.Contains(">Businessdevelopment<", nav.Find("[data-testid='nav-growth']").OuterHtml.Replace(" ", "")); // one group: a plain link
    }

    [Fact]
    public async Task A_page_in_a_group_shows_its_siblings_as_tabs_and_detail_pages_do_not()
    {
        var ctx = NavTestKit.NewContext(new NavUser(OrgRoles.Finance));
        await using var _ = ctx;
        var navigation = ctx.Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();
        navigation.NavigateTo("cash/forecast");
        var access = await NavAccess.LoadAsync(ctx.Services);

        var tabs = ctx.Render<GroupTabs>(p => p.Add(t => t.Access, access));
        Assert.Equal(["cash", "cash/reconcile", "cash/reconciliations", "cash/forecast", "cards"], tabs.FindAll("a").Select(a => a.GetAttribute("href")!).ToList());
        Assert.Equal("Cash", tabs.Find("nav").GetAttribute("aria-label"));

        navigation.NavigateTo("purchasing/bills/" + Guid.NewGuid());
        tabs.WaitForAssertion(() => Assert.Empty(tabs.FindAll("a")));

        navigation.NavigateTo("time"); // not in a group
        tabs.WaitForAssertion(() => Assert.Empty(tabs.FindAll("a")));
    }

    [Fact]
    public async Task Employee_people_section_is_the_directory_and_their_own_profile()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Employee));
        await using var _ = ctx;

        Assert.Equal("hr hr/org-chart", nav.Find("[data-testid='nav-group-Directory']").GetAttribute("data-pages"));
        Assert.Equal("hr/me hr/benefits/me", nav.Find("[data-testid='nav-group-My profile']").GetAttribute("data-pages"));
        Assert.Equal(2, nav.FindAll("[data-testid='nav-people'] a[href]").Count);
    }

    [Fact]
    public async Task Hr_sees_its_own_work_first()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Hr));
        await using var _ = ctx;

        var people = nav.FindAll("[data-testid='nav-people'] a[href]").Select(a => a.TextContent.Trim()).ToList();
        Assert.Equal(["HR administration", "Directory", "My profile"], people);
    }

    [Fact]
    public async Task Finance_groups_lead_with_the_everyday_pages_and_setup_comes_first_under_setup()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Finance));
        await using var _ = ctx;

        var finance = nav.FindAll("[data-testid='nav-finance'] a[href]").Select(a => a.TextContent.Trim()).ToList();
        Assert.Equal(["Accounting", "Billing & AR", "Payables & purchasing", "Cash", "Assets", "Payroll", "Revenue & compliance", "Planning"], finance);
        Assert.StartsWith("accounting/journal accounting/close accounting/recurring", nav.Find("[data-testid='nav-group-Accounting']").GetAttribute("data-pages"));
        Assert.EndsWith("accounting/accounts accounting/posting-rules", nav.Find("[data-testid='nav-group-Accounting']").GetAttribute("data-pages"));
        Assert.StartsWith("purchasing/bills purchasing/payments purchasing/orders", nav.Find("[data-testid='nav-group-Payables & purchasing']").GetAttribute("data-pages"));
        Assert.Equal("Setup", nav.FindAll("[data-testid='nav-admin'] a[href]")[0].TextContent.Trim());
    }

    [Fact]
    public async Task Admin_reaches_support_requests_from_the_menu()
    {
        var (nav, ctx) = await RenderAsync(new NavUser(OrgRoles.Admin));
        await using var _ = ctx;

        Assert.Contains("admin/support", nav.Find("[data-testid='nav-group-People and access']").GetAttribute("data-pages")!.Split(' '));
        Assert.EndsWith("materials/setup", nav.Find("[data-testid='nav-group-Inventory']").GetAttribute("data-pages"));
    }

    private static bool Expanded(IRenderedComponent<NavMenu> nav, string testId) =>
        nav.Find($"[data-testid='{testId}'] > button").GetAttribute("aria-expanded") == "true";
}

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Companies;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Ui.Projects;

namespace Workbench.Tests.Projects;

/// <summary>The project page shows only the tabs the server allows, picks the tab from the route, and the list's two views.</summary>
public sealed class ProjectHubPageTests
{
    private static readonly Guid ProjectId = Guid.NewGuid();

    private static readonly ProjectMoneyDto Money = new(100_000m, 40_000m, 70_000m, 110_000m, 0.4m, 1.1m, 1_000_000m, 400_000m, 360_000m, 0.4m, 0.1m, 0.5m,
        ProjectHealth.Over, ["The forecast at completion is 10% over budget."]);

    private static ProjectHubRowDto Row(ProjectMoneyDto? money, string code = "1001") =>
        new(code == "1001" ? ProjectId : Guid.NewGuid(), code, "Modernization", null, "Pat Morgan", null, "C1", ProjectState.Active,
            new DateOnly(2026, 10, 1), new DateOnly(2027, 9, 30), null, money is not null, money is null, money is null ? null : 3, money);

    private static ProjectHubTabsDto Tabs(bool money, bool setup) => new(false, money, money, money, money, money, false, money, setup);

    private sealed class FakeHub(ProjectHubDto hub, ProjectHubListDto list) : IProjectHubService
    {
        public Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(list);

        public Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(hub);

        public Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ProjectTeamMemberDto>>([]);
    }

    private sealed class NoActivity : IActivityService
    {
        public Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ActivityDto>>([]);
    }

    private static BunitContext NewContext(ProjectHubDto hub, ProjectHubListDto? list = null, Action<IServiceCollection>? configure = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        configure?.Invoke(ctx.Services);
        ctx.Services.AddSingleton<IProjectHubService>(new FakeHub(hub, list ?? new ProjectHubListDto(false, [hub.Project])));
        ctx.Services.AddSingleton<IActivityService>(new NoActivity());
        ctx.Render<MudBlazor.MudPopoverProvider>();
        return ctx;
    }

    private static ProjectHubDto Hub(ProjectMoneyDto? money, ProjectHubTabsDto tabs) =>
        new(Row(money), "Support", "Army", tabs, null, [new ProjectAlertDto(ProjectAlertLevel.Critical, "budget", "Over budget", "financials")], new DateOnly(2026, 10, 18));

    private static List<string> TabLabels(IRenderedComponent<ProjectHubPage> page) =>
        page.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).ToList();

    [Fact]
    public async Task A_pm_sees_the_money_tabs_and_the_plain_language_numbers()
    {
        await using var ctx = NewContext(Hub(Money, Tabs(money: true, setup: false)));

        var page = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, ProjectId));

        // The everyday tabs; the occasional ones wait under More.
        page.WaitForAssertion(() => Assert.Equal(["Overview", "Budget & staffing", "Financials"], TabLabels(page)));
        Assert.NotEmpty(page.FindAll("[data-testid=project-more]"));
        foreach (var label in new[] { "Spent so far", "Budget", "Forecast at completion", "Funding left" })
        {
            Assert.Contains(label, page.Find("[data-testid=key-numbers]").TextContent);
        }

        Assert.Contains("$110,000", page.Markup);
        Assert.Contains("See Financials", page.Find("[data-testid=project-alerts]").TextContent);
    }

    [Fact]
    public async Task Someone_who_only_charges_sees_no_money_tabs_or_numbers()
    {
        await using var ctx = NewContext(Hub(null, Tabs(money: false, setup: false) with { Costs = true }));

        var page = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, ProjectId).Add(c => c.Tab, "financials"));

        page.WaitForAssertion(() => Assert.Equal(["Overview", "Costs"], TabLabels(page)));
        Assert.Empty(page.FindAll("[data-testid=key-numbers]")); // asked for Financials: falls back to the Overview
        Assert.Empty(page.FindAll("[data-testid=health-chip]"));
        Assert.NotEmpty(page.FindAll("[data-testid=schedule-health]"));
    }

    [Fact]
    public async Task The_route_picks_the_tab()
    {
        var project = new ProjectDto(ProjectId, "1001", "Modernization", null, null, null, ProjectState.Active, null, null, null);
        await using var ctx = NewContext(Hub(Money, Tabs(money: true, setup: true)), configure: services =>
        {
            services.AddSingleton(NullService<IProjectService>.Create(project));
            services.AddSingleton(NullService<IPeopleService>.Create());
            services.AddSingleton(NullService<IContractService>.Create());
            services.AddSingleton(NullService<IPlanSyncService>.Create(new PlanSyncReport(ProjectId, null, [])));
            services.AddSingleton(NullService<Workbench.Contracts.Identity.ICurrentUserContext>.Create(
                new Workbench.Contracts.Identity.CurrentUserInfo("pm@example.com", "Pat", Workbench.Contracts.Identity.OrgRoles.Employee, null)));
        });

        // Setup lives on the project page now; /setup/{section} opens that section.
        var page = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, ProjectId).Add(c => c.Tab, "setup").Add(c => c.Section, "sync"));

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("[data-testid=project-setup]")));
        Assert.Contains("Setup", TabLabels(page));
        Assert.Contains("Sync with the plan", page.Find("[data-testid=setup-sync]").TextContent);
        Assert.Empty(page.FindAll("[data-testid=setup-details] [data-testid=project-code]")); // closed sections render nothing
    }

    [Fact]
    public async Task An_occasional_tab_shows_in_the_bar_while_it_is_open()
    {
        await using var ctx = NewContext(Hub(Money, Tabs(money: true, setup: false)), configure: services =>
        {
            services.AddSingleton(NullService<Workbench.Contracts.Ceilings.ICeilingService>.Create());
            services.AddSingleton(NullService<Workbench.Contracts.Closeout.ICloseoutService>.Create());
        });

        var page = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, ProjectId).Add(c => c.Tab, "ceilings"));

        page.WaitForAssertion(() => Assert.Equal(["Overview", "Budget & staffing", "Financials", "Ceilings & closeout"], TabLabels(page)));
    }

    [Theory]
    [InlineData(null, "overview")]
    [InlineData("odcs", "costs")]
    [InlineData("EVM", "evm")]
    [InlineData("earned-value", "evm")]
    [InlineData("team", "staffing")]
    [InlineData("budget", "staffing")]
    [InlineData("budgets", "staffing")]
    public void Older_tab_names_still_work(string? segment, string slug) => Assert.Equal(slug, ProjectsFormat.Normalize(segment));

    [Fact]
    public async Task The_list_shows_money_columns_only_when_rows_have_money()
    {
        var withMoney = new ProjectHubListDto(true, [Row(Money), Row(null, "2002")]);
        var mine = new ProjectHubListDto(false, [Row(null)]);

        await using var financeCtx = NewContext(Hub(Money, Tabs(true, true)), withMoney);
        var finance = financeCtx.Render<ProjectListPage>();
        finance.WaitForAssertion(() => Assert.Equal(2, finance.FindAll("[data-testid=project-table] tbody tr").Count));
        Assert.Contains("Forecast vs budget", finance.Markup);
        Assert.Single(finance.FindAll("[data-testid=health-chip]"));
        Assert.Single(finance.FindAll("[data-testid=mine-toggle]"));

        await using var employeeCtx = NewContext(Hub(null, Tabs(false, false)), mine);
        var employee = employeeCtx.Render<ProjectListPage>();
        employee.WaitForAssertion(() => Assert.Contains("My projects", employee.Markup));
        Assert.DoesNotContain("Forecast vs budget", employee.Markup);
        Assert.DoesNotContain("$", employee.Find("[data-testid=project-table]").TextContent);
        Assert.Empty(employee.FindAll("[data-testid=mine-toggle]"));
    }

    [Fact]
    public async Task People_working_in_several_companies_get_a_company_filter_and_column_and_everyone_else_doesnt()
    {
        var main = new CompanyDto(Guid.NewGuid(), "MAIN", "Main company", null, 10, "USD", true, true);
        var fed = new CompanyDto(Guid.NewGuid(), "FED", "Federal Solutions LLC", null, 10, "USD", true, false);
        var list = new ProjectHubListDto(true, [Row(null) with { CompanyId = main.Id, CompanyCode = "MAIN" }, Row(null, "4001") with { CompanyId = fed.Id, CompanyCode = "FED" }]);
        void Companies(IServiceCollection services, CompanyContextDto context) =>
            services.AddSingleton(Workbench.Tests.Navigation.Stub<ICompanyService>.Create(("GetContextAsync", _ => Task.FromResult(context))));

        await using var multiCtx = NewContext(Hub(null, Tabs(false, false)), list, s => Companies(s, new CompanyContextDto([main, fed], null, true)));
        var multi = multiCtx.Render<ProjectListPage>();
        multi.WaitForAssertion(() => Assert.Single(multi.FindAll("[data-testid=project-company-filter]")));
        multi.WaitForAssertion(() => Assert.Contains(multi.FindAll("[data-testid=project-table] th"), th => th.TextContent.Contains("Company")));
        Assert.Contains("FED", multi.Find("[data-testid=project-table]").TextContent);

        // One company in view (limited to it, or working in it from the app bar): nothing new.
        await using var singleCtx = NewContext(Hub(null, Tabs(false, false)), list, s => Companies(s, new CompanyContextDto([main, fed], fed.Id, true)));
        var single = singleCtx.Render<ProjectListPage>();
        single.WaitForAssertion(() => Assert.Equal(2, single.FindAll("[data-testid=project-table] tbody tr").Count));
        Assert.Empty(single.FindAll("[data-testid=project-company-filter]"));
        Assert.DoesNotContain(single.FindAll("[data-testid=project-table] th"), th => th.TextContent.Contains("Company"));
    }
}

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Collaboration;
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

    private static BunitContext NewContext(ProjectHubDto hub, ProjectHubListDto? list = null)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
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

        page.WaitForAssertion(() => Assert.Equal(
            ["Overview", "Financials", "Budgets", "Revenue", "Earned value", "Monthly narrative", "Ceilings & closeout", "Team"], TabLabels(page)));
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
        await using var ctx = NewContext(Hub(Money, Tabs(money: true, setup: true)));

        var page = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, ProjectId).Add(c => c.Tab, "setup"));

        page.WaitForAssertion(() => Assert.Equal($"setup/projects/{ProjectId}", page.Find("[data-testid=setup-link]").GetAttribute("href")));
        Assert.Contains("Setup", TabLabels(page));
    }

    [Theory]
    [InlineData(null, "overview")]
    [InlineData("odcs", "costs")]
    [InlineData("EVM", "evm")]
    [InlineData("earned-value", "evm")]
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
}

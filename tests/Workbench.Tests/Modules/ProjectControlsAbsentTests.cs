using System.Net;
using Bunit;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Application.Assistant;
using Workbench.Application.Customization;
using Workbench.Application.MyTodo;
using Workbench.Application.Projects;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Projects;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Projects;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the project controls module (earned value, ceilings and closeout, the risk register and the monthly narrative live in a
/// private repository and load from the modules folder): its tables, tabs, cards, API paths, permissions, tools and widget are simply absent, and
/// nothing else notices. See docs/operations/module-extraction-projectcontrols.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class ProjectControlsAbsentTests
{
    private static readonly string[] ProjectControlsTables =
    [
        "Ceiling", "CeilingOverride", "CeilingAlert", "RateCeiling", "Closeouts", "CloseoutWaiver", "ProjectRisk",
        "EvmBaseline", "EvmWorkPackage", "EvmMilestone", "EvmStatusPeriod", "EvmPeriodSnapshot", "EvmVarianceAnalysis", "EvmChangeRequest", "EvmChangeRequestLine",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        var connectionString = TestDb.NewConnectionString(DatabaseProvider.Sqlite);
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            tables.Add(reader.GetString(0));
        }

        Assert.Equal(15, ProjectControlsTables.Length);
        Assert.Contains("Contract", tables); // A migrated core database...
        Assert.Contains("ProjectNarrative", tables); // ...including the narrative table, which stays in core with the assistant's records...
        Assert.DoesNotContain(tables, t => ProjectControlsTables.Contains(t)); // ...without the tables core used to create for the module.
        Assert.DoesNotContain("__EFMigrationsHistory_projectcontrols", tables);
    }

    [Fact]
    public void Permissions_api_segments_and_services_of_the_module_are_not_there()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "evm", "ceilings", "ceiling-overrides", "rate-ceilings", "closeout", "risks" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.All(new[] { "projects.evm.manage", "projects.ceilings.manage" }, k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.Contains(PermissionCatalog.AreaProjects, PermissionCatalog.Areas); // The area belongs to core.
        Assert.Empty(ModuleRegistry.Current.RowsOf<ModuleHomeWidget>());
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_the_rest_of_core_still_works()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        var id = Guid.NewGuid();
        foreach (var path in new[] { $"api/v1/projects/{id}/evm/baseline", $"api/v1/projects/{id}/ceilings", $"api/v1/contracts/{id}/ceilings", $"api/v1/contracts/{id}/rate-ceilings", $"api/v1/ceilings/{id}",
                     $"api/v1/risks/projects/{id}", $"api/v1/closeout/project/{id}", $"api/v1/assistant/projects/{id}/narrative/periods" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/migration/checklist")).StatusCode); // The rest of the same area works.
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/projects")).StatusCode);
    }

    [Fact]
    public async Task Core_hooks_answer_nothing_without_the_module()
    {
        await using var host = await SeededHost.CreateAsync();
        var services = host.Host.Get<IServiceProvider>();
        Assert.IsType<NoContractCeilingSource>(services.GetRequiredService<IContractCeilingSource>());
        Assert.Empty(services.GetServices<ICeilingProximitySource>());
        Assert.Empty(services.GetServices<IProjectAlertSource>());
        Assert.Empty(services.GetServices<ProjectHubTabContribution>());
        Assert.Empty(services.GetServices<UiSlotContribution>().Where(s => s.Slot is "project.setup.more" or "contract.tabs" or "project.financials.cards" or "project.financials.actions" or "dashboard.widget.earned-value"));
        Assert.DoesNotContain(services.GetServices<IMyTodoProvider>(), p => p.Name is "Variance analysis" or "Ceilings");
        Assert.DoesNotContain(services.GetRequiredService<AssistantToolbox>().AllSpecs, s => s.Name is "get_earned_value" or "get_ceilings");
        Assert.Empty(await new NoContractCeilingSource().HourCeilingsAsync(Guid.NewGuid(), default));

        // The saved "Earned value" dashboard widget (its type is core's) says where its figures come from.
        var data = await services.GetRequiredService<HomeWidgetData>().ComputeModuleAsync(
            new WidgetDto(Guid.NewGuid(), Guid.NewGuid(), WidgetType.EarnedValue, "Earned value", 0, 6, WidgetHeight.Medium, new WidgetConfig()), ModuleWidgetKeys.EarnedValue, default);
        Assert.Contains("module that isn't installed or licensed", data.Error);

        // Starting a project from a contract and the timesheet detail work without ceilings.
        await ProjectAccountingFixture.SeedAsync(host);
        var asEditor = host.As(SeededHost.Editor).Host;
        var timesheet = await asEditor.Get<Workbench.Contracts.Time.ITimesheetService>().GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 21));
        Assert.True(timesheet.CeilingRoom is null or { Count: 0 });
    }

    [Fact]
    public async Task The_project_page_shows_its_own_tabs_and_the_setup_more_section_without_ceilings()
    {
        var projectId = Guid.NewGuid();
        var row = new ProjectHubRowDto(projectId, "1001", "Modernization", null, "Pat Morgan", null, "C1", ProjectState.Active, new DateOnly(2026, 10, 1), new DateOnly(2027, 9, 30), null, true, false, 3,
            new ProjectMoneyDto(100_000m, 40_000m, 70_000m, 110_000m, 0.4m, 1.1m, 1_000_000m, 400_000m, 360_000m, 0.4m, 0.1m, 0.5m, ProjectHealth.Over, ["Over."]));
        var hub = new ProjectHubDto(row, "Support", "Army", new ProjectHubTabsDto(false, true, true, true, true, true, false, true, true), null, [new ProjectAlertDto(ProjectAlertLevel.Warning, "ceiling", "A ceiling is at 92%.", "ceilings")], new DateOnly(2026, 10, 18));
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        ctx.Services.AddSingleton<IProjectHubService>(new FakeHub(hub));
        ctx.Services.AddSingleton<Workbench.Contracts.Collaboration.IActivityService>(new NoActivity());
        ctx.Services.AddSingleton(NullService<IProjectService>.Create(new ProjectDto(projectId, "1001", "Modernization", null, null, null, ProjectState.Active, null, null, null)));
        ctx.Services.AddSingleton(NullService<IPeopleService>.Create());
        ctx.Services.AddSingleton(NullService<IContractService>.Create());
        ctx.Services.AddSingleton(NullService<IPlanSyncService>.Create(new PlanSyncReport(projectId, null, [])));
        ctx.Services.AddSingleton(NullService<ICurrentUserContext>.Create(new CurrentUserInfo("pm@example.com", "Pat", OrgRoles.Employee, null)));
        ctx.Render<MudBlazor.MudPopoverProvider>();

        // A leftover alert that points at a tab the host doesn't have shows its text and no link; asking for that tab lands on the Overview.
        var overview = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, projectId).Add(c => c.Tab, "evm"));
        overview.WaitForAssertion(() => Assert.Equal(["Overview", "Budget", "Financials", "Setup"], overview.FindAll(".mud-tab").Select(t => t.TextContent.Trim()).ToList()));
        Assert.Contains("A ceiling is at 92%.", overview.Find("[data-testid=project-alerts]").TextContent);
        Assert.DoesNotContain("See Ceilings", overview.Find("[data-testid=project-alerts]").TextContent);

        var setup = ctx.Render<ProjectHubPage>(p => p.Add(c => c.ProjectId, projectId).Add(c => c.Tab, "setup").Add(c => c.Section, "more"));
        setup.WaitForAssertion(() => Assert.NotEmpty(setup.FindAll("[data-testid=setup-more]")));
        var more = setup.Find("[data-testid=setup-more]").TextContent;
        Assert.DoesNotContain("ceilings", more, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("closeout", more, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class FakeHub(ProjectHubDto hub) : IProjectHubService
    {
        public Task<ProjectHubListDto> ListProjectsAsync(CancellationToken cancellationToken = default) => Task.FromResult(new ProjectHubListDto(false, [hub.Project]));

        public Task<ProjectHubDto> GetProjectAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult(hub);

        public Task<IReadOnlyList<ProjectTeamMemberDto>> GetTeamAsync(Guid projectId, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProjectTeamMemberDto>>([]);
    }

    private sealed class NoActivity : Workbench.Contracts.Collaboration.IActivityService
    {
        public Task<IReadOnlyList<Workbench.Contracts.Collaboration.ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Workbench.Contracts.Collaboration.ActivityDto>>([]);
    }
}

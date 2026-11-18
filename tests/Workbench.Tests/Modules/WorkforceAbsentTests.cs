using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Leave;
using Workbench.Application.MyTodo;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Licensing;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Tests.Leave;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the workforce module (HR records and payroll live in a private repository and load from the modules folder): their tables,
/// pages, API paths, permissions and menu entries are simply absent, and leave, timesheets, the Plan and resourcing do not notice. The pay calendar and pay
/// profile records the leave engine reads stay in core. See docs/operations/module-extraction-workforce.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class WorkforceAbsentTests
{
    private static readonly string[] WorkforceTables =
    [
        "BenefitDeductionExport", "BenefitDependent", "BenefitElection", "BenefitElectionDependent", "BenefitPlan", "BenefitTier", "ChecklistTask", "ChecklistTemplate", "ChecklistTemplateTask",
        "CompensationChange", "EmergencyContact", "EmployeeChecklist", "EmployeeCredential", "EmployeeDocument", "EmployeeEeoRecord", "EmployeeHrProfile", "EmployeePayRate", "HrChangeRequest",
        "LifeEvent", "MeritCycle", "OpenEnrollmentWindow", "PaylocityCostCenterMapping", "PaylocityRosterSetting", "PayrollExportLine", "PayrollExportRecord", "PayrollFileLayout",
        "PayrollProviderConnection", "PayrollProviderSetting", "PayrollRegister", "PayrollRegisterLine", "PayrollRun", "Position",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables_but_keeps_the_pay_calendar()
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

        Assert.Equal(32, WorkforceTables.Length);
        Assert.Contains("Employee", tables);
        Assert.All(new[] { "PayGroup", "PayPeriod", "EmployeePayProfile", "LaborCostAdjustment", "LeavePolicy", "LeaveLedgerEntry", "Holiday", "ResourceAllocation", "StaffingAssignment" }, t => Assert.Contains(t, tables)); // What core keeps.
        Assert.DoesNotContain(tables, t => WorkforceTables.Contains(t));
        Assert.DoesNotContain("__EFMigrationsHistory_workforce", tables);
    }

    [Fact]
    public void Permissions_api_segments_menu_and_services_of_the_module_are_not_there_but_staffing_is_core()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "hr", "payroll" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForApiSegment("resourcing")); // Staffing is core now.
        Assert.All(new[] { "payroll.run", "payroll.setup", "payroll.profiles.manage", "payroll.pay_rates.view", "hr.records.manage", "hr.positions.manage", "hr.compensation.manage", "hr.benefits.manage", "hr.onboarding.manage" },
            k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.All(new[] { Permission.HrWorkforceView, Permission.HrRestrictedReports, Permission.ResourcingManage, Permission.ResourcingView }, k => Assert.Equal(ModuleKeys.Core, ModuleMap.ForPermission(k)));
        Assert.Contains(PermissionCatalog.AreaPayroll, PermissionCatalog.Areas); // The areas belong to core.
        Assert.Contains(PermissionCatalog.AreaPeople, PermissionCatalog.Areas);
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href == "hr" || e.Href.StartsWith("hr/", StringComparison.Ordinal) || e.Href.StartsWith("payroll", StringComparison.Ordinal));
        Assert.Contains(NavCatalog.Entries, e => e.Href == "resourcing"); // Resourcing's own menu entries are core's.
        Assert.DoesNotContain(ListSources.All, s => s.ListKey == "people");
        var people = ApiKeyScopes.Areas.Single(a => a.Key == "people").PathSegments;
        Assert.Equal(["employees"], people);
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(IPayGroupChoiceService)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Resourcing.IResourcePlanningService)));
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_leave_and_staffing_still_work()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        var id = Guid.NewGuid();
        foreach (var path in new[] { "api/v1/hr/access", "api/v1/hr/employees", $"api/v1/hr/employees/{id}", "api/v1/hr/positions", "api/v1/hr/reports/headcount?asOf=2026-10-21", "api/v1/payroll/pay-groups", "api/v1/payroll/runs",
                     "api/v1/payroll/profiles", "api/v1/payroll/providers", "api/v1/payroll/paylocity/mapping", "api/v1/payroll/layouts/paylocity" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/leave/pay-groups")).StatusCode); // Leave's pay group list answers (empty without payroll).
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/leave/balances/mine")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/projects")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/migration/checklist")).StatusCode);
    }

    [Fact]
    public async Task Core_hooks_answer_nothing_and_leave_accrues_on_the_core_pay_calendar_without_payroll()
    {
        await using var kit = await LeaveKit.CreateAsync(); // The pay group, periods and profiles are core's records: no payroll service is involved.
        var services = kit.Seeded.Host.Get<IServiceProvider>();
        Assert.DoesNotContain(services.GetServices<IMyTodoProvider>(), p => p.Name == "Payroll");
        Assert.DoesNotContain(services.GetServices<IMyTodoProvider>(), p => p.Name is "Onboarding tasks" or "Clearances and certifications" or "Benefits" or "Personnel requests");
        Assert.Empty(services.GetServices<Workbench.Application.IModuleSeed>().Where(s => s.Stage is Workbench.Application.ModuleSeedStage.AfterCash));

        var pto = await kit.PtoPolicyAsync();
        kit.As(SeededHost.Finance);
        var result = await kit.Balances.RunAccrualAsync(kit.Period.Id);
        Assert.True(result.EntriesAdded > 0);
        Assert.Equal(4m, await kit.LedgerHoursAsync(kit.Fx.AnalystId, pto.Id));

        // The eligibility choices list the pay group.
        kit.As(SeededHost.Admin);
        Assert.Contains(await kit.Get<IPayGroupChoiceService>().ListAsync(), g => g.Code == "BW");
    }
}

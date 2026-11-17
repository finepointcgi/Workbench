using System.Net;
using Microsoft.Data.Sqlite;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Licensing;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the government contracting module (it lives in a private repository and loads from the modules
/// folder): its tables, pages, API paths and permissions are simply absent, and nothing else notices. See
/// docs/operations/module-extraction-govcon.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class GovConAbsentTests
{
    private static readonly string[] GovConTables =
    [
        "CmmcPoamItem", "CmmcPractice", "DisclosureAnswers", "DisclosureStatements", "IncurredCostInputs",
        "IncurredCostSubmissions", "ProcurementFileApprovals", "ProcurementFiles",
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

        Assert.Contains("PurchaseOrder", tables); // A migrated core database...
        Assert.DoesNotContain(tables, t => GovConTables.Contains(t)); // ...without the tables core used to create for the module.
        Assert.DoesNotContain("__EFMigrationsHistory_govcon", tables);
    }

    [Fact]
    public void Menu_permissions_and_api_paths_of_the_module_are_not_there()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "incurred-cost", "total-time", "compliance", "parallel-run" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href is "accounting/incurred-cost" or "accounting/total-time" or "setup/migration/parallel-run" or "admin/cmmc" or "admin/cpsr-readiness" or "admin/cas-disclosure" or "admin/dcaa-readiness");
        Assert.All(new[] { "compliance.incurred_cost", "compliance.cas_cpsr", "time.total_time", "admin.compliance" }, k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.DoesNotContain(Workbench.Contracts.Integrations.ApiKeyScopes.Areas.SelectMany(a => a.PathSegments), s => s is "incurred-cost" or "total-time");
    }

    [Fact]
    public async Task The_host_starts_and_the_modules_api_paths_are_404()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        foreach (var path in new[] { "api/v1/incurred-cost", "api/v1/compliance/cpsr/settings", "api/v1/total-time/policy", "api/v1/migration/parallel-run" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/migration/checklist")).StatusCode); // The rest of the same area works.
    }
}

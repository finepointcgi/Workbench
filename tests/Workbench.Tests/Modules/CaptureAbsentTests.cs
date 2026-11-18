using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Automations;
using Workbench.Application.Customization;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Dashboards;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Domain.Integrations;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Customization;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the capture module (business development and proposal pricing live in a private repository and load from the
/// modules folder): its tables, pages, API paths, permissions, catalog rows and importers are simply absent, and nothing else notices. See
/// docs/operations/module-extraction-capture.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class CaptureAbsentTests
{
    private static readonly string[] CaptureTables =
    [
        "CrmAccount", "CrmActivity", "CrmContact", "CrmSettings", "GateCriterion", "GateDefinition", "Opportunity", "OpportunityCompetitor", "OpportunityGateReview",
        "OpportunityGateScore", "OpportunityLead", "OpportunityMilestone", "OpportunityPlanItem", "OpportunityPwinAssessment", "OpportunityRequirement",
        "OpportunityStageChange", "OpportunityTeammate", "PriceClin", "PriceCostLine", "PriceEscalation", "PriceLaborHours", "PriceLaborLine", "PriceModel",
        "PricePeriod", "PriceWbs", "Proposal", "ProposalSection", "SavedLeadSearch",
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
        Assert.DoesNotContain(tables, t => CaptureTables.Contains(t)); // ...without the tables core used to create for the module.
        Assert.DoesNotContain("__EFMigrationsHistory_capture", tables);
    }

    [Fact]
    public void Menu_permissions_catalog_rows_and_api_paths_of_the_module_are_not_there()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "crm", "pricing" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href is "pricing" or "bd" || e.Href.StartsWith("bd/", StringComparison.Ordinal));
        Assert.DoesNotContain(NavCatalog.MenuEntries(new NavAccess(new CurrentUserInfo("a@example.com", "A", OrgRoles.Admin, null))), e => e.Section == NavCatalog.Growth); // The section hides when empty.
        Assert.All(new[] { "bd.pipeline.view", "bd.capture", "bd.opportunities.edit_all", "bd.bp_charge_code", "pricing.prices" }, k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.True(PermissionCatalog.Exists(Permission.FieldsPricingFee)); // The fee field belongs to the field permissions, which are core's.
        Assert.DoesNotContain(ApiKeyScopes.Areas.SelectMany(a => a.PathSegments), s => s is "crm" or "pricing");
        Assert.DoesNotContain(NewCatalog.Actions, a => a.Key == "opportunity");
        Assert.DoesNotContain("opportunities", CustomizableLists.All);
        Assert.DoesNotContain(ListSources.All, s => s.ListKey == "opportunities");
        Assert.DoesNotContain(DocumentNumberTypes.All, t => t.Key == "opportunity");
        Assert.Null(AutomationEntities.Find("opportunity"));
        Assert.DoesNotContain(AutomationTemplates.All, t => t.Key == "capture-stage"); // The recipe needs the opportunity record type.
        Assert.DoesNotContain(WebhookEventCatalog.All, e => e.Key == "opportunity.stage_changed");
        Assert.DoesNotContain(IntegrationEventType.All, e => e.Key == "opportunity.stage_changed");
        Assert.DoesNotContain(StatusSetCatalog.All, s => s.Key.StartsWith("opportunity-", StringComparison.Ordinal));
        Assert.DoesNotContain(ApprovalDocumentTypes.All, k => k == "opportunity-gate");
        Assert.DoesNotContain(BuiltInForms.All, f => f.EntityType == "opportunity");
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_the_pipeline_tile_says_where_it_comes_from()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        foreach (var path in new[] { "api/v1/crm/opportunities", "api/v1/crm/accounts", "api/v1/crm/pipeline/board", "api/v1/pricing/proposals", "api/v1/pricing/options" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await finance.GetAsync("api/v1/migration/checklist")).StatusCode); // The rest of the same area works.

        // A saved home page can still hold the "Weighted pipeline" tile: without the module it says so instead of failing.
        await using var host = await SeededHost.CreateAsync();
        var data = new HomeWidgetData(host.Host.Get<IServiceProvider>(), TimeProvider.System);
        var tile = await data.ComputeAsync(new WidgetDto(Guid.NewGuid(), Guid.NewGuid(), WidgetType.Kpi, "Pipeline", 0, 6, WidgetHeight.Medium, new WidgetConfig(Kpi: KpiKind.Pipeline)), default);
        Assert.Contains("module that isn't installed", tile.Error);
        Assert.Null(tile.Kpi);
    }

    [Fact]
    public async Task The_import_wizard_offers_no_crm_source_and_the_import_service_lists_no_crm_kind()
    {
        await using var host = await SeededHost.CreateAsync();
        var kinds = await host.As(SeededHost.Admin).Host.Get<Workbench.Contracts.Imports.IImportService>().ListKindsAsync();
        Assert.DoesNotContain(kinds, k => k.Source is Workbench.Contracts.Imports.ImportSource.CaptureExec or Workbench.Contracts.Imports.ImportSource.HubSpot
            or Workbench.Contracts.Imports.ImportSource.Salesforce or Workbench.Contracts.Imports.ImportSource.GenericCsv);
    }
}

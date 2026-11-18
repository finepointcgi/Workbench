using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Domain.Licensing;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the enterprise module (the Jira and GitLab sync, the Costpoint sync and customer links live in a private repository and load from the modules folder):
/// their tables, pages, API paths and webhook receivers are absent, and the platform layer that stays (the integration outbox, webhooks, API keys, connectors, companies, custom fields,
/// automations) does not notice. See docs/operations/module-extraction-enterprise.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class EnterpriseAbsentTests
{
    private static readonly string[] EnterpriseTables =
    [
        "JiraConnections", "JiraProjectLinks", "JiraItemLinks", "JiraSyncNotes", "JiraWebhookReceipts",
        "GitLabConnections", "GitLabProjectLinks", "GitLabItemLinks", "GitLabUserMaps", "GitLabSyncConflicts", "GitLabPendingIssues", "GitLabWebhookReceipts",
        "CpProjects", "CpWbsElements", "CpEmployees", "CpLaborCategories", "CpBudgets", "CpActuals", "CpSyncRuns",
        "CustomerShareLink",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables_but_keeps_the_platform_layers()
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

        Assert.Equal(20, EnterpriseTables.Length);
        Assert.All(new[] { "IntegrationOutboxEvents", "IntegrationConnectors", "WebhookEndpoints", "ApiKeys", "Companies", "CustomFieldDefinitions", "Contract", "Project", "AccessReview", "RetentionPolicy" }, t => Assert.Contains(t, tables));
        Assert.DoesNotContain(tables, t => EnterpriseTables.Contains(t));
        Assert.DoesNotContain("__EFMigrationsHistory_enterprise", tables);
    }

    [Fact]
    public void The_modules_services_and_segment_are_not_there_and_what_stayed_in_core_is_free_or_gated_as_before()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.Null(ModuleMap.ForApiSegment("costpoint"));
        Assert.All(new[] { "integrations", "customization", "automations", "access-reviews", "retention", "edi" }, s => Assert.Equal(ModuleKeys.Core, ModuleMap.ForApiSegment(s)));
        Assert.All(new[] { "companies", "approval-workflows" }, s => Assert.Equal(ModuleKeys.Enterprise, ModuleMap.ForApiSegment(s)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Integrations.IApiKeyService)));
        Assert.DoesNotContain(ModuleMap.ServiceInterfaces(), t => t.Name is "IJiraConnectionService" or "IJiraProjectSyncService" or "IGitLabConnectionService" or "IGitLabProjectSyncService" or "ICostpointService" or "ICustomerShareLinkService" or "ICustomerViewService");
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Integrations.IConnectorService)));
        Assert.Equal(ModuleKeys.Enterprise, ModuleMap.ForService(typeof(Workbench.Contracts.Approvals.IApprovalWorkflowService)));

        // The five features still need an enterprise key where the gate stayed in the product; nothing was added or removed.
        Assert.Equal(
            [Feature.MultiCompany, Feature.SingleSignOn, Feature.Integrations, Feature.CustomerLinks, Feature.ApprovalWorkflows],
            FeatureCatalog.All.Where(f => f.Module == ModuleIds.Enterprise).Select(f => f.Feature).ToArray());
        var enterprise = ModuleCatalog.Default.Single(m => m.Id == ModuleIds.Enterprise);
        Assert.True(enterprise.Enforced);
        Assert.False(enterprise.WholeModule);
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_webhooks_and_customer_page_answer_404_and_the_platform_layer_works()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var admin = factory.CreateClientAs("dev@workbench.local");

        foreach (var path in new[] { "api/v1/costpoint/status", "api/v1/integrations/jira/connection", "api/v1/integrations/gitlab/connection" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(path)).StatusCode);
        }

        // The anonymous entry points are not mapped at all (the host's catch-all page answers a call to them, and nothing receives it).
        var routes = factory.Services.GetServices<Microsoft.AspNetCore.Routing.EndpointDataSource>().SelectMany(d => d.Endpoints)
            .OfType<Microsoft.AspNetCore.Routing.RouteEndpoint>().Select(e => e.RoutePattern.RawText ?? "").ToList();
        Assert.DoesNotContain(routes, r => r.StartsWith("/webhooks/jira", StringComparison.Ordinal) || r.StartsWith("/webhooks/gitlab", StringComparison.Ordinal) || r.StartsWith("/c/", StringComparison.Ordinal));
        Assert.Contains(routes, r => r.StartsWith("/api/v1/", StringComparison.Ordinal)); // (the host's own are there)

        foreach (var path in new[] { "api/v1/integrations/webhooks", "api/v1/integrations/api-keys", "api/v1/integrations/connectors", "api/v1/companies" })
        {
            var response = await admin.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task No_slots_hooks_or_background_jobs_are_registered_without_the_module()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var services = seeded.Host.Get<IServiceProvider>();
        Assert.DoesNotContain(services.GetServices<UiSlotContribution>(), s => s.Slot is "integrations.tabs" or "item.details.links" or "project.setup.panels");
        Assert.DoesNotContain(services.GetServices<UiSlotContribution>(), s => s.Slot == "contract.tabs" && s.Component.Name == "CustomerLinksTabPanel");
        Assert.DoesNotContain(services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), h => h.GetType().Name is "JiraReconcileBackgroundService" or "GitLabReconcileBackgroundService" or "CostpointSyncBackgroundService" or "JiraChangeListener" or "GitLabChangeListener");
        Assert.DoesNotContain(services.GetServices<Workbench.Application.Integrations.Connectors.IConnector>(), c => c.GetType().Name is "JiraSyncConnector" or "GitLabSyncConnector");
    }
}

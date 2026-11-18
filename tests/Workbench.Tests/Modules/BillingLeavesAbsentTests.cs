using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Projects;
using Workbench.Application.Reports;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Contracts.Modules;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;
using Workbench.Ui.Projects;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the billing module (revenue recognition and the live push to QuickBooks Online and Xero live in a private repository and load from the
/// modules folder): their tables, pages, API paths, permission and menu entries are simply absent, and invoicing, payments, receivables and the accounting file export do not notice.
/// See docs/operations/module-extraction-billing-leaves.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class BillingLeavesAbsentTests
{
    private static readonly string[] BillingTables =
    [
        "RevenueSetup", "RevenueEstimate", "RevenueDelivery", "RevenueRun", "RevenueRunLine",
        "AccountingConnections", "AccountingOAuthStates", "AccountingProviderLinks", "AccountingPushRuns", "AccountingPushItems",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables_but_keeps_invoices_payments_and_the_export_history()
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

        Assert.Equal(10, BillingTables.Length);
        Assert.All(new[] { "Invoice", "InvoiceLine", "Payment", "PaymentApplication", "BillingMilestone", "AccountingMappings", "AccountingExportRuns", "AccountingExportedRecords", "GlJournalEntries" }, t => Assert.Contains(t, tables));
        Assert.DoesNotContain(tables, t => BillingTables.Contains(t));
        Assert.DoesNotContain("__EFMigrationsHistory_billing", tables);
    }

    [Fact]
    public void The_permission_api_segments_menu_and_services_of_the_module_are_not_there_but_invoicing_is_core()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "revenue", "accounting-live" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.All(new[] { "billing", "accounting-export" }, s => Assert.Equal(ModuleKeys.Core, ModuleMap.ForApiSegment(s)));
        Assert.False(PermissionCatalog.Exists("revenue.manage"));
        Assert.All(new[] { Permission.BillingInvoicesManage, Permission.BillingPaymentsRecord, Permission.BillingReportsView, Permission.GlExport }, k => Assert.Equal(ModuleKeys.Core, ModuleMap.ForPermission(k)));
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href.StartsWith("accounting/revenue", StringComparison.Ordinal));
        Assert.Contains(NavCatalog.Entries, e => e.Href == "billing/invoices"); // Invoices are core's.
        Assert.Contains(NavCatalog.Entries, e => e.Href == "accounting/indirect-rates");
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForNav("finance", "Billing & AR", "billing/invoices"));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Billing.IInvoiceService)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Billing.IPaymentService)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.AccountingExport.IAccountingExportService)));
        Assert.DoesNotContain("revenue", ApiKeyScopes.Areas.Single(a => a.Key == "billing").PathSegments);
        Assert.Contains("billing", ApiKeyScopes.Areas.Single(a => a.Key == "billing").PathSegments);
        Assert.DoesNotContain("revenue", new ProjectTabSet().All.Select(t => t.Slug)); // No Revenue tab on the project page.
        Assert.Equal(["overview", "plan", "staffing", "financials", "costs", "schedule-health", "setup"], new ProjectTabSet().All.Select(t => t.Slug));
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_invoicing_payments_and_file_exports_still_work()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        foreach (var path in new[] { "api/v1/revenue/projects", "api/v1/revenue/estimates", "api/v1/revenue/runs", "api/v1/accounting-live/connections", "api/v1/accounting-live/runs" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        foreach (var path in new[] { "api/v1/billing/invoices", "api/v1/billing/payments", "api/v1/accounting-export/formats" })
        {
            var response = await finance.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Core_hooks_answer_nothing_and_nothing_else_is_registered_for_the_project_start_the_close_checklist_or_the_report_list()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var services = seeded.Host.Get<IServiceProvider>();
        Assert.Empty(services.GetServices<IProjectStartHook>());
        Assert.DoesNotContain(services.GetServices<Workbench.Application.MyTodo.IMyTodoProvider>(), p => p.Name == "Estimates at completion");
        Assert.DoesNotContain(services.GetServices<Workbench.Application.Accounting.IPeriodCloseCheck>(), c => c.GetType().Name == "RevenueCloseCheck"); // The close checklist has no revenue item.
        Assert.Empty(services.GetServices<StandardReportPage>().Where(p => p.Key == "estimates-at-completion"));
        Assert.DoesNotContain(services.GetServices<UiSlotContribution>(), s => s.Slot.StartsWith("accounting-export.", StringComparison.Ordinal));
        Assert.DoesNotContain(services.GetServices<ProjectHubTabContribution>(), t => t.Slug == "revenue");
        Assert.Equal(CloseCheckKind.RevenueRecognized, CloseCheckKind.RevenueRecognized); // The saved-data name stays in core's enum.
    }
}

using System.Net;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Application.Billing;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Licensing;
using Workbench.Infrastructure.Persistence;
using Workbench.Plugins;
using Workbench.Tests.Identity;
using Workbench.Ui.Navigation;

namespace Workbench.Tests.Modules;

/// <summary>
/// The product as shipped, without the accounting module (cash and bank reconciliation, bank files, fixed assets, corporate budgets and recurring journals, and sales and use
/// tax live in a private repository and load from the modules folder): their tables, pages, API paths, permissions and menu entries are simply absent, and the general ledger,
/// payables, purchasing, currencies, ODCs, expense reports and the accounting exports do not notice. See docs/operations/module-extraction-accounting.md.
/// </summary>
[Collection(ModuleCollection.Name)]
public sealed class AccountingAbsentTests
{
    private static readonly string[] AccountingTables =
    [
        "ApBillTaxes", "AssetCategory", "AssetDepreciation", "AssetDepreciationRun", "AssetTaxBooks", "AssetTransfer", "BankAccounts", "BankAchSettings", "BankCloseWaivers", "BankMatchLines",
        "BankMatches", "BankReconciliations", "BankStatementImports", "BankTransactions", "CashForecastItems", "CashSettings", "CustomerTaxProfiles", "FixedAsset", "GlBudgetLines",
        "GlBudgetSubmissionLines", "GlBudgetSubmissions", "GlBudgetVersions", "GlPlanningCycles", "GlRecurringRuns", "GlRecurringSettings", "GlRecurringTemplateLines", "GlRecurringTemplates",
        "InvoiceTaxes", "PaymentFileDownloads", "PositivePayLayouts", "RemittanceAdvices", "SalesTaxSettings", "TaxCodeAssignments", "TaxCodes", "TaxExemptionCertificates", "TaxJurisdictions",
        "TaxRates", "VendorBankAccounts", "VendorBankChanges",
    ];

    [Fact]
    public void A_new_database_has_none_of_the_modules_tables_but_keeps_the_ledger_vendors_and_payables()
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

        Assert.Equal(39, AccountingTables.Length);
        Assert.All(new[] { "GlAccounts", "GlJournalEntries", "GlJournalLines", "Vendor", "ApBill", "ApBillLine", "ApPaymentRun", "PurchaseOrder", "Invoice", "Currencies", "FiscalPeriod", "IndirectPoolSetups" }, t => Assert.Contains(t, tables));
        Assert.DoesNotContain(tables, t => AccountingTables.Contains(t));
        Assert.DoesNotContain("__EFMigrationsHistory_accounting", tables);
    }

    [Fact]
    public void Permissions_api_segments_menu_and_services_of_the_module_are_not_there_but_the_ledger_is_core()
    {
        using var empty = ModuleHost.Use(ModuleHost.Empty);
        Assert.All(new[] { "cash", "assets", "planning", "sales-tax", "bank-files", "vendor-bank" }, s => Assert.Null(ModuleMap.ForApiSegment(s)));
        Assert.All(new[] { "accounting", "currencies", "indirect-rates", "purchasing", "cards" }, s => Assert.Equal(ModuleKeys.Core, ModuleMap.ForApiSegment(s)));
        Assert.All(new[] { "cash.manage", "cash.forecast.view", "assets.manage", "assets.depreciation.run", "planning.budgets.manage", "tax.sales_tax.manage", "gl.recurring.manage" }, k => Assert.False(PermissionCatalog.Exists(k), k));
        Assert.All(new[] { Permission.GlJournalsPost, Permission.GlClose, Permission.ApBillsManage, Permission.ApPaymentsManage, Permission.VendorBankDetails, Permission.VendorsManage, Permission.PurchaseOrdersManage, Permission.CardsManage },
            k => Assert.Equal(ModuleKeys.Core, ModuleMap.ForPermission(k)));
        Assert.Contains(PermissionCatalog.AreaCash, PermissionCatalog.Areas); // The areas belong to core.
        Assert.DoesNotContain(NavCatalog.Entries, e => e.Href is "cash" or "planning/budgets" or "assets" or "accounting/recurring" or "accounting/sales-tax" or "setup/sales-tax"
            || e.Href.StartsWith("cash/", StringComparison.Ordinal) || e.Href.StartsWith("assets/", StringComparison.Ordinal) || e.Href.StartsWith("planning/", StringComparison.Ordinal));
        Assert.Contains(NavCatalog.Entries, e => e.Href == "accounting/journal" || e.Href.StartsWith("purchasing", StringComparison.Ordinal)); // The ledger and payables menu entries are core's.
        Assert.Contains(NavCatalog.Entries, e => e.Href == "cards");
        var accounting = ApiKeyScopes.Areas.Single(a => a.Key == "accounting").PathSegments;
        Assert.DoesNotContain("cash", accounting);
        Assert.Contains("accounting", accounting);
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Accounting.IJournalService)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Purchasing.IApBillService)));
        Assert.Equal(ModuleKeys.Core, ModuleMap.ForService(typeof(Workbench.Contracts.Currencies.IRevaluationService)));
        Assert.False(Workbench.Contracts.ProjectAccounting.SetupSteps.CashAvailable); // The setup guide offers no bank account step without the cash page.
    }

    [Fact]
    public async Task The_host_starts_the_modules_api_paths_are_404_and_the_ledger_payables_and_exports_still_work()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = factory.CreateClientAs("pm@example.com");

        var id = Guid.NewGuid();
        foreach (var path in new[] { "api/v1/cash/accounts", "api/v1/cash/reconciliations", "api/v1/assets", "api/v1/assets/categories", "api/v1/planning/budgets", "api/v1/planning/cycles", "api/v1/accounting/recurring-journals", "api/v1/sales-tax/settings",
                     $"api/v1/bank-files/runs/{id}", $"api/v1/vendor-bank/{id}" })
        {
            Assert.Equal(HttpStatusCode.NotFound, (await finance.GetAsync(path)).StatusCode);
        }

        foreach (var path in new[] { "api/v1/accounting/accounts", "api/v1/purchasing/vendors", "api/v1/purchasing/bills", "api/v1/currencies/rates", "api/v1/accounting/indirect-rates/pools", "api/v1/accounting-export/formats", "api/v1/migration/checklist" })
        {
            var response = await finance.GetAsync(path);
            Assert.True(response.StatusCode == HttpStatusCode.OK, $"{path}: {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task Core_hooks_answer_nothing_and_invoices_and_bills_work_without_tax_or_assets()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var services = seeded.Host.Get<IServiceProvider>();
        Assert.Null(services.GetService<IInvoiceTaxHook>());
        Assert.Null(services.GetService<IBillTaxHook>());
        Assert.Null(services.GetService<IAssetSource>());
        Assert.Empty(services.GetServices<Workbench.Application.IModuleSeed>().Where(s => s.Stage is Workbench.Application.ModuleSeedStage.AfterProcurement or Workbench.Application.ModuleSeedStage.AfterSourcing));
    }
}

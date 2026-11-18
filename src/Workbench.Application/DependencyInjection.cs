using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Application.Automation;
using Workbench.Application.Licensing;
using Workbench.Application.Billing;
using Workbench.Application.Ceilings;
using Workbench.Application.Cash;
using Workbench.Application.Collaboration;
using Workbench.Application.ContractDetail;
using Workbench.Application.Costpoint;
using Workbench.Application.Identity;
using Workbench.Application.Imports;
using Workbench.Application.IndirectRates;
using Workbench.Application.Leave;
using Workbench.Application.Odcs;
using Workbench.Application.Payroll;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.Reports;
using Workbench.Application.Assets;
using Workbench.Application.PerDiem;
using Workbench.Application.Resourcing;
using Workbench.Application.Revenue;
using Workbench.Application.Dashboards;
using Workbench.Application.Evm;
using Workbench.Application.MyWork;
using Workbench.Application.MyTodo;
using Workbench.Application.Security;
using Workbench.Application.Support;
using Workbench.Application.Sheets;
using Workbench.Application.Tenancy;
using Workbench.Application.Time;
using Workbench.Contracts.Sheets;

namespace Workbench.Application;

public static class DependencyInjection
{
    /// <param name="registerModules">
    /// Registers the license modules loaded from the modules folder (<c>ModuleHost.ConfigureServices</c>). It runs after every in-repo
    /// feature and before <c>AddWorkbenchIdentity</c> and the edition gates, so a module's services are wrapped with the same permission
    /// and edition checks as the in-repo ones, and may decorate them. Null (the default) registers the modules the process has loaded, which is none unless a loader ran.
    /// </param>
    public static IServiceCollection AddWorkbenchApplication(this IServiceCollection services, Action<IServiceCollection>? registerModules = null)
    {
        // A host that doesn't pass its own registration still gets the modules the process has loaded (none unless a loader ran): a container
        // built by hand, such as a test's, sees the same services as the web host.
        registerModules ??= modules => Workbench.Plugins.ModuleHost.Current.ConfigureServices(modules, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISheetService, SheetService>();
        services.AddScoped<IItemService, ItemService>();
        services.AddScoped<IColumnService, ColumnService>();
        services.AddScoped<ISheetViewService, SheetViewService>();
        services.AddScoped<ISheetBulkEditService, SheetBulkEditService>(); // Paste, fill and clear in one save (one reflow, one automation batch). // Saved grid views (sort, filter, hidden columns) and sheet exports.
        services.AddWorkbenchScheduling();
        services.AddWorkbenchCollaboration();
        services.AddWorkbenchCostpoint();
        services.AddWorkbenchProjectAccounting();
        services.AddWorkbenchAutomation();
        services.AddWorkbenchMyWork();
        services.AddWorkbenchDashboards();
        services.AddWorkbenchTimekeeping();
        services.AddScoped<OrgProfile.CompanyProfileService>().AddScoped<Contracts.Company.ICompanyProfileService>(sp => sp.GetRequiredService<OrgProfile.CompanyProfileService>());
        services.AddWorkbenchOdcs();
        services.AddWorkbenchFinancials();
        services.AddWorkbenchSetup();
        services.AddWorkbenchAccounting(); // GL posting decorators wrap time/ODC approvals; must stay above AddWorkbenchIdentity.
        services.AddWorkbenchIndirectRates(); // After AddWorkbenchAccounting (decorates its reports, adds a close check); above AddWorkbenchIdentity.
        services.AddWorkbenchSecurity();
        services.AddWorkbenchContractDetail();
        services.AddWorkbenchBilling(); // Billing/AR: posts through the GL; must stay above AddWorkbenchIdentity.
        services.AddWorkbenchPurchasing(); // AP bills post through AddWorkbenchAccounting's engine; above AddWorkbenchIdentity.
        services.AddWorkbenchTenancy(); // SingleTenant defaults; AddWorkbenchInfrastructure replaces them from the Tenancy section.
        services.AddWorkbenchImports(); // Costpoint and Smartsheet importers; they import through the services above.
        services.AddWorkbenchAssets(); // Fixed assets: depreciation posts through the GL and adds a close check; above AddWorkbenchIdentity.
        services.AddWorkbenchCash(); // Cash and bank reconciliation: books bank items through the GL journal; above AddWorkbenchIdentity.
        services.AddWorkbenchPayroll(); // Payroll runs post through the GL (JournalSource.Payroll); above AddWorkbenchIdentity.
        services.AddWorkbenchLeave(); // Decorates ITimesheetService (leave balance checks); after timekeeping, above AddWorkbenchIdentity.
        services.AddWorkbenchPerDiem(); // Per diem rates and travel per diem; decorates IExpenseReportService (after AddWorkbenchOdcs), above AddWorkbenchIdentity.
        services.AddWorkbenchResourcing(); // Allocations, capacity, utilization and allocation-based ETC; above AddWorkbenchIdentity.
        services.AddWorkbenchRevenue(); // Revenue recognition: runs post through the GL (JournalSource.Revenue) and add a close check; after billing, above AddWorkbenchIdentity.
        services.AddWorkbenchReports(); // Standard reports, report writer, saved reports and schedules; above AddWorkbenchIdentity.
        services.AddWorkbenchEvm(); // Earned value (PMB, status, VARs, BCRs, IPMDAR exports); ACWP via IBurdenedCostService; above AddWorkbenchIdentity.
        services.AddWorkbenchCeilings(); // Ceilings, rate caps and closeout; decorates time, ODCs, expenses, AP bills, POs and invoices; above AddWorkbenchIdentity.
        Approvals.ApprovalRegistration.AddWorkbenchApprovals(services); // Approval workflow engine, inbox, delegation; above AddWorkbenchIdentity.
        services.AddScoped<Contracts.Projects.IProjectHubService, Projects.ProjectHubService>(); // Projects hub (list and project overview); above AddWorkbenchIdentity.
        services.AddWorkbenchMyTodo(); // Home page to-dos: providers read through the permission-checked services above.
        services.AddWorkbenchReauthentication(); // SSO/MFA sign-in settings, re-authentication for sensitive actions (decorators), compliance checklists; above AddWorkbenchIdentity.
        services.AddWorkbenchSupport(); // In-app support requests (Support:Email), the host's cross-tenant view and per-user help state; above AddWorkbenchIdentity.
        Payroll.PaylocityRegistration.AddWorkbenchPaylocity(services); // Paylocity file exchange: mapping, export history, roster import; above AddWorkbenchIdentity.
        Payroll.PayrollLayoutRegistration.AddWorkbenchPayrollLayouts(services); // Configurable Paylocity hours export and register layouts; above AddWorkbenchIdentity.
        Migration.MigrationRegistration.AddWorkbenchMigration(services); // Go-live checklist and Costpoint reconciliation; reads imports and reports; above AddWorkbenchIdentity.
        Tasks.TaskRegistration.AddWorkbenchTasks(services); // Task assignment (item Assignees), its notifications and the "Assigned to you" to-do; above AddWorkbenchIdentity.
        Hr.HrRegistration.AddWorkbenchHr(services); // HR: personnel, positions, compensation (pay rate history), benefits, onboarding/offboarding, reports, to-dos; above AddWorkbenchIdentity.
        Assistant.AssistantRegistration.AddWorkbenchAssistant(services); // AI assistant (off by default), PM narratives and timesheet flags; tools read through the permission-checked services; above AddWorkbenchIdentity.
        Planning.PlanningRegistration.AddWorkbenchPlanning(services); // Recurring journals (post through the GL), corporate GL budgets, budget vs actual, planning cycles (approval handler); above AddWorkbenchIdentity.
        Currencies.CurrencyRegistration.AddWorkbenchCurrencies(services); // Multicurrency: currencies, rates, revaluation (posts through the GL, adds a close check); above AddWorkbenchIdentity.
        Companies.CompanyRegistration.AddWorkbenchCompanies(services); // Multicompany: companies, intercompany accounts, consolidation; the ledger's company rules live in GeneralLedger; above AddWorkbenchIdentity.
        Travel.TravelRegistration.AddWorkbenchTravel(services); // Travel authorizations and corporate cards; decorates IExpenseReportService (after AddWorkbenchOdcs), card lines post to the card liability; above AddWorkbenchIdentity.
        BankFiles.BankFilesRegistration.AddWorkbenchBankFiles(services); // Vendor ACH details (approved changes), NACHA and positive pay files from payment runs, remittance advice; above AddWorkbenchIdentity.
        AccountingExport.AccountingExportRegistration.AddWorkbenchAccountingExport(services); // QuickBooks, Xero and generic accounting files, account mapping, export history; Finance/Admin; above AddWorkbenchIdentity.
        AccountingLive.AccountingLiveRegistration.AddWorkbenchAccountingLive(services); // Live push to QuickBooks Online and Xero (OAuth 2.0); above AddWorkbenchIdentity.
        SalesTax.SalesTaxRegistration.AddWorkbenchSalesTax(services); // Sales and use tax: rates, codes, exemptions, invoice tax at approval, use tax on posted bills, the return; above AddWorkbenchIdentity.
        Integrations.IntegrationRegistration.AddWorkbenchIntegrations(services); // Webhooks (outbox + dispatcher), API keys and connectors (Teams, Slack, GL export, report export); Admin-only; above AddWorkbenchIdentity.
        Calendar.CalendarRegistration.AddWorkbenchCalendarFeed(services); // Read-only per-person .ics calendar feed (leave, holidays, due tasks, milestones, timesheet due dates).

        services.AddWorkbenchSourcing(); // RFQs and quotes, the supplier portal and EDI; decorates IPurchaseOrderService (850 on issue); above AddWorkbenchIdentity.
        Receipts.ReceiptRegistration.AddWorkbenchReceipts(services); // Receipt library ("My receipts"), reading receipts and folios (PDF text, assistant when on), per diem lodging check; above AddWorkbenchIdentity.
        Customization.CustomizationRegistration.AddWorkbenchCustomization(services); // Custom fields, value lists, status labels, numbering; above AddWorkbenchIdentity.
        services.AddScoped<Workbench.Contracts.Identity.IRoleService, Identity.RoleService>(); // Custom roles and permissions (Admin › Roles); checks in AddWorkbenchIdentity.
        services.AddScoped<Identity.PermissionRegistry>(); // Run-time permissions (custom fields, automations, forms) next to the built-in catalog; unchecked, features call it from their own services.
        services.AddScoped<Workbench.Contracts.Identity.IPermissionRegistry>(sp => sp.GetRequiredService<Identity.PermissionRegistry>());
        services.AddScoped<Workbench.Contracts.Identity.IPermissionCatalog>(sp => sp.GetRequiredService<Identity.PermissionRegistry>());
        Automations.AutomationBuilderRegistration.AddWorkbenchAutomationBuilder(services); // Admin › Automation: cross-module automations (capture hook in AuditRecorder, queue, schedules, thresholds); above AddWorkbenchIdentity.
        Customization.LayoutsRegistration.AddWorkbenchLayouts(services); // Form layouts (required fields checked on save), list views, role home pages; after every form's service, above AddWorkbenchIdentity.
        services.AddWorkbenchSheetExtras(); // Wave E: linked rows, lookups, cross-sheet references, templates, copy, cross-sheet reports, forms; above AddWorkbenchIdentity.
        registerModules?.Invoke(services); // License modules from outside the repository (docs/operations/module-plugins.md); above AddWorkbenchIdentity.
        // Keep AddWorkbenchIdentity last: its permission decorators must wrap the outermost existing decorator
        // (activity recording and any later feature's), so new decorators belong above this line.
        services.AddWorkbenchIdentity();
        services.AddWorkbenchEditionGates(); // After identity: the edition check is the outermost wrapper, on top of the role checks.
        return services;
    }
}

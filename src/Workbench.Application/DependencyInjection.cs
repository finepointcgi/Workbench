using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Application.Automation;
using Workbench.Application.Billing;
using Workbench.Application.Ceilings;
using Workbench.Application.Cash;
using Workbench.Application.Collaboration;
using Workbench.Application.ContractDetail;
using Workbench.Application.Costpoint;
using Workbench.Application.Identity;
using Workbench.Application.Imports;
using Workbench.Application.IncurredCost;
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
    public static IServiceCollection AddWorkbenchApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<ISheetService, SheetService>();
        services.AddScoped<IItemService, ItemService>();
        services.AddScoped<IColumnService, ColumnService>();
        services.AddWorkbenchScheduling();
        services.AddWorkbenchCollaboration();
        services.AddWorkbenchCostpoint();
        services.AddWorkbenchProjectAccounting();
        services.AddWorkbenchAutomation();
        services.AddWorkbenchMyWork();
        services.AddWorkbenchDashboards();
        services.AddWorkbenchTimekeeping();
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
        services.AddWorkbenchIncurredCost(); // Incurred cost submission (ICE schedules): reads the GL, pools, billing and AP; above AddWorkbenchIdentity.
        services.AddWorkbenchReports(); // Standard reports, report writer, saved reports and schedules; above AddWorkbenchIdentity.
        services.AddWorkbenchEvm(); // Earned value (PMB, status, VARs, BCRs, IPMDAR exports); ACWP via IBurdenedCostService; above AddWorkbenchIdentity.
        services.AddWorkbenchCeilings(); // Ceilings, rate caps and closeout; decorates time, ODCs, expenses, AP bills, POs and invoices; above AddWorkbenchIdentity.
        Approvals.ApprovalRegistration.AddWorkbenchApprovals(services); // Approval workflow engine, inbox, delegation; above AddWorkbenchIdentity.
        services.AddScoped<Contracts.Projects.IProjectHubService, Projects.ProjectHubService>(); // Projects hub (list and project overview); above AddWorkbenchIdentity.
        services.AddWorkbenchMyTodo(); // Home page to-dos: providers read through the permission-checked services above.
        services.AddWorkbenchReauthentication(); // SSO/MFA sign-in settings, re-authentication for sensitive actions (decorators), compliance checklists; above AddWorkbenchIdentity.
        services.AddWorkbenchSupport(); // In-app support requests (Support:Email), the host's cross-tenant view and per-user help state; above AddWorkbenchIdentity.
        Payroll.PayrollLayoutRegistration.AddWorkbenchPayrollLayouts(services); // Configurable Paylocity hours export and register layouts; above AddWorkbenchIdentity.
        Migration.MigrationRegistration.AddWorkbenchMigration(services); // Go-live checklist and Costpoint reconciliation; reads imports and reports; above AddWorkbenchIdentity.
        Tasks.TaskRegistration.AddWorkbenchTasks(services); // Task assignment (item Assignees), its notifications and the "Assigned to you" to-do; above AddWorkbenchIdentity.
        Hr.HrRegistration.AddWorkbenchHr(services); // HR: personnel, positions, compensation (pay rate history), benefits, onboarding/offboarding, reports, to-dos; above AddWorkbenchIdentity.
        Materials.MaterialsRegistration.AddWorkbenchMaterials(services); // Parts, inventory, BOMs, work orders, MRP; posts through the GL (JournalSource.Inventory/Manufacturing), decorates PO receiving; above AddWorkbenchIdentity.
        Crm.CrmRegistration.AddWorkbenchCrm(services); // Business development: accounts, opportunities, gates (approval handler), SAM.gov leads, pipeline, to-dos; above AddWorkbenchIdentity.
        Pricing.PricingRegistration.AddWorkbenchPricing(services); // Proposal pricing (price models, scenarios, locked versions, cost volume, won handoff); above AddWorkbenchIdentity.
        Assistant.AssistantRegistration.AddWorkbenchAssistant(services); // AI assistant (off by default), PM narratives and timesheet flags; tools read through the permission-checked services; above AddWorkbenchIdentity.
        Planning.PlanningRegistration.AddWorkbenchPlanning(services); // Recurring journals (post through the GL), corporate GL budgets, budget vs actual, planning cycles (approval handler); above AddWorkbenchIdentity.
        TotalTime.TotalTimeRegistration.AddWorkbenchTotalTime(services); // Total time accounting: labor cost method (Standard/TTA/Hybrid) and uncompensated overtime reports; above AddWorkbenchIdentity.
        Currencies.CurrencyRegistration.AddWorkbenchCurrencies(services); // Multicurrency: currencies, rates, revaluation (posts through the GL, adds a close check); above AddWorkbenchIdentity.
        Companies.CompanyRegistration.AddWorkbenchCompanies(services); // Multicompany: companies, intercompany accounts, consolidation; the ledger's company rules live in GeneralLedger; above AddWorkbenchIdentity.
        Travel.TravelRegistration.AddWorkbenchTravel(services); // Travel authorizations and corporate cards; decorates IExpenseReportService (after AddWorkbenchOdcs), card lines post to the card liability; above AddWorkbenchIdentity.

        // Keep AddWorkbenchIdentity last: its permission decorators must wrap the outermost existing decorator
        // (activity recording and any later feature's), so new decorators belong above this line.
        services.AddWorkbenchIdentity();
        return services;
    }
}

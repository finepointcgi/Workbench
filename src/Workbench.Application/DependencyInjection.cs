using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Application.Automation;
using Workbench.Application.Billing;
using Workbench.Application.Collaboration;
using Workbench.Application.ContractDetail;
using Workbench.Application.Costpoint;
using Workbench.Application.Identity;
using Workbench.Application.IndirectRates;
using Workbench.Application.Odcs;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Application.Dashboards;
using Workbench.Application.MyWork;
using Workbench.Application.Security;
using Workbench.Application.Sheets;
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

        // Keep AddWorkbenchIdentity last: its permission decorators must wrap the outermost existing decorator
        // (activity recording and any later feature's), so new decorators belong above this line.
        services.AddWorkbenchIdentity();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Automation;
using Workbench.Application.Collaboration;
using Workbench.Application.Costpoint;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Dashboards;
using Workbench.Application.MyWork;
using Workbench.Application.Sheets;
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

        // Keep AddWorkbenchIdentity last: its permission decorators must wrap the outermost existing decorator
        // (activity recording and any later feature's), so new decorators belong above this line.
        services.AddWorkbenchIdentity();
        return services;
    }
}

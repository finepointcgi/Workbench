using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Costpoint;
using Workbench.Application.ProjectAccounting;
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
        services.AddWorkbenchSetup();
        return services;
    }
}

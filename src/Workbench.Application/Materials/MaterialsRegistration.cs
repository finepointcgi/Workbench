using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;

namespace Workbench.Application.Materials;

public static class MaterialsRegistration
{
    /// <summary>
    /// Materials and manufacturing: parts, inventory, BOMs, routings, work orders, MRP and reports, plus the purchasing
    /// decorator that receives part lines into stock. Binds <see cref="MaterialsOptions"/> from config <c>Materials</c>.
    /// Call after AddWorkbenchPurchasing and AddWorkbenchAccounting and before AddWorkbenchIdentity (which adds
    /// <c>AddWorkbenchMaterialsPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchMaterials(this IServiceCollection services)
    {
        services.AddOptions<MaterialsOptions>().BindConfiguration(MaterialsOptions.Section);
        services.AddScoped<MaterialsLedger>();
        services.AddScoped<InventoryReceiptContext>();
        services.AddScoped<IPartService, PartService>();
        services.AddScoped<IInventoryService, InventoryService>();
        services.AddScoped<IManufacturingService, ManufacturingService>();
        services.AddScoped<IMrpService, MrpService>();
        services.AddScoped<IMaterialReportService, MaterialReportService>();
        services.Decorate<IPurchaseOrderService, InventoryReceivingPurchaseOrderService>(); // Part lines received anywhere go into stock.
        return services;
    }
}

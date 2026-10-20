using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Materials;

namespace Workbench.Application.Materials;

public static class ShopFloorRegistration
{
    /// <summary>
    /// Kitting, backflush settings and shortages, landed cost, physical inventory and finite capacity scheduling. Binds
    /// <see cref="LandedCostOptions"/> from config <c>Materials</c>. Call after AddWorkbenchMaterials and before
    /// AddWorkbenchIdentity (which adds <c>AddWorkbenchShopFloorPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchShopFloor(this IServiceCollection services)
    {
        services.AddOptions<LandedCostOptions>().BindConfiguration(MaterialsOptions.Section);
        services.AddScoped<IKittingService, KittingService>();
        services.AddScoped<IBackflushService, BackflushService>();
        services.AddScoped<ILandedCostService, LandedCostService>();
        services.AddScoped<IPhysicalInventoryService, PhysicalInventoryService>();
        services.AddScoped<ICapacitySchedulingService, CapacitySchedulingService>();
        return services;
    }
}

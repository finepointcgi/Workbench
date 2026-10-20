using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Materials;

namespace Workbench.Application.Materials;

public static class SalesRegistration
{
    /// <summary>
    /// Sales orders and shipping: order entry, pick/pack/ship (posting through <see cref="MaterialsLedger"/>), the DD250, the
    /// WAWF receiving report (through <see cref="IWawfSubmitter"/>, by default <see cref="ManualWawfSubmitter"/>) and the
    /// delivery invoice through billing. Binds <see cref="SalesShippingOptions"/> from config <c>Materials:Sales</c>.
    /// Call after AddWorkbenchMaterials and AddWorkbenchBilling, before AddWorkbenchIdentity.
    /// </summary>
    public static IServiceCollection AddWorkbenchSalesShipping(this IServiceCollection services)
    {
        services.AddOptions<SalesShippingOptions>().BindConfiguration(SalesShippingOptions.Section);
        services.TryAddScoped<IWawfSubmitter, ManualWawfSubmitter>();
        services.AddScoped<ShipmentInvoicer>();
        services.AddScoped<ISalesOrderService, SalesOrderService>();
        services.AddScoped<IShippingService, ShippingService>();
        return services;
    }
}

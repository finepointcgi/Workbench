using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Crm;
using Workbench.Contracts.Customization;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Purchasing;

namespace Workbench.Application.Customization;

public static class LayoutsRegistration
{
    /// <summary>
    /// Form layouts, list views and role home pages. The field catalog and the access seam are <c>TryAdd</c>ed, so the
    /// custom fields and custom roles modules replace them. Register it after every service whose save requests a form
    /// layout can make fields required for, and above <c>AddWorkbenchIdentity</c> (whose permission checks then run first).
    /// </summary>
    public static IServiceCollection AddWorkbenchLayouts(this IServiceCollection services)
    {
        services.TryAddScoped<IEntityFieldCatalog, BuiltInEntityFieldCatalog>();
        services.TryAddScoped<ICustomizationAccess, DefaultCustomizationAccess>();
        services.AddScoped<FormLayoutService>();
        services.AddScoped<IFormLayoutService>(sp => sp.GetRequiredService<FormLayoutService>());
        services.AddScoped<IListViewService, ListViewService>();
        services.AddScoped<IHomePageService, HomePageService>();
        services.AddScoped<FormRequirementGuard>();

        // Fields a layout makes required are checked on every save of these records.
        Guard<IProjectService>(services);
        Guard<IContractService>(services);
        Guard<ISetupAdminService>(services);
        Guard<IPeopleService>(services);
        Guard<IVendorService>(services);
        Guard<IPurchaseOrderService>(services);
        Guard<IApBillService>(services);
        Guard<IInvoiceService>(services);
        Guard<IExpenseReportService>(services);
        Guard<IPartService>(services);
        Guard<IOpportunityService>(services);
        return services;
    }

    /// <summary>Wraps the last registration of <typeparamref name="T"/> in a <see cref="FormRequirementProxy{T}"/>, keeping its lifetime.</summary>
    private static void Guard<T>(IServiceCollection services)
        where T : class
    {
        var index = services.ToList().FindLastIndex(d => d.ServiceType == typeof(T) && !d.IsKeyedService);
        if (index < 0)
        {
            throw new InvalidOperationException($"Register {typeof(T).Name} before AddWorkbenchCustomization.");
        }

        var original = services[index];
        services[index] = ServiceDescriptor.Describe(typeof(T), sp => FormRequirementProxy<T>.Create((T)(original.ImplementationInstance
            ?? original.ImplementationFactory?.Invoke(sp)
            ?? ActivatorUtilities.CreateInstance(sp, original.ImplementationType!)), sp), original.Lifetime);
    }
}

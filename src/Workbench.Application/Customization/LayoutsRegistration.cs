using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Customization;
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
        services.AddFormRequirementGuard<IProjectService>();
        services.AddFormRequirementGuard<IContractService>();
        services.AddFormRequirementGuard<ISetupAdminService>();
        services.AddFormRequirementGuard<IPeopleService>();
        services.AddFormRequirementGuard<IVendorService>();
        services.AddFormRequirementGuard<IPurchaseOrderService>();
        services.AddFormRequirementGuard<IApBillService>();
        services.AddFormRequirementGuard<IInvoiceService>();
        services.AddFormRequirementGuard<IExpenseReportService>();
        return services;
    }

    /// <summary>
    /// Wraps the last registration of <typeparamref name="T"/> in a <see cref="FormRequirementProxy{T}"/>, keeping its lifetime: the fields a layout
    /// makes required are then checked on every save through the service. A module that owns a record with a built-in form calls this after
    /// registering the service and before its permission decorators (the built-in services are guarded in <see cref="AddWorkbenchLayouts"/>).
    /// </summary>
    public static void AddFormRequirementGuard<T>(this IServiceCollection services)
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

using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Files;
using Workbench.Application.Purchasing.Edi;
using Workbench.Contracts.Purchasing;

namespace Workbench.Application.Purchasing;

public static class SourcingRegistration
{
    /// <summary>
    /// RFQs and quotes, the supplier portal (vendor-facing service, sign-in, buyers' admin) and EDI (X12 over a pluggable
    /// transport, 850s sent when orders are issued). Binds <c>Purchasing:Sourcing</c> and <c>Edi</c>. Call after the
    /// purchasing, ceilings and materials registrations (it decorates <see cref="IPurchaseOrderService"/>) and above
    /// AddWorkbenchIdentity, which adds the permission decorators (<c>AddWorkbenchSourcingPermissions</c>). The web host
    /// replaces <see cref="ISupplierPrincipal"/> with the portal's sign-in.
    /// </summary>
    public static IServiceCollection AddWorkbenchSourcing(this IServiceCollection services)
    {
        services.AddOptions<SourcingOptions>().BindConfiguration(SourcingOptions.Section);
        services.AddOptions<EdiOptions>().BindConfiguration(EdiOptions.Section);
        services.TryAddScoped<FileStorage>();
        services.TryAddScoped<ISupplierPrincipal, NoSupplierPrincipal>();
        services.AddScoped<SupplierCaller>();
        services.AddScoped<SupplierPortalMail>();
        services.AddScoped<SupplierPortalLog>();
        services.AddScoped<SupplierSignIn>();
        services.AddScoped<SupplierDocuments>();
        services.AddScoped<IRfqService, RfqService>();
        services.AddScoped<ISupplierPortalService, SupplierPortalService>();
        services.AddScoped<ISupplierPortalAdminService, SupplierPortalAdminService>();
        services.AddScoped<IEdiTransport, FileDropEdiTransport>();
        services.AddScoped<IEdiTransport, SftpEdiTransport>();
        services.AddScoped<EdiService>();
        services.AddScoped<IEdiService>(sp => sp.GetRequiredService<EdiService>());
        services.Decorate<IPurchaseOrderService, EdiSendingPurchaseOrderService>();
        return services;
    }
}

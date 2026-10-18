using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Files;
using Workbench.Contracts.Purchasing;

namespace Workbench.Application.Purchasing;

public static class PurchasingRegistration
{
    /// <summary>
    /// Vendors, subcontracts, AP bills, payments and AP reports, plus requisitions, purchase orders, receiving, the three-way
    /// match and commitments. Binds <see cref="PurchasingOptions"/> from config
    /// <c>Purchasing</c>. Call after AddWorkbenchAccounting (bills post through its engine) and before AddWorkbenchIdentity,
    /// which adds the permission decorators (<c>AddWorkbenchPurchasingPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchPurchasing(this IServiceCollection services)
    {
        services.AddOptions<PurchasingOptions>().BindConfiguration(PurchasingOptions.Section);

        // Vendor TINs are encrypted with the host's Data Protection keys (idempotent: the web host registers it too).
        services.AddDataProtection();
        services.AddSingleton<TaxIdProtector>();
        services.TryAddScoped<FileStorage>();
        services.AddScoped<IVendorService, VendorService>();
        services.AddScoped<ISubcontractService, SubcontractService>();
        services.AddScoped<IApBillService, ApBillService>();
        services.AddScoped<IApPaymentService, ApPaymentService>();
        services.AddScoped<IApReportService, ApReportService>();

        // Procure-to-pay: requisitions, purchase orders and receiving, the three-way match, commitments.
        services.AddScoped<RequisitionAccess>();
        services.AddScoped<IRequisitionService, RequisitionService>();
        services.AddScoped<IPurchaseOrderService, PurchaseOrderService>();
        services.AddScoped<IThreeWayMatchService, ThreeWayMatchService>();
        services.AddScoped<ICommitmentService, CommitmentService>();
        return services;
    }
}

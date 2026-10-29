using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Contracts.Billing;

namespace Workbench.Application.Billing;

public static class BillingRegistration
{
    /// <summary>
    /// Billing and accounts receivable. Call after AddWorkbenchAccounting (it posts through the general ledger) and before
    /// AddWorkbenchIdentity, which adds the Finance / project-manager checks (<c>AddWorkbenchBillingPermissions</c>).
    /// Cost-plus billing uses an <see cref="IBurdenedCostService"/> when the indirect-rates module registers one, and falls
    /// back to direct cost otherwise.
    /// </summary>
    public static IServiceCollection AddWorkbenchBilling(this IServiceCollection services)
    {
        services.AddOptions<BillingOptions>().BindConfiguration("Billing");
        services.AddScoped<BillingCostSource>();
        services.AddScoped<InvoiceGenerator>();
        services.AddScoped<BillingLedger>();
        services.AddScoped<InvoiceDocuments>();
        services.AddScoped<UnbilledAccruals>();
        services.AddScoped<IPeriodCloseCheck, UnbilledAccrualCloseCheck>();
        services.AddScoped<OverdueInvoiceCheck>();
        services.AddScoped<IInvoiceService, InvoiceService>();
        services.AddScoped<ICustomerShareLinkService, CustomerShareLinkService>();
        services.AddScoped<ICustomerViewService, CustomerViewService>();
        services.AddScoped<IBillingMilestoneService, BillingMilestoneService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IArReportService, ArReportService>();
        services.AddScoped<IProgressPaymentService, ProgressPaymentService>();
        services.AddScoped<IPerformanceBasedPaymentService, PerformanceBasedPaymentService>();
        services.AddScoped<IPieeSubmitter, ManualPieeSubmitter>(); // Register a transmitting submitter after this one to replace it.
        services.AddScoped<IGovernmentBillingService, GovernmentBillingService>();
        services.AddHostedService<OverdueInvoiceBackgroundService>();
        return services;
    }
}

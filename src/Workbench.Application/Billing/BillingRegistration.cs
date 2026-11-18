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
        services.AddScoped<IBillingMilestoneService, BillingMilestoneService>();
        services.AddScoped<IPaymentService, PaymentService>();
        services.AddScoped<IArReportService, ArReportService>();
        // Commercial billing: agreements without CLINs, customer credits and write-offs, reminders, the organization's invoice settings.
        // The concrete types are registered too: the daily run and the milestone drafts call them without a signed-in user's permission check.
        services.AddScoped<InvoiceService>();
        services.AddScoped<AgreementBillingService>();
        services.AddScoped<IAgreementBillingService, AgreementBillingService>();
        services.AddScoped<DueSoonInvoiceCheck>();
        services.AddScoped<IReceivablesService, ReceivablesService>();
        services.AddScoped<IBillingSettingsService, BillingSettingsService>();
        services.AddHostedService<AgreementBillingBackgroundService>();
        // Contract financing (government progress and performance-based payments) is the government contracting module's: it registers an
        // IInvoiceFinancing after this one. Without it, invoicing sees none.
        services.AddScoped<IInvoiceFinancing>(_ => NoInvoiceFinancing.Instance);
        services.AddHostedService<OverdueInvoiceBackgroundService>();
        return services;
    }
}

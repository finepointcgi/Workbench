using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Billing;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;
using Workbench.Contracts.Odcs;
using Workbench.Contracts.Purchasing;
using Workbench.Contracts.Time;

namespace Workbench.Application.Ceilings;

public static class CeilingsRegistration
{
    /// <summary>
    /// Ceilings, overrides, rate caps and closeout. Decorates timesheets, ODCs, expense reports, AP bills, purchase orders and
    /// invoices with the closed and ceiling checks, so it comes after those modules (and per diem, leave) and above
    /// AddWorkbenchIdentity, whose permission decorators wrap these.
    /// </summary>
    public static IServiceCollection AddWorkbenchCeilings(this IServiceCollection services)
    {
        services.AddScoped<ChargeGuard>();
        services.AddScoped<ICeilingService, CeilingService>();
        services.AddScoped<ICloseoutService, CloseoutService>();
        services.AddScoped<IBillingLimits, RateCeilingBillingLimits>();
        services.Decorate<ITimesheetService, CeilingCheckingTimesheetService>();
        services.Decorate<IOdcService, CeilingCheckingOdcService>();
        services.Decorate<IExpenseReportService, CeilingCheckingExpenseReportService>();
        services.Decorate<IApBillService, CeilingCheckingApBillService>();
        services.Decorate<IPurchaseOrderService, CeilingCheckingPurchaseOrderService>();
        services.Decorate<IInvoiceService, CeilingCheckingInvoiceService>();
        return services;
    }
}

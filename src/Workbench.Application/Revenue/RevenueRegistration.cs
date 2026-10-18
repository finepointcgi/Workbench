using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Contracts.Revenue;

namespace Workbench.Application.Revenue;

public static class RevenueRegistration
{
    /// <summary>
    /// Revenue recognition: setup, estimates at completion, the monthly revenue run and reports, plus the "revenue
    /// recognized" period-close check. Reads cost through billing's <c>BillingCostSource</c> (burdened when indirect rates
    /// are registered), so call after AddWorkbenchBilling and before AddWorkbenchIdentity, which adds the permission
    /// decorators (<c>AddWorkbenchRevenuePermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchRevenue(this IServiceCollection services)
    {
        services.AddScoped<RevenueEngine>();
        services.AddScoped<IRevenueSetupService, RevenueSetupService>();
        services.AddScoped<IRevenueRunService, RevenueRunService>();
        services.AddScoped<IRevenueReportService, RevenueReportService>();
        services.AddScoped<IPeriodCloseCheck, RevenueCloseCheck>();
        return services;
    }
}

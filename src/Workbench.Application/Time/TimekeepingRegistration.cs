using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Time;

namespace Workbench.Application.Time;

public static class TimekeepingRegistration
{
    public static IServiceCollection AddWorkbenchTimekeeping(this IServiceCollection services)
    {
        services.AddOptions<TimekeepingOptions>().BindConfiguration("Timekeeping");
        services.AddSingleton<TimekeepingClock>();

        // Defaults that the auth and notifications work can replace by registering their own implementation.
        services.TryAddSingleton<ITimekeepingRoles, ConfiguredTimekeepingRoles>();
        services.TryAddScoped<ITimesheetNotifier, NoOpTimesheetNotifier>();

        services.AddScoped<ITimesheetService, TimesheetService>();
        services.AddScoped<ITimesheetApprovalService, TimesheetApprovalService>();
        services.AddScoped<ITimeReportService, TimeReportService>();
        return services;
    }
}

using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Time;

namespace Workbench.Application.Time;

public static class TimekeepingRegistration
{
    public static IServiceCollection AddWorkbenchTimekeeping(this IServiceCollection services)
    {
        services.AddOptions<TimekeepingOptions>().BindConfiguration("Timekeeping");
        services.AddSingleton<TimekeepingClock>();

        services.AddScoped<ITimesheetNotifier, PublishingTimesheetNotifier>();
        services.AddScoped<MissingTimesheetCheck>();

        services.AddScoped<ITimesheetService, TimesheetService>();
        services.AddScoped<ITimesheetApprovalService, TimesheetApprovalService>();
        services.AddScoped<ITimesheetTimerService, TimesheetTimerService>();
        services.AddScoped<ITimeReportService, TimeReportService>();
        services.AddScoped<IOrganizationSettingsService, OrganizationSettingsService>();
        return services;
    }

    /// <summary>For the server host: the daily "timesheet missing for yesterday" reminder.</summary>
    public static IServiceCollection AddWorkbenchTimekeepingHosting(this IServiceCollection services)
    {
        services.AddHostedService<MissingTimesheetBackgroundService>();
        return services;
    }
}

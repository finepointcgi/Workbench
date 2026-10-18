using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Reports;

namespace Workbench.Application.Reports;

public static class ReportsRegistration
{
    /// <summary>
    /// Standard reports, the report writer, saved reports and schedules. Their permission decorators are added by
    /// <c>AddWorkbenchIdentity</c> (<see cref="Identity.ReportPermissionRegistration"/>).
    /// </summary>
    public static IServiceCollection AddWorkbenchReports(this IServiceCollection services)
    {
        services.AddScoped<ReportAccess>();
        services.AddScoped<IStandardReportService, StandardReportService>();
        services.AddScoped<IReportWriterService, ReportWriterService>();
        services.AddScoped<ReportDeliverer>();
        services.AddScoped<SavedReportService>();
        services.AddScoped<ISavedReportService>(sp => sp.GetRequiredService<SavedReportService>());
        services.AddSingleton<ReportScheduleRunner>();
        return services;
    }

    /// <summary>
    /// For the server host, after its <see cref="ICurrentUser"/> (and the other current-user decorators) are registered:
    /// lets scheduled reports run as their owner and starts the schedule check.
    /// </summary>
    public static IServiceCollection AddWorkbenchReportsHosting(this IServiceCollection services)
    {
        services.Decorate<ICurrentUser, RunAsAwareCurrentUser>();
        services.AddHostedService<ReportScheduleBackgroundService>();
        return services;
    }
}

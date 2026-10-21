using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.TotalTime;

namespace Workbench.Application.Identity;

// The labor cost method and the total time reports (rates, salaries, labor cost) are Finance's (Finance or Admin). A
// timesheet's standard vs worked hours are for whoever can see the timesheet; the service checks that through ITimesheetService.

public static class TotalTimePermissionRegistration
{
    /// <summary>Called from <see cref="IdentityRegistration.AddWorkbenchIdentity"/> so these wrap everything else.</summary>
    public static IServiceCollection AddWorkbenchTotalTimePermissions(this IServiceCollection services)
    {
        services.Decorate<ITotalTimeService, PermissionCheckingTotalTimeService>();
        return services;
    }
}

public sealed class PermissionCheckingTotalTimeService(ITotalTimeService inner, AccessControl access) : ITotalTimeService
{
    public async Task<LaborCostSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.TimeTotalTime, "view the labor cost method", cancellationToken);
        return await inner.GetSettingsAsync(cancellationToken);
    }

    public async Task<LaborCostSettingsDto> SetMethodAsync(SetLaborCostMethodRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.TimeTotalTime, "change the labor cost method", cancellationToken);
        return await inner.SetMethodAsync(request, cancellationToken);
    }

    public Task<TimesheetTotalTimeDto> GetTimesheetSummaryAsync(Guid timesheetId, CancellationToken cancellationToken = default) =>
        inner.GetTimesheetSummaryAsync(timesheetId, cancellationToken);

    public async Task<TotalTimeReportDto> GetReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.TimeTotalTime, "view uncompensated overtime and effective labor rates", cancellationToken);
        return await inner.GetReportAsync(from, to, cancellationToken);
    }
}

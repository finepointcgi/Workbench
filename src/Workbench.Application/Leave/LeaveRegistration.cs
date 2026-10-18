using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;

namespace Workbench.Application.Leave;

public static class LeaveRegistration
{
    /// <summary>
    /// Leave policies, balances, requests and holidays. Decorates <see cref="ITimesheetService"/> (leave balance checks),
    /// so it must come after AddWorkbenchTimekeeping and above AddWorkbenchIdentity. Permission checks are inside the
    /// services: they depend on who the employee's supervisor is.
    /// </summary>
    public static IServiceCollection AddWorkbenchLeave(this IServiceCollection services)
    {
        services.AddScoped<ILeavePolicyService, LeavePolicyService>();
        services.AddScoped<IHolidayService, HolidayService>();
        services.AddScoped<ILeaveBalanceService, LeaveBalanceService>();
        services.AddScoped<ILeaveRequestService, LeaveRequestService>();
        services.AddScoped<ILeaveTimesheetService, LeaveTimesheetService>();
        services.Decorate<ITimesheetService, LeaveCheckingTimesheetService>();
        return services;
    }
}

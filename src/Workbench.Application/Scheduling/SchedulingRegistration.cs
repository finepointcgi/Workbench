using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Scheduling;
using Workbench.Contracts.Scheduling;

namespace Workbench.Application;

public static class SchedulingRegistration
{
    /// <summary>Dependencies, critical-path analysis, baselines and the plan editor (outline, durations, Excel import/export).</summary>
    public static IServiceCollection AddWorkbenchScheduling(this IServiceCollection services)
    {
        services.AddScoped<IDependencyService, DependencyService>();
        services.AddScoped<IBaselineService, BaselineService>();
        services.AddScoped<IPlanScheduleService, PlanScheduleService>();
        services.AddScoped<IScheduleHealthService, ScheduleHealthService>();
        return services;
    }
}

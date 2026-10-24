using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Resourcing;

namespace Workbench.Application.Resourcing;

public static class ResourcingRegistration
{
    /// <summary>
    /// Allocations, capacity and utilization, and the allocation-based ETC. Binds <see cref="ResourcingOptions"/> from config
    /// <c>Resourcing</c>. Holidays and leave come from <see cref="IResourceLeaveCalendar"/>: the no-op default is TryAdd'ed,
    /// so the leave module's registration wins wherever it's made.
    /// </summary>
    public static IServiceCollection AddWorkbenchResourcing(this IServiceCollection services)
    {
        services.AddOptions<ResourcingOptions>().BindConfiguration(ResourcingOptions.Section);
        services.TryAddScoped<IResourceLeaveCalendar, NoLeaveCalendar>();
        services.AddScoped<ResourcingAccess>();
        services.AddScoped<IResourceAllocationService, ResourceAllocationService>();
        services.AddScoped<IResourcePlanningService, ResourcePlanningService>();
        services.AddScoped<IAllocationForecastService, AllocationForecastService>();
        services.AddScoped<ProjectStaffingService>();
        services.AddScoped<IProjectStaffingService>(sp => sp.GetRequiredService<ProjectStaffingService>());
        services.AddScoped<StaffingAssignmentService>();
        services.AddScoped<IStaffingAssignmentService>(sp => sp.GetRequiredService<StaffingAssignmentService>());
        services.AddScoped<IResourcePlanService, ResourcePlanService>();
        services.AddScoped<MyTodo.IMyTodoProvider, StaffingTodoProvider>();
        return services;
    }
}

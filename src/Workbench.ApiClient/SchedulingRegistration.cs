using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Scheduling;

namespace Workbench.ApiClient;

public static class SchedulingRegistration
{
    public static IServiceCollection AddWorkbenchSchedulingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IDependencyService, HttpDependencyService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IBaselineService, HttpBaselineService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IPlanScheduleService, HttpPlanScheduleService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

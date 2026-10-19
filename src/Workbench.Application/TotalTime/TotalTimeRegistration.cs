using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.TotalTime;

namespace Workbench.Application.TotalTime;

public static class TotalTimeRegistration
{
    /// <summary>Total time accounting: the labor cost method and its reports. Above AddWorkbenchIdentity (its permission decorator).</summary>
    public static IServiceCollection AddWorkbenchTotalTime(this IServiceCollection services)
    {
        services.AddScoped<ITotalTimeService, TotalTimeService>();
        return services;
    }
}

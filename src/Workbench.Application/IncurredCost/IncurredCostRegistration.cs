using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.IncurredCost;

namespace Workbench.Application.IncurredCost;

public static class IncurredCostRegistration
{
    /// <summary>
    /// The annual incurred cost submission (ICE schedules). Reads the GL, indirect pools, contracts, billing and AP; posts
    /// nothing. Above AddWorkbenchIdentity, which adds the Finance-only decorator (<c>AddWorkbenchIncurredCostPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchIncurredCost(this IServiceCollection services)
    {
        services.AddScoped<IIncurredCostService, IncurredCostService>();
        return services;
    }
}

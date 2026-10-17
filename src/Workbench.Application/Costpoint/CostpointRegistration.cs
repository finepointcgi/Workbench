using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Contracts.Costpoint;
using Workbench.Costpoint;

namespace Workbench.Application.Costpoint;

public static class CostpointRegistration
{
    /// <summary>
    /// Registers the sync and <see cref="ICostpointService"/>. The adapter itself comes from
    /// <c>AddCostpointSource(configuration)</c> in Workbench.Costpoint; without it the sync uses <see cref="NullCostpointSource"/>.
    /// </summary>
    public static IServiceCollection AddWorkbenchCostpoint(this IServiceCollection services)
    {
        services.TryAddSingleton<ICostpointSource, NullCostpointSource>();
        services.AddSingleton<CostpointSyncService>();
        services.AddScoped<ICostpointService, CostpointService>();
        return services;
    }
}

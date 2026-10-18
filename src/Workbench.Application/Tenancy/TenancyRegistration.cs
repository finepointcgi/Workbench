using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workbench.Application.Tenancy;

public static class TenancyRegistration
{
    /// <summary>SingleTenant defaults. The infrastructure replaces them from the <c>Tenancy</c> configuration section.</summary>
    public static IServiceCollection AddWorkbenchTenancy(this IServiceCollection services)
    {
        services.TryAddSingleton(new TenancyOptions());
        services.TryAddSingleton<ITenantContext, AmbientTenantContext>();
        services.TryAddSingleton<ITenantJobRunner, SingleTenantJobRunner>();
        return services;
    }
}

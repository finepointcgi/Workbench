using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Application.ProjectAccounting;

public static class SetupRegistration
{
    /// <summary>Services behind the /setup admin screens. Needs <see cref="ProjectAccountingRegistration.AddWorkbenchProjectAccounting"/>.</summary>
    public static IServiceCollection AddWorkbenchSetup(this IServiceCollection services)
    {
        services.AddScoped<IPlanSyncService, PlanSyncService>();
        services.AddScoped<IProjectStartService, ProjectStartService>();
        services.AddScoped<ISetupAdminService, SetupAdminService>();
        return services;
    }
}

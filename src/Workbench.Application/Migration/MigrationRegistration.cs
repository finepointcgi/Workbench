using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Migration;

namespace Workbench.Application.Migration;

public static class MigrationRegistration
{
    /// <summary>
    /// The go-live checklist and the parallel-run reconciliation. Reads the import history and Workbench's reports, so
    /// call it after AddWorkbenchImports and the accounting modules, above AddWorkbenchIdentity (whose
    /// <c>AddWorkbenchMigrationPermissions</c> makes both Finance/Admin only).
    /// </summary>
    public static IServiceCollection AddWorkbenchMigration(this IServiceCollection services)
    {
        services.AddScoped<ReconciliationSources>();
        services.AddScoped<IReconciliationService, ReconciliationService>();
        services.AddScoped<IMigrationService, MigrationService>();
        services.AddScoped<ParallelRunExplainer>();
        services.AddScoped<IParallelRunService, ParallelRunService>();
        return services;
    }
}

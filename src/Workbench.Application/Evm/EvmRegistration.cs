using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Collaboration;
using Workbench.Application.Security;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Security;

namespace Workbench.Application.Evm;

public static class EvmRegistration
{
    /// <summary>
    /// Earned value management. Actual cost comes through billing's <c>BillingCostSource</c> (the indirect-rates
    /// <c>IBurdenedCostService</c> at provisional rates, or direct cost when none is registered). Exports are recorded in the
    /// security event log. Register above <c>AddWorkbenchIdentity</c>, whose decorators add the permission checks.
    /// </summary>
    public static IServiceCollection AddWorkbenchEvm(this IServiceCollection services)
    {
        services.AddScoped<IEvmService, EvmService>();
        services.AddScoped<IEvmChangeRequestService, EvmChangeRequestService>();
        services.AddScoped<IEvmExportService, EvmExportService>();
        services.Decorate<IEvmExportService, SecurityLoggingEvmExportService>();
        return services;
    }
}

/// <summary>Records each IPMDAR export (registered inside the permission check, so only allowed exports are recorded).</summary>
public sealed class SecurityLoggingEvmExportService(IEvmExportService inner, SecurityEventLog log) : IEvmExportService
{
    public async Task<EvmExportFile> ExportFormat1Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportFormat1Async(projectId, fiscalPeriodId, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportEvm, file.FileName, detail: $"IPMDAR Format 1, project {projectId}", cancellationToken: cancellationToken);
        return file;
    }

    public async Task<EvmExportFile> ExportFormat5Async(Guid projectId, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var file = await inner.ExportFormat5Async(projectId, fiscalPeriodId, cancellationToken);
        await log.RecordAsync(SecurityEventTypes.ExportEvm, file.FileName, detail: $"IPMDAR Format 5, project {projectId}", cancellationToken: cancellationToken);
        return file;
    }
}

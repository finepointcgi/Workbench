using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Approvals;
using Workbench.Application.Files;
using Workbench.Application.Identity;
using Workbench.Contracts.Quality;

namespace Workbench.Application.Quality;

public static class QualityRegistration
{
    /// <summary>
    /// Quality (inspection plans and queue, NCRs and the material review board, corrective actions, supplier quality) and
    /// engineering change control, with the "NCR disposition" and "Engineering change" inbox handlers. Binds
    /// <see cref="QualityOptions"/> from config <c>Quality</c>. After AddWorkbenchMaterials; above AddWorkbenchIdentity (which
    /// adds <c>AddWorkbenchQualityPermissions</c>).
    /// </summary>
    public static IServiceCollection AddWorkbenchQuality(this IServiceCollection services)
    {
        services.AddOptions<QualityOptions>().BindConfiguration(QualityOptions.Section);
        services.TryAddScoped<FileStorage>();
        services.AddScoped<IInspectionService, InspectionService>();
        services.AddScoped<INonconformanceService, NonconformanceService>();
        services.AddScoped<ICorrectiveActionService, CorrectiveActionService>();
        services.AddScoped<IQualityFileService, QualityFileService>();
        services.AddScoped<EngineeringChangeService>();
        services.AddScoped<IEngineeringChangeService>(sp => sp.GetRequiredService<EngineeringChangeService>());
        services.AddScoped<IApprovalDocumentHandler, NcrDispositionApprovalHandler>();
        services.AddScoped<IApprovalDocumentHandler, EngineeringChangeApprovalHandler>();
        return services;
    }

    /// <summary>For the server host: makes approved engineering changes effective on their date, hourly.</summary>
    public static IServiceCollection AddWorkbenchQualityHosting(this IServiceCollection services)
    {
        services.AddHostedService<EngineeringChangeBackgroundService>();
        return services;
    }
}

/// <summary>Hourly, per tenant: approved engineering changes whose effective date has come become effective.</summary>
public sealed class EngineeringChangeBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<EngineeringChangeBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = SystemExecution.CreateScope(scopes);
                    var count = await scope.ServiceProvider.GetRequiredService<EngineeringChangeService>().ApplyDueAsync(null, ct);
                    if (count > 0)
                    {
                        logger.LogInformation("Engineering changes made effective: {Count}", count);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Applying due engineering changes failed.");
            }
        }
        while (await NextTickAsync(timer, stoppingToken));
    }

    /// <summary>False when the host stops (the timer is cancelled or disposed during shutdown).</summary>
    private static async Task<bool> NextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}

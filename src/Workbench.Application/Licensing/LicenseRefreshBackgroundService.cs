using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;

namespace Workbench.Application.Licensing;

/// <summary>
/// Keeps a connected install's license key current: at startup and then on the refresh interval, in every tenant, it asks the
/// license server for a fresh key (see <see cref="LicenseRefreshService"/>). With <c>License:ServerUrl</c> empty (the default,
/// for air-gapped and defense installs) it does nothing and never touches the network. A failure never changes entitlements.
/// </summary>
public sealed class LicenseRefreshBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, LicenseOptions options, TimeProvider clock, ILogger<LicenseRefreshBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(options.ServerUrl))
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = TimeSpan.FromHours(Math.Max(1, options.RefreshIntervalHours));
            try
            {
                wait = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "License refresh failed");
            }

            try
            {
                await Task.Delay(wait < TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : wait, clock, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>One pass over every tenant; returns the shortest wait any of them asked for.</summary>
    internal async Task<TimeSpan> RunOnceAsync(CancellationToken ct)
    {
        var shortest = TimeSpan.MaxValue;
        var gate = new object();
        await tenants.ForEachTenantAsync(async tenantCt =>
        {
            await using var scope = SystemExecution.CreateScope(scopes);
            var wait = await scope.ServiceProvider.GetRequiredService<LicenseRefreshService>().RunIfDueAsync(tenantCt);
            lock (gate)
            {
                shortest = wait < shortest ? wait : shortest;
            }
        }, ct);
        return shortest == TimeSpan.MaxValue ? TimeSpan.FromHours(Math.Max(1, options.RefreshIntervalHours)) : shortest;
    }
}

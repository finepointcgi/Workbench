using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Identity;

namespace Workbench.Application.Ceilings;

/// <summary>
/// Hourly, in every tenant: evaluates every active ceiling and sends the threshold alerts not sent yet (once per ceiling,
/// measure, threshold and ceiling amount, like the charge-time path). It catches what enforcement points don't see:
/// timesheet approvals, imports and postings. Runs as the read-only Workbench System context; it reads consumption straight
/// from the ceiling engine and writes only its own alert records.
/// </summary>
public sealed class CeilingAlertBackgroundService(IServiceScopeFactory scopes, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<CeilingAlertBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(RunOnceAsync, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Ceiling alert check failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>One tenant's check (the current tenant scope).</summary>
    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        await using var scope = SystemExecution.CreateScope(scopes);
        var sent = await scope.ServiceProvider.GetRequiredService<ChargeGuard>().AlertAllAsync(ct);
        if (sent > 0)
        {
            logger.LogInformation("Ceiling alert check as {Actor}: sent {Count} alerts", SystemExecutionContext.Actor, sent);
        }

        return sent;
    }
}

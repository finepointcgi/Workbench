using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Automation;

/// <summary>Checks date triggers hourly. Each rule fires once per item and date (see <see cref="RuleEngine.RunDateTriggersAsync"/>).</summary>
public sealed class DateTriggerBackgroundService(IServiceScopeFactory scopes, ITenantJobRunner tenants, TimeProvider clock, ILogger<DateTriggerBackgroundService> logger)
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
                    await using var scope = scopes.CreateAsyncScope();
                    var engine = scope.ServiceProvider.GetRequiredService<RuleEngine>();
                    var fired = await engine.RunDateTriggersAsync(scope.ServiceProvider.GetRequiredService<IItemService>(), ct);
                    if (fired > 0)
                    {
                        logger.LogInformation("Date triggers fired {Count} rules", fired);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Date trigger run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

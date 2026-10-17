using Workbench.Costpoint;

namespace Workbench.Worker;

/// <summary>Pulls Costpoint master data and actuals into the mirror tables on a schedule.</summary>
public sealed class CostpointSyncWorker(ICostpointSource source, IConfiguration configuration, ILogger<CostpointSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = configuration.GetValue("Costpoint:SyncInterval", TimeSpan.FromHours(24));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var projects = await source.GetProjectsAsync(stoppingToken);
                logger.LogInformation("Costpoint sync via {Source}: {Count} projects read", source.Name, projects.Count);
                // TODO: upsert into CpProject / CpWbsElement / CpActual mirror tables.
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Costpoint sync via {Source} failed", source.Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

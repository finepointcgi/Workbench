using Workbench.Application.Costpoint;
using Workbench.Domain.Costpoint;

namespace Workbench.Worker;

/// <summary>Pulls Costpoint master data, budgets and actuals into the mirror tables on a schedule.</summary>
public sealed class CostpointSyncWorker(CostpointSyncService sync, IConfiguration configuration, ILogger<CostpointSyncWorker> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!sync.Configured)
        {
            logger.LogInformation("Costpoint:Source is None; scheduled Costpoint sync is off");
            return;
        }

        var interval = configuration.GetValue("Costpoint:SyncInterval", TimeSpan.FromHours(24));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                var run = await sync.RunAsync(stoppingToken);
                if (run.Status == CpSyncStatus.Succeeded)
                {
                    logger.LogInformation(
                        "Costpoint sync via {Source}: {Projects} projects, {Actuals} actuals; {Inserted} inserted, {Updated} updated, {Deleted} deleted, {Skipped} skipped",
                        run.Source, run.Projects, run.Actuals, run.Inserted, run.Updated, run.Deleted, run.Skipped);
                }
                else
                {
                    logger.LogError("Costpoint sync via {Source} failed: {Error}", run.Source, run.Error);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // RunAsync records its own failures; this catches the database being unreachable.
                logger.LogError(ex, "Costpoint sync via {Source} failed", sync.SourceName);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

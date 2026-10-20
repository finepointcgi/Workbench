using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Collaboration;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations.Connectors;

/// <summary>When a scheduled connector runs next (UTC).</summary>
public static class ConnectorScheduleMath
{
    public static DateTime? Next(ConnectorSchedule schedule, int hourUtc, int day, DateTime after)
    {
        var hour = Math.Clamp(hourUtc, 0, 23);
        var at = after.Date.AddHours(hour);
        switch (schedule)
        {
            case ConnectorSchedule.Hourly:
                return after.Date.AddHours(after.Hour + 1);
            case ConnectorSchedule.Daily:
                return at > after ? at : at.AddDays(1);
            case ConnectorSchedule.Weekly:
            {
                var target = (DayOfWeek)Math.Clamp(day, 0, 6);
                var candidate = at.AddDays(((int)target - (int)at.DayOfWeek + 7) % 7);
                return candidate > after ? candidate : candidate.AddDays(7);
            }

            case ConnectorSchedule.Monthly:
            {
                var dom = Math.Clamp(day, 1, 28);
                var candidate = new DateTime(after.Year, after.Month, dom, hour, 0, 0, DateTimeKind.Utc);
                return candidate > after ? candidate : candidate.AddMonths(1);
            }

            default:
                return null;
        }
    }
}

/// <summary>Runs one connector (scheduled, manual or a test), keeps a copy of any file, and writes the run log.</summary>
public sealed class ConnectorRunner(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IConnector> connectors,
    IntegrationSecrets secrets,
    IBlobStore blobs,
    TimeProvider clock,
    ILogger<ConnectorRunner> logger)
{
    public IConnector? Find(string kind) => connectors.FirstOrDefault(c => c.Kind == kind);

    public ConnectorConfig ConfigOf(IntegrationConnector row) =>
        new(JsonSerializer.Deserialize<Dictionary<string, string>>(row.SettingsJson) ?? [], secrets.UnprotectConnectorSecrets(row.SecretsProtected));

    /// <summary>Runs <paramref name="connectorId"/> now and records the run. Scheduled runs move the next run (with backoff after failures).</summary>
    public async Task<ConnectorRun> RunAsync(Guid connectorId, ConnectorTrigger trigger, string? requestedBy, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<IntegrationConnector>().FirstOrDefaultAsync(c => c.Id == connectorId, ct)
            ?? throw Contracts.WorkbenchNotFoundException.For("Connector", connectorId);
        var started = clock.GetUtcNow().UtcDateTime;
        var run = new ConnectorRun { ConnectorId = row.Id, Trigger = trigger, StartedAtUtc = started, Message = "", RequestedBy = requestedBy };
        ConnectorResult result;
        if (Find(row.Kind) is not { } connector)
        {
            result = new ConnectorResult(false, $"This server doesn't have the {row.Kind} connector.");
        }
        else
        {
            try
            {
                result = await connector.RunAsync(new ConnectorContext(row, ConfigOf(row), trigger, null, started), ct);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or UnauthorizedAccessException or IOException or HttpRequestException)
            {
                result = new ConnectorResult(false, ex.Message);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Connector {Kind} {ConnectorId} failed", row.Kind, row.Id);
                result = new ConnectorResult(false, "The connector failed unexpectedly. The server log has the details.");
            }
        }

        run.Status = result.Success ? ConnectorRunStatus.Succeeded : ConnectorRunStatus.Failed;
        run.Message = Clip(result.Message, 1000);
        run.ItemCount = result.ItemCount;
        run.FinishedAtUtc = clock.GetUtcNow().UtcDateTime;
        if (result.File is { } file)
        {
            run.OutputFileName = Clip(file.FileName, 260);
            run.OutputBytes = file.Content.LongLength;
            run.OutputBlobKey = $"integrations/connectors/{row.Id:N}/{run.Id:N}";
            await blobs.SaveAsync(run.OutputBlobKey, new MemoryStream(file.Content), ct);
        }

        db.Set<ConnectorRun>().Add(run);
        Apply(row, run, trigger, started);
        await db.SaveChangesAsync(ct);
        return run;
    }

    /// <summary>Updates the connector's status from a run; a failed scheduled run is retried with backoff before its next slot.</summary>
    internal static void Apply(IntegrationConnector row, ConnectorRun run, ConnectorTrigger trigger, DateTime now)
    {
        row.LastRunAtUtc = run.FinishedAtUtc;
        row.LastStatus = run.Status == ConnectorRunStatus.Succeeded ? run.Message : $"Failed: {run.Message}";
        row.LastStatus = Clip(row.LastStatus, 1000);
        row.ConsecutiveFailures = run.Status == ConnectorRunStatus.Failed ? row.ConsecutiveFailures + 1 : 0;
        if (trigger == ConnectorTrigger.Schedule)
        {
            var next = ConnectorScheduleMath.Next(row.Schedule, row.RunAtHourUtc, row.RunOnDay, now);
            if (run.Status == ConnectorRunStatus.Failed && row.ConsecutiveFailures < 6 && next is { } slot)
            {
                var retry = now + RetryPolicy.DelayAfter(row.ConsecutiveFailures, row.Id);
                next = retry < slot ? retry : slot;
            }

            row.NextRunAtUtc = next;
        }
    }

    internal static string Clip(string text, int max) => text.Length <= max ? text : text[..max];
}

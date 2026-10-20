using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Collaboration;
using Workbench.Application.Integrations.Connectors;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations;

/// <summary>Prebuilt connectors and their run logs (Admin › Integrations › Connectors). Administrators only (the permission decorator).</summary>
public sealed class ConnectorService(
    IWorkbenchDbContextFactory dbFactory,
    ConnectorRunner runner,
    IEnumerable<IConnector> connectors,
    IntegrationSecrets secrets,
    IBlobStore blobs,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    TimeProvider clock) : IConnectorService
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<ConnectorKindDto>> ListKindsAsync(CancellationToken cancellationToken = default)
    {
        var kinds = new List<ConnectorKindDto>();
        foreach (var c in connectors)
        {
            kinds.Add(new ConnectorKindDto(c.Kind, c.Name, c.Description, c.Mode, await c.GetFieldsAsync(cancellationToken)));
        }

        return kinds;
    }

    public async Task<IReadOnlyList<ConnectorDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await db.Set<IntegrationConnector>().AsNoTracking().OrderBy(c => c.Name).ToListAsync(cancellationToken)).Select(ToDto).ToList();
    }

    public async Task<ConnectorDto> GetAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await FindAsync(db, connectorId, cancellationToken));
    }

    public async Task<ConnectorDto> CreateAsync(SaveConnectorRequest request, CancellationToken cancellationToken = default)
    {
        var connector = Kind(request.Kind);
        var now = Now;
        var row = new IntegrationConnector { Kind = connector.Kind, Name = "", SettingsJson = "{}", CreatedBy = Me, CreatedAtUtc = now, UpdatedAtUtc = now };
        await ApplyAsync(row, connector, request, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        db.Set<IntegrationConnector>().Add(row);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{connector.Name}: {row.Name}", detail: "Created", cancellationToken: cancellationToken);
        return ToDto(row);
    }

    public async Task<ConnectorDto> UpdateAsync(Guid connectorId, SaveConnectorRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await FindAsync(db, connectorId, cancellationToken);
        if (!string.Equals(request.Kind, row.Kind, StringComparison.Ordinal))
        {
            throw new ArgumentException("A connector's kind can't change; add a new connector instead.");
        }

        var connector = Kind(row.Kind);
        await ApplyAsync(row, connector, request, cancellationToken);

        // Saving makes the saver the account scheduled runs act as (so a deactivated owner's connector can be taken over).
        row.CreatedBy = Me;
        row.UpdatedBy = Me;
        row.UpdatedAtUtc = Now;
        row.Version = Guid.NewGuid();
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{connector.Name}: {row.Name}",
            detail: $"Updated; {(row.IsActive ? "on" : "off")}; {row.Schedule}", cancellationToken: cancellationToken);
        return ToDto(row);
    }

    public async Task DeleteAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await FindAsync(db, connectorId, cancellationToken);
        var runs = await db.Set<ConnectorRun>().Where(r => r.ConnectorId == connectorId).ToListAsync(cancellationToken);
        var deliveries = await db.Set<IntegrationDelivery>().Include(d => d.Attempts).Where(d => d.ConnectorId == connectorId).ToListAsync(cancellationToken);
        db.Set<ConnectorRun>().RemoveRange(runs);
        db.Set<IntegrationDelivery>().RemoveRange(deliveries);
        db.Set<IntegrationConnector>().Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        foreach (var key in runs.Where(r => r.OutputBlobKey != null).Select(r => r.OutputBlobKey!))
        {
            await blobs.DeleteAsync(key, cancellationToken);
        }

        await securityLog.RecordAsync(SecurityEventTypes.ConnectorChanged, $"{row.Kind}: {row.Name}", detail: "Deleted", cancellationToken: cancellationToken);
    }

    public async Task<ConnectorRunDto> RunNowAsync(Guid connectorId, CancellationToken cancellationToken = default)
    {
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var row = await FindAsync(db, connectorId, cancellationToken);
            if (!row.IsActive)
            {
                throw new ArgumentException("Turn the connector on before running it.");
            }
        }

        var run = await runner.RunAsync(connectorId, ConnectorTrigger.Manual, Me, cancellationToken);
        return ToRunDto(run, null);
    }

    public async Task<IReadOnlyList<ConnectorRunDto>> ListRunsAsync(Guid connectorId, int take = 50, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await FindAsync(db, connectorId, cancellationToken);
        var runs = await db.Set<ConnectorRun>().AsNoTracking().Where(r => r.ConnectorId == connectorId)
            .OrderByDescending(r => r.StartedAtUtc).Take(Math.Clamp(take, 1, 500)).ToListAsync(cancellationToken);
        var eventIds = runs.Where(r => r.EventId != null).Select(r => r.EventId!.Value).Distinct().ToList();
        var types = await db.Set<IntegrationOutboxEvent>().AsNoTracking().Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.EventType, cancellationToken);
        return runs.Select(r => ToRunDto(r, r.EventId is { } id ? types.GetValueOrDefault(id) : null)).ToList();
    }

    public async Task<ConnectorFileDto> GetRunFileAsync(Guid runId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var run = await db.Set<ConnectorRun>().AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken);
        if (run?.OutputBlobKey is not { } key || run.OutputFileName is not { } name)
        {
            throw WorkbenchNotFoundException.For("Run file", runId);
        }

        await using var stream = await blobs.OpenReadAsync(key, cancellationToken);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.ExportReport, name, detail: "Connector run file downloaded", cancellationToken: cancellationToken);
        var type = name.EndsWith(".csv", StringComparison.OrdinalIgnoreCase) ? "text/csv"
            : name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase) ? "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
            : "text/plain";
        return new ConnectorFileDto(name, type, buffer.ToArray());
    }

    private IConnector Kind(string? kind) =>
        connectors.FirstOrDefault(c => c.Kind == kind?.Trim()) ?? throw new ArgumentException($"There's no connector called '{kind}'.");

    private async Task ApplyAsync(IntegrationConnector row, IConnector connector, SaveConnectorRequest request, CancellationToken ct)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ArgumentException("Give the connector a name (200 characters at most).");
        }

        var fields = await connector.GetFieldsAsync(ct);
        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        var stored = secrets.UnprotectConnectorSecrets(row.SecretsProtected);
        var newSecrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var field in fields)
        {
            if (field.Kind == ConnectorFieldKind.Secret)
            {
                var given = request.Secrets?.GetValueOrDefault(field.Key);
                var value = given is null ? stored.GetValueOrDefault(field.Key) : given.Trim();
                if (!string.IsNullOrEmpty(value))
                {
                    newSecrets[field.Key] = value;
                }
            }
            else if (request.Settings?.GetValueOrDefault(field.Key) is { } v && !string.IsNullOrWhiteSpace(v))
            {
                var value = v.Trim();
                if (value.Length > 2000)
                {
                    throw new ArgumentException($"{field.Label} is too long.");
                }

                if (field.Kind == ConnectorFieldKind.Select && field.Options is { Count: > 0 } options && options.All(o => o.Value != value))
                {
                    throw new ArgumentException($"Choose {field.Label.ToLowerInvariant()} from the list.");
                }

                settings[field.Key] = value;
            }
            else if (field.DefaultValue is { } d)
            {
                settings[field.Key] = d;
            }
        }

        if (fields.FirstOrDefault(f => f.Required && !settings.ContainsKey(f.Key) && !newSecrets.ContainsKey(f.Key)) is { } missing)
        {
            throw new ArgumentException($"{missing.Label} is required.");
        }

        var config = new ConnectorConfig(settings, newSecrets);
        await connector.ValidateAsync(config, ct);

        IReadOnlyList<string>? events = null;
        if (connector.Mode == ConnectorMode.Events)
        {
            events = WebhookEventCatalog.Normalize(request.EventTypes);
        }

        if (connector.Mode == ConnectorMode.Schedule && request.Schedule == ConnectorScheduleKind.Hourly)
        {
            throw new ArgumentException("File exports run daily, weekly or monthly.");
        }

        if (request.RunAtHourUtc is < 0 or > 23 || request.RunOnDay is < 0 or > 28)
        {
            throw new ArgumentException("Choose an hour from 0 to 23 and a day from 1 to 28 (0 to 6 for a weekday).");
        }

        row.Name = name;
        row.IsActive = request.IsActive;
        row.SettingsJson = JsonSerializer.Serialize(settings);
        row.SecretsProtected = secrets.ProtectConnectorSecrets(newSecrets);
        row.EventTypes = events is null ? null : string.Join(',', events);
        row.Schedule = connector.Mode == ConnectorMode.Events ? ConnectorSchedule.Manual : (ConnectorSchedule)(int)request.Schedule;
        row.RunAtHourUtc = request.RunAtHourUtc;
        row.RunOnDay = request.RunOnDay;
        row.NextRunAtUtc = row.IsActive ? ConnectorScheduleMath.Next(row.Schedule, row.RunAtHourUtc, row.RunOnDay, Now) : null;
    }

    private static async Task<IntegrationConnector> FindAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<IntegrationConnector>().FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Connector", id);

    private ConnectorDto ToDto(IntegrationConnector c)
    {
        var settings = JsonSerializer.Deserialize<Dictionary<string, string>>(c.SettingsJson) ?? [];
        var secretKeys = secrets.UnprotectConnectorSecrets(c.SecretsProtected).Keys.Order(StringComparer.Ordinal).ToList();
        var kind = connectors.FirstOrDefault(x => x.Kind == c.Kind);
        return new ConnectorDto(c.Id, c.Kind, kind?.Name ?? c.Kind, c.Name, c.IsActive, settings, secretKeys,
            IntegrationDispatcher.Ordered(c.EventTypes), (ConnectorScheduleKind)(int)c.Schedule, c.RunAtHourUtc, c.RunOnDay,
            c.NextRunAtUtc, c.LastRunAtUtc, c.LastStatus, c.CreatedBy, c.CreatedAtUtc);
    }

    private static ConnectorRunDto ToRunDto(ConnectorRun r, string? eventType) => new(
        r.Id, r.ConnectorId, (ConnectorRunTrigger)(int)r.Trigger, (ConnectorRunState)(int)r.Status, r.StartedAtUtc, r.FinishedAtUtc, r.Message, r.ItemCount,
        eventType, r.RequestedBy, r.OutputFileName, r.OutputBytes);
}

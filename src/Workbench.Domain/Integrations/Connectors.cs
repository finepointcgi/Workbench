namespace Workbench.Domain.Integrations;

public enum ConnectorSchedule
{
    /// <summary>Runs only when someone presses "Run now" (or, for chat connectors, when a subscribed event happens).</summary>
    Manual,
    Hourly,
    Daily,
    Weekly,
    Monthly,
}

/// <summary>
/// A configured prebuilt connector (Teams, Slack, GL export, report export). <see cref="Kind"/> picks the code
/// (<c>IConnector.Kind</c>); settings are the connector's own fields, secrets encrypted separately.
/// </summary>
public sealed class IntegrationConnector : Entity
{
    public required string Kind { get; set; }
    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>Non-secret settings as a JSON object of strings.</summary>
    public required string SettingsJson { get; set; }

    /// <summary>Secret settings (incoming webhook URLs, SFTP passwords) as a JSON object, encrypted with Data Protection.</summary>
    public string? SecretsProtected { get; set; }

    /// <summary>For chat connectors: the event type keys it posts, comma-separated.</summary>
    public string? EventTypes { get; set; }

    public ConnectorSchedule Schedule { get; set; }

    /// <summary>Hour of day (UTC) for daily, weekly and monthly runs.</summary>
    public int RunAtHourUtc { get; set; } = 6;

    /// <summary>Weekly: the day of the week; monthly: the day of the month (1-28).</summary>
    public int RunOnDay { get; set; } = 1;

    public DateTime? NextRunAtUtc { get; set; }
    public DateTime? LastRunAtUtc { get; set; }
    public string? LastStatus { get; set; }
    public int ConsecutiveFailures { get; set; }

    /// <summary>Changes whenever a run is claimed, so two dispatchers never start the same scheduled run.</summary>
    public Guid Version { get; set; } = Guid.NewGuid();

    /// <summary>Scheduled runs act as this person (an administrator); they stop if the account is deactivated.</summary>
    public required string CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ConnectorTrigger
{
    Manual,
    Schedule,
    Event,
}

public enum ConnectorRunStatus
{
    Succeeded,
    Failed,
    Skipped,
}

/// <summary>The connector's run log: one row per run or event post.</summary>
public sealed class ConnectorRun : Entity
{
    public Guid ConnectorId { get; set; }
    public ConnectorTrigger Trigger { get; set; }
    public ConnectorRunStatus Status { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime FinishedAtUtc { get; set; }
    public required string Message { get; set; }
    public int ItemCount { get; set; }
    public Guid? EventId { get; set; }
    public string? RequestedBy { get; set; }

    /// <summary>For file exports: the file's name and where Workbench kept a copy (blob key), for download from the log.</summary>
    public string? OutputFileName { get; set; }

    public string? OutputBlobKey { get; set; }
    public long? OutputBytes { get; set; }
}

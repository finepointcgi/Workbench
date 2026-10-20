namespace Workbench.Application.Integrations;

/// <summary>Server-wide settings for webhooks and connectors (config section <c>Integrations</c>; docs/operations/configuration.md).</summary>
public sealed class IntegrationOptions
{
    public const string Section = "Integrations";

    /// <summary>Runs the dispatcher on this host. Turn off on extra web hosts if only one should send (sending is safe on several).</summary>
    public bool DispatcherEnabled { get; set; } = true;

    public int DispatchIntervalSeconds { get; set; } = 15;

    /// <summary>Allows <c>http://</c> targets. Development only; refused in Production by the startup check.</summary>
    public bool AllowHttp { get; set; }

    /// <summary>Allows loopback and private network targets (a receiver on your machine). Development only; refused in Production.</summary>
    public bool AllowPrivateNetworkTargets { get; set; }

    /// <summary>Attempts per delivery before it's marked failed (the first try included).</summary>
    public int MaxAttempts { get; set; } = 12;

    /// <summary>Consecutive failed attempts after which an endpoint is marked unhealthy and only probed hourly.</summary>
    public int UnhealthyAfterFailures { get; set; } = 10;

    public int RequestTimeoutSeconds { get; set; } = 10;

    /// <summary>Days to keep sent events, deliveries and connector runs.</summary>
    public int RetentionDays { get; set; } = 30;
}

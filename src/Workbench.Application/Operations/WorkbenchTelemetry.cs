using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Workbench.Application.Operations;

/// <summary>
/// Workbench's own traces and metrics (meter and activity source "Workbench"), exported over OTLP when telemetry is
/// configured. Tags never carry people, emails, amounts or secrets. Alerts built on these are in
/// docs/operations/alerts.md.
/// </summary>
public static class WorkbenchTelemetry
{
    public const string Name = "Workbench";

    public static readonly ActivitySource ActivitySource = new(Name);
    public static readonly Meter Meter = new(Name);

    /// <summary>Security events by <c>event.type</c> and <c>outcome</c> (failed sign-ins are <c>outcome=failure</c>).</summary>
    public static readonly Counter<long> SecurityEvents = Meter.CreateCounter<long>("workbench.security.events", "{event}", "Security events recorded, by type and outcome.");

    /// <summary>Background job runs by <c>job</c> and <c>outcome</c> (one per tenant in MultiTenant mode).</summary>
    public static readonly Counter<long> JobRuns = Meter.CreateCounter<long>("workbench.jobs.runs", "{run}", "Background job runs, by job and outcome.");

    public static readonly Histogram<double> JobDuration = Meter.CreateHistogram<double>("workbench.jobs.duration", "s", "Background job run time.");

    /// <summary>Emails handed to the provider, by <c>email.provider</c> and <c>outcome</c>.</summary>
    public static readonly Counter<long> EmailsSent = Meter.CreateCounter<long>("workbench.email.sent", "{email}", "Emails sent, by provider and outcome.");
}

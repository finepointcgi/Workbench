using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Workbench.Application.Operations;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Notifications;
using Workbench.Tests.Identity;
using Workbench.Web.Security;

namespace Workbench.Tests.Operations;

public sealed class ObservabilityTests
{
    [Fact]
    public void Redaction_removes_emails_tokens_and_connection_string_secrets()
    {
        var text = LogRedaction.Text("Sent to pm@example.com with Authorization: Bearer eyJhbGciOiJIUzI1NiJ9.abc; Host=db;Password=s3cret!;Username=wb");

        Assert.DoesNotContain("pm@example.com", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", text);
        Assert.DoesNotContain("s3cret", text);
        Assert.Contains("[email]", text);
        Assert.Contains("Host=db", text);
        Assert.True(LogRedaction.IsSecretName("ClientSecret"));
        Assert.True(LogRedaction.IsSecretName("Tin"));
        Assert.True(LogRedaction.IsSecretName("bank_account"));
        Assert.False(LogRedaction.IsSecretName("Timing"));
        Assert.False(LogRedaction.IsSecretName("Tenant"));
    }

    [Fact]
    public void Json_console_logs_are_one_redacted_object_per_line()
    {
        var formatter = new RedactingJsonConsoleFormatter(new StaticOptions(new JsonConsoleFormatterOptions()));
        var state = new List<KeyValuePair<string, object?>>
        {
            new("Email", "analyst@example.com"),
            new("ApiKey", "SG.very-secret"),
            new("Count", 3),
            new("{OriginalFormat}", "Sent {Count} to {Email} with {ApiKey}"),
        };
        using var writer = new StringWriter();

        using (TenantScope.Enter(new TenantInfo(Guid.NewGuid(), "acme", "Acme")))
        {
            formatter.Write(new LogEntry<IReadOnlyList<KeyValuePair<string, object?>>>(LogLevel.Warning, "Workbench.Test", new EventId(7), state, null,
                (_, _) => "Sent 3 to analyst@example.com with SG.very-secret"), null, writer);
        }

        var line = writer.ToString().TrimEnd();
        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain("analyst@example.com", line);
        Assert.DoesNotContain("very-secret", line[line.IndexOf("\"State\"", StringComparison.Ordinal)..]);
        var json = JsonNode.Parse(line)!;
        Assert.Equal("Warning", (string?)json["Level"]);
        Assert.Equal("acme", (string?)json["Tenant"]);
        Assert.Equal("[redacted]", (string?)json["State"]!["ApiKey"]);
        Assert.Equal("[email]", (string?)json["State"]!["Email"]);
        Assert.Equal(3, (int)json["State"]!["Count"]!);
    }

    [Fact]
    public async Task Background_job_runs_and_failures_are_counted_by_job()
    {
        var services = new ServiceCollection();
        services.AddSingleton<ITenantJobRunner, SingleTenantJobRunner>();
        services.AddWorkbenchJobInstrumentation();
        var runner = services.BuildServiceProvider().GetRequiredService<ITenantJobRunner>();
        var measurements = new List<(long Value, string Job, string Outcome)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorkbenchTelemetry.Name && instrument.Name == "workbench.jobs.runs")
            {
                l.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
        {
            var all = tags.ToArray().ToDictionary(t => t.Key, t => t.Value?.ToString());
            if (all["job"] == nameof(ObservabilityTests))
            {
                lock (measurements)
                {
                    measurements.Add((value, all["job"]!, all["outcome"]!));
                }
            }
        });
        listener.Start();

        await runner.ForEachTenantAsync(_ => Task.CompletedTask, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.ForEachTenantAsync(_ => throw new InvalidOperationException("boom"), CancellationToken.None));

        Assert.Contains((1L, nameof(ObservabilityTests), "success"), measurements);
        Assert.Contains((1L, nameof(ObservabilityTests), "failure"), measurements);
    }

    [Fact]
    public async Task Email_is_degraded_after_a_failed_delivery_or_when_production_writes_to_a_folder()
    {
        var status = new EmailDeliveryStatus();
        var production = new HostingEnvironment { EnvironmentName = "Production" };
        var sendGrid = new Observability.EmailHealthCheck(new EmailSettings { Mode = "SendGrid" }, status, production, TimeProvider.System);

        Assert.Equal(HealthStatus.Healthy, (await sendGrid.CheckHealthAsync(new HealthCheckContext())).Status);
        status.Failed(DateTimeOffset.UtcNow);
        Assert.Equal(HealthStatus.Degraded, (await sendGrid.CheckHealthAsync(new HealthCheckContext())).Status);
        status.Succeeded(DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Equal(HealthStatus.Healthy, (await sendGrid.CheckHealthAsync(new HealthCheckContext())).Status);

        var pickup = new Observability.EmailHealthCheck(new EmailSettings { Mode = "Pickup" }, new EmailDeliveryStatus(), production, TimeProvider.System);
        Assert.Equal(HealthStatus.Degraded, (await pickup.CheckHealthAsync(new HealthCheckContext())).Status);

        var closedPort = new Observability.EmailHealthCheck(new EmailSettings { Mode = "Smtp", SmtpHost = "127.0.0.1", SmtpPort = FreePort() }, new EmailDeliveryStatus(), production, TimeProvider.System);
        Assert.Equal(HealthStatus.Degraded, (await closedPort.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [Fact]
    public async Task Readiness_lists_each_check_without_details_and_liveness_is_plain()
    {
        await using var factory = new WorkbenchWebFactory("Production", oidcAuthority: "https://login.example.test/tenant/v2.0");
        using var client = factory.CreateClientAs(null);

        using var ready = await client.GetAsync("/health/ready");
        using var live = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        var json = JsonNode.Parse(await ready.Content.ReadAsStringAsync())!;
        Assert.Equal("Degraded", (string?)json["status"]); // Production with the pickup folder: email isn't really sent.
        Assert.Equal("Healthy", (string?)json["checks"]!["database"]);
        Assert.Equal("Healthy", (string?)json["checks"]!["catalog"]);
        Assert.Equal("Degraded", (string?)json["checks"]!["email"]);
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal("Healthy", await live.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_container_health_probe_fails_when_nothing_answers()
    {
        Assert.Equal(1, await Observability.RunContainerHealthCheckAsync(["--health-check", $"http://127.0.0.1:{FreePort()}/health/live"]));
    }

    private static int FreePort()
    {
        var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    private sealed class StaticOptions(JsonConsoleFormatterOptions value) : IOptionsMonitor<JsonConsoleFormatterOptions>
    {
        public JsonConsoleFormatterOptions CurrentValue => value;
        public JsonConsoleFormatterOptions Get(string? name) => value;
        public IDisposable? OnChange(Action<JsonConsoleFormatterOptions, string?> listener) => null;
    }
}

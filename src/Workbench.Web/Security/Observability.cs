using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Workbench.Application.Operations;
using Workbench.Application.Tenancy;
using Workbench.Infrastructure.Notifications;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Web.Security;

/// <summary>
/// Production observability (A.8.15, A.8.16): OpenTelemetry traces, metrics and logs over OTLP, structured JSON
/// console logs with personal data and secrets redacted, background-job metrics, readiness checks for the catalog and
/// email, and the container health probe. See docs/operations/monitoring.md.
/// </summary>
public static class Observability
{
    public const string JsonFormatterName = "workbench-json";

    /// <summary>
    /// Call right after <c>WebApplication.CreateBuilder</c>. Adds secrets mounted as files (<c>SecretsDirectory</c>, e.g.
    /// <c>/run/secrets</c> with a file per key such as <c>ConnectionStrings__Workbench</c>) and the JSON log formatter
    /// (the default outside Development unless <c>Logging:Console:FormatterName</c> says otherwise).
    /// </summary>
    public static WebApplicationBuilder AddWorkbenchHosting(this WebApplicationBuilder builder)
    {
        if (builder.Configuration["SecretsDirectory"] is { Length: > 0 } secrets && Directory.Exists(secrets))
        {
            // One file per key; files with a dot in their name (a mounted .pfx) aren't configuration.
            builder.Configuration.AddKeyPerFile(source =>
            {
                source.FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(secrets);
                source.Optional = true;
                source.IgnoreCondition = name => name.Contains('.', StringComparison.Ordinal);
            });
        }

        builder.Logging.AddConsoleFormatter<RedactingJsonConsoleFormatter, JsonConsoleFormatterOptions>();
        if (!builder.Environment.IsDevelopment() && string.IsNullOrWhiteSpace(builder.Configuration["Logging:Console:FormatterName"]))
        {
            builder.Services.Configure<ConsoleLoggerOptions>(o => o.FormatterName = JsonFormatterName);
        }

        return builder;
    }

    /// <summary>
    /// OpenTelemetry over OTLP when <c>OpenTelemetry:Endpoint</c> (or the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c>) is
    /// set: traces (ASP.NET Core, HttpClient, Npgsql, Workbench), metrics (the same plus .NET runtime and Workbench's
    /// jobs, security events and email) and logs (redacted like the console). Off by default.
    /// </summary>
    public static IServiceCollection AddWorkbenchTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["OpenTelemetry:Endpoint"] is { Length: > 0 } configured ? configured : configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return services;
        }

        var protocol = (configuration["OpenTelemetry:Protocol"] ?? "grpc").Equals("http/protobuf", StringComparison.OrdinalIgnoreCase)
            ? OtlpExportProtocol.HttpProtobuf
            : OtlpExportProtocol.Grpc;
        void Exporter(OtlpExporterOptions o)
        {
            o.Endpoint = new Uri(endpoint);
            o.Protocol = protocol;
            if (configuration["OpenTelemetry:Headers"] is { Length: > 0 } headers)
            {
                o.Headers = headers; // e.g. "x-api-key=..." for a hosted backend; a secret, so supply it from the vault.
            }
        }

        var serviceName = configuration["OpenTelemetry:ServiceName"] is { Length: > 0 } name ? name : "workbench-web";
        var environment = configuration["ASPNETCORE_ENVIRONMENT"] ?? configuration["DOTNET_ENVIRONMENT"] ?? "Production";
        services.AddOpenTelemetry()
            .ConfigureResource(r => r
                .AddService(serviceName, serviceVersion: typeof(Observability).Assembly.GetName().Version?.ToString())
                .AddAttributes([new("deployment.environment.name", environment)]))
            .WithTracing(t => t
                .AddSource(WorkbenchTelemetry.Name)
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/health") && !http.Request.Path.StartsWithSegments("/_framework");
                    o.EnrichWithHttpRequest = (activity, request) => activity.SetTag("url.query", null);
                })
                .AddHttpClientInstrumentation(o => o.EnrichWithHttpRequestMessage = (activity, _) => activity.SetTag("url.query", null))
                .AddNpgsql()
                .AddOtlpExporter(Exporter))
            .WithMetrics(m => m
                .AddMeter(WorkbenchTelemetry.Name)
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddNpgsqlInstrumentation()
                .AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Components", "Microsoft.AspNetCore.Http.Connections")
                .AddOtlpExporter(Exporter))
            .WithLogging(l => l
                .AddProcessor(new RedactingLogProcessor())
                .AddOtlpExporter(Exporter), o => o.IncludeFormattedMessage = true);
        return services;
    }

    /// <summary>Wraps the registered <see cref="ITenantJobRunner"/> so every background job run is counted, timed and traced.</summary>
    public static IServiceCollection AddWorkbenchJobInstrumentation(this IServiceCollection services)
    {
        var descriptor = services.LastOrDefault(d => d.ServiceType == typeof(ITenantJobRunner));
        if (descriptor is null)
        {
            return services;
        }

        services.Remove(descriptor);
        services.Add(ServiceDescriptor.Describe(typeof(ITenantJobRunner), sp => new InstrumentedTenantJobRunner(descriptor switch
        {
            { ImplementationInstance: ITenantJobRunner instance } => instance,
            { ImplementationFactory: { } factory } => (ITenantJobRunner)factory(sp),
            _ => (ITenantJobRunner)ActivatorUtilities.CreateInstance(sp, descriptor.ImplementationType!),
        }), descriptor.Lifetime));
        return services;
    }

    /// <summary>The readiness checks besides the tenant database: the catalog (MultiTenant) and email.</summary>
    public static IHealthChecksBuilder AddWorkbenchReadinessChecks(this IHealthChecksBuilder checks)
    {
        checks.Services.AddSingleton<EmailHealthCheck>();
        return checks
            .AddCheck<CatalogHealthCheck>("catalog", tags: ["ready"])
            .AddCheck<EmailHealthCheck>("email", failureStatus: HealthStatus.Degraded, tags: ["ready"]);
    }

    /// <summary>
    /// <c>{"status":"Healthy","checks":{"database":"Healthy","email":"Degraded"}}</c>: each check's status, never its
    /// description or exception (they can reveal hosts and connection details).
    /// </summary>
    public static Task WriteHealthJson(HttpContext context, HealthReport report)
    {
        context.Response.ContentType = "application/json; charset=utf-8";
        return context.Response.WriteAsync(JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
        }));
    }

    /// <summary>
    /// <c>Workbench.Web --health-check [url]</c>: the container HEALTHCHECK (the runtime image has no shell or curl).
    /// Exits 0 when <c>/health/live</c> answers 200.
    /// </summary>
    public static async Task<int> RunContainerHealthCheckAsync(string[] args)
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS")?.Split(';', ',')[0] is { Length: > 0 } p ? p : "8080";
        var url = args.SkipWhile(a => a != "--health-check").Skip(1).FirstOrDefault(a => a.StartsWith("http", StringComparison.Ordinal))
            ?? $"http://127.0.0.1:{port}/health/live";
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = await http.GetAsync(url);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }

    private sealed class InstrumentedTenantJobRunner(ITenantJobRunner inner) : ITenantJobRunner
    {
        public Task ForEachTenantAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            var job = JobName(work);
            return inner.ForEachTenantAsync(async ct =>
            {
                using var activity = WorkbenchTelemetry.ActivitySource.StartActivity($"job {job}");
                activity?.SetTag("workbench.job", job);
                var started = Stopwatch.GetTimestamp();
                var outcome = "success";
                try
                {
                    await work(ct);
                }
                catch (OperationCanceledException)
                {
                    outcome = "cancelled";
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = "failure";
                    activity?.SetStatus(ActivityStatusCode.Error, ex.GetType().Name);
                    throw;
                }
                finally
                {
                    WorkbenchTelemetry.JobRuns.Add(1, new("job", job), new("outcome", outcome));
                    WorkbenchTelemetry.JobDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, new KeyValuePair<string, object?>("job", job));
                }
            }, cancellationToken);
        }

        /// <summary>The background service the callback was written in (lambdas compile into nested closure classes).</summary>
        private static string JobName(Delegate work)
        {
            var type = work.Method.DeclaringType;
            while (type?.DeclaringType is not null)
            {
                type = type.DeclaringType;
            }

            return type?.Name ?? "unknown";
        }
    }

    /// <summary>MultiTenant: the catalog database answers (tenant resolution needs it on every request).</summary>
    private sealed class CatalogHealthCheck(IServiceProvider services, TenancyOptions tenancy) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            if (!tenancy.IsMultiTenant)
            {
                return HealthCheckResult.Healthy("SingleTenant: no catalog.");
            }

            try
            {
                await using var catalog = await services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(cancellationToken);
                await catalog.Tenants.AsNoTracking().AnyAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return HealthCheckResult.Unhealthy("The catalog database is unavailable.");
            }
        }
    }

    /// <summary>
    /// Email is degraded (never unhealthy: a mail outage mustn't take the app out of the load balancer) when the SMTP
    /// relay doesn't accept connections, the last delivery failed within 15 minutes, or Production writes to a folder.
    /// The SMTP probe is cached for a minute so frequent readiness probes don't hammer the relay.
    /// </summary>
    public sealed class EmailHealthCheck(EmailSettings settings, EmailDeliveryStatus status, IHostEnvironment environment, TimeProvider clock) : IHealthCheck
    {
        private readonly SemaphoreSlim _probeLock = new(1, 1);
        private (DateTimeOffset At, bool Reachable)? _lastProbe;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            var mode = settings.Mode.ToLowerInvariant();
            if (mode == "pickup")
            {
                return environment.IsProduction()
                    ? HealthCheckResult.Degraded("Email is written to a folder, not sent. Set Email:Mode.")
                    : HealthCheckResult.Healthy();
            }

            var now = clock.GetUtcNow();
            if (status.LastFailure is { } failed && failed > now.AddMinutes(-15) && (status.LastSuccess is not { } ok || ok < failed))
            {
                return HealthCheckResult.Degraded("The last email delivery failed.");
            }

            if (mode == "smtp" && !await SmtpReachableAsync(now, cancellationToken))
            {
                return HealthCheckResult.Degraded("The SMTP relay doesn't accept connections.");
            }

            return HealthCheckResult.Healthy();
        }

        private async Task<bool> SmtpReachableAsync(DateTimeOffset now, CancellationToken cancellationToken)
        {
            await _probeLock.WaitAsync(cancellationToken);
            try
            {
                if (_lastProbe is { } last && last.At > now.AddMinutes(-1))
                {
                    return last.Reachable;
                }

                var reachable = false;
                if (!string.IsNullOrWhiteSpace(settings.SmtpHost))
                {
                    try
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        using var tcp = new TcpClient();
                        await tcp.ConnectAsync(settings.SmtpHost, settings.SmtpPort, timeout.Token);
                        reachable = true;
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
                    {
                        reachable = false;
                    }
                }

                _lastProbe = (now, reachable);
                return reachable;
            }
            finally
            {
                _probeLock.Release();
            }
        }
    }
}

/// <summary>
/// Removes personal data and secrets from log text: email addresses, bearer tokens, passwords and keys in connection
/// strings, and the values of properties whose names say they are secret. Applied to the console JSON and OTLP logs.
/// It is a safety net; log statements still must not log secrets (docs/iso/secure-development-policy.md).
/// </summary>
public static partial class LogRedaction
{
    public const string Redacted = "[redacted]";

    private static readonly string[] SecretNames = ["password", "pwd", "secret", "token", "apikey", "api_key", "authorization", "cookie", "connectionstring", "tin", "ssn", "bankaccount", "accountnumber", "routingnumber", "privatekey"];

    public static string? Text(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        text = EmailRegex().Replace(text, "[email]");
        text = BearerRegex().Replace(text, "$1 " + Redacted);
        return ConnectionSecretRegex().Replace(text, "$1=" + Redacted);
    }

    public static bool IsSecretName(string name)
    {
        var normalized = name.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Replace(".", "", StringComparison.Ordinal);
        return SecretNames.Any(s => normalized.Equals(s.Replace("_", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)
            || (s.Length > 4 && normalized.Contains(s.Replace("_", "", StringComparison.Ordinal), StringComparison.OrdinalIgnoreCase)));
    }

    public static object? Value(string name, object? value) =>
        IsSecretName(name) ? Redacted : value is string s ? Text(s) : value;

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\b(Bearer|Basic)\s+[A-Za-z0-9\-._~+/=]{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\b(Password|Pwd|AccessKey|SharedAccessKey|AccountKey|ApiKey|Secret)\s*=\s*[^;\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionSecretRegex();
}

/// <summary>
/// One JSON object per line (the shape of ASP.NET Core's <c>json</c> formatter) with <see cref="LogRedaction"/>
/// applied, plus the trace and span ids and the tenant, so logs join up with traces. Select it with
/// <c>Logging:Console:FormatterName=workbench-json</c> (the default outside Development).
/// </summary>
public sealed class RedactingJsonConsoleFormatter(IOptionsMonitor<JsonConsoleFormatterOptions> options) : ConsoleFormatter(Observability.JsonFormatterName)
{
    public override void Write<TState>(in LogEntry<TState> logEntry, IExternalScopeProvider? scopeProvider, TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        using var buffer = new MemoryStream();
        var writerOptions = options.CurrentValue.JsonWriterOptions;
        if (writerOptions.Encoder is null)
        {
            // Log lines aren't HTML: keep '+', quotes and non-ASCII names readable instead of \u escapes.
            writerOptions.Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        }

        using (var json = new Utf8JsonWriter(buffer, writerOptions))
        {
            json.WriteStartObject();
            json.WriteString("Timestamp", DateTimeOffset.UtcNow.ToString("O"));
            json.WriteString("Level", logEntry.LogLevel.ToString());
            json.WriteString("Category", logEntry.Category);
            json.WriteNumber("EventId", logEntry.EventId.Id);
            json.WriteString("Message", LogRedaction.Text(message));
            if (logEntry.Exception is { } exception)
            {
                json.WriteString("Exception", LogRedaction.Text(exception.ToString()));
            }

            if (Activity.Current is { } activity)
            {
                json.WriteString("TraceId", activity.TraceId.ToHexString());
                json.WriteString("SpanId", activity.SpanId.ToHexString());
            }

            if (TenantScope.Current is { } tenant)
            {
                json.WriteString("Tenant", tenant.Slug);
            }

            if (logEntry.State is IReadOnlyList<KeyValuePair<string, object?>> state)
            {
                json.WriteStartObject("State");
                foreach (var (key, value) in state)
                {
                    if (key != "{OriginalFormat}")
                    {
                        WriteValue(json, key, LogRedaction.Value(key, value));
                    }
                }

                json.WriteEndObject();
            }

            if (options.CurrentValue.IncludeScopes && scopeProvider is not null)
            {
                json.WriteStartArray("Scopes");
                scopeProvider.ForEachScope((scope, writer) =>
                {
                    if (scope is IReadOnlyList<KeyValuePair<string, object?>> pairs)
                    {
                        writer.WriteStartObject();
                        foreach (var (key, value) in pairs)
                        {
                            if (key != "{OriginalFormat}")
                            {
                                WriteValue(writer, key, LogRedaction.Value(key, value));
                            }
                        }

                        writer.WriteEndObject();
                    }
                    else if (scope is not null)
                    {
                        writer.WriteStringValue(LogRedaction.Text(scope.ToString()));
                    }
                }, json);
                json.WriteEndArray();
            }

            json.WriteEndObject();
        }

        textWriter.Write(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length));
        textWriter.Write(Environment.NewLine);
    }

    private static void WriteValue(Utf8JsonWriter json, string key, object? value)
    {
        switch (value)
        {
            case null:
                json.WriteNull(key);
                break;
            case bool b:
                json.WriteBoolean(key, b);
                break;
            case int or long or short or byte or uint or ulong or ushort:
                json.WriteNumber(key, Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            case double or float or decimal:
                json.WriteNumber(key, Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture));
                break;
            default:
                json.WriteString(key, LogRedaction.Text(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)));
                break;
        }
    }
}

/// <summary>Applies <see cref="LogRedaction"/> to log records before the OTLP exporter sends them.</summary>
public sealed class RedactingLogProcessor : BaseProcessor<LogRecord>
{
    public override void OnEnd(LogRecord data)
    {
        data.FormattedMessage = LogRedaction.Text(data.FormattedMessage);
        if (data.Body is { } body)
        {
            data.Body = LogRedaction.Text(body);
        }

        if (data.Attributes is { Count: > 0 } attributes)
        {
            data.Attributes = attributes.Select(a => new KeyValuePair<string, object?>(a.Key, a.Key == "{OriginalFormat}" ? a.Value : LogRedaction.Value(a.Key, a.Value))).ToList();
        }
    }
}

using System.Security.Cryptography.X509Certificates;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Workbench.Application;
using Workbench.Application.Security;
using Workbench.Domain.Security;

namespace Workbench.Web.Security;

/// <summary>Data protection keys, health checks, telemetry and the retention job: the operational controls.</summary>
public static class OperationsSetup
{
    /// <summary>
    /// ASP.NET Core Data Protection (A.8.24) protects auth cookies and antiforgery tokens. Keys are kept in the database
    /// (so every server instance and restart shares them) or a folder, and encrypted at rest with a certificate when
    /// one is configured.
    /// </summary>
    public static IServiceCollection AddWorkbenchDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        var builder = services.AddDataProtection().SetApplicationName(settings.ApplicationName);
        switch (settings.Store.ToLowerInvariant())
        {
            case "database":
                services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(o =>
                    o.XmlRepository = new DatabaseXmlRepository(sp.GetRequiredService<IWorkbenchDbContextFactory>())));
                break;
            case "filesystem":
                if (string.IsNullOrWhiteSpace(settings.KeysFolder))
                {
                    throw new InvalidOperationException("Set DataProtection:KeysFolder when DataProtection:Store is FileSystem.");
                }

                builder.PersistKeysToFileSystem(new DirectoryInfo(settings.KeysFolder));
                break;
            case "default":
                break;
            default:
                throw new InvalidOperationException($"Unsupported DataProtection:Store '{settings.Store}'. Use Database, FileSystem or Default.");
        }

        if (!string.IsNullOrWhiteSpace(settings.CertificatePath))
        {
            builder.ProtectKeysWithCertificate(X509CertificateLoader.LoadPkcs12FromFile(settings.CertificatePath, settings.CertificatePassword));
        }
        else if (!string.IsNullOrWhiteSpace(settings.CertificateThumbprint))
        {
            builder.ProtectKeysWithCertificate(settings.CertificateThumbprint);
        }

        return services;
    }

    /// <summary>
    /// <c>/health/live</c> (the process is up) and <c>/health/ready</c> (the database answers), anonymous and without
    /// detail, for load balancers and orchestrators (A.8.16).
    /// </summary>
    public static IServiceCollection AddWorkbenchHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]);
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous().DisableRateLimiting();
        return app;
    }

    /// <summary>
    /// OpenTelemetry traces and metrics over OTLP when <c>OpenTelemetry:Endpoint</c> is set (off by default).
    /// Request URLs are recorded without query strings, which can carry tokens.
    /// </summary>
    public static IServiceCollection AddWorkbenchTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var endpoint = configuration["OpenTelemetry:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return services;
        }

        var serviceName = configuration["OpenTelemetry:ServiceName"] ?? "workbench-web";
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation(o =>
                {
                    o.Filter = http => !http.Request.Path.StartsWithSegments("/health") && !http.Request.Path.StartsWithSegments("/_framework");
                    o.EnrichWithHttpRequest = (activity, request) => activity.SetTag("url.query", null);
                })
                .AddHttpClientInstrumentation()
                .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)))
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentationIfAvailable()
                .AddOtlpExporter(o => o.Endpoint = new Uri(endpoint)));
        return services;
    }

    private static MeterProviderBuilder AddRuntimeInstrumentationIfAvailable(this MeterProviderBuilder builder) =>
        builder.AddMeter("System.Runtime", "Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel", "Microsoft.AspNetCore.Components");

    /// <summary>Stores Data Protection keys as rows of <see cref="DataProtectionKeyRecord"/>.</summary>
    private sealed class DatabaseXmlRepository(IWorkbenchDbContextFactory dbFactory) : IXmlRepository
    {
        public IReadOnlyCollection<XElement> GetAllElements()
        {
            var db = dbFactory.CreateAsync().GetAwaiter().GetResult();
            using var _ = (IDisposable)db;
            return db.Set<DataProtectionKeyRecord>().AsNoTracking().Select(k => k.Xml).ToList().Select(XElement.Parse).ToList();
        }

        public void StoreElement(XElement element, string friendlyName)
        {
            var db = dbFactory.CreateAsync().GetAwaiter().GetResult();
            using var _ = (IDisposable)db;
            db.Set<DataProtectionKeyRecord>().Add(new DataProtectionKeyRecord { FriendlyName = friendlyName, Xml = element.ToString(SaveOptions.DisableFormatting) });
            db.SaveChangesAsync().GetAwaiter().GetResult();
        }
    }

    private sealed class DatabaseHealthCheck(IWorkbenchDbContextFactory dbFactory) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var db = await dbFactory.CreateAsync(cancellationToken);
                await db.Set<DataProtectionKeyRecord>().AsNoTracking().AnyAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // No exception detail in the response: it can reveal connection details.
                return HealthCheckResult.Unhealthy("The database is unavailable.");
            }
        }
    }
}

/// <summary>Runs the retention purge once a day at <see cref="RetentionOptions.RunHourUtc"/> (A.5.33, A.8.10).</summary>
public sealed class RetentionBackgroundService(IServiceScopeFactory scopes, RetentionOptions options, TimeProvider clock, ILogger<RetentionBackgroundService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.NightlyJobEnabled)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, Math.Clamp(options.RunHourUtc, 0, 23), 0, 0, TimeSpan.Zero);
            if (next <= now)
            {
                next = next.AddDays(1);
            }

            await Task.Delay(next - now, clock, stoppingToken);
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(dryRun: false, "system", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention run failed");
            }
        }
    }
}

using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Workbench.Application;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Domain.Security;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Web.Security;

/// <summary>Data protection keys, health checks, telemetry and the retention job: the operational controls.</summary>
public static class OperationsSetup
{
    /// <summary>
    /// ASP.NET Core Data Protection (A.8.24) protects auth cookies and antiforgery tokens. Keys are kept in the database
    /// (so every server instance and restart shares them) or a folder, and encrypted at rest with a certificate, Azure
    /// Key Vault or AWS KMS (<see cref="DataProtectionKeyEncryption"/>; Production refuses to start without one).
    /// </summary>
    public static IServiceCollection AddWorkbenchDataProtection(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var settings = configuration.GetSection(DataProtectionSettings.SectionName).Get<DataProtectionSettings>() ?? new DataProtectionSettings();
        var builder = services.AddDataProtection().SetApplicationName(settings.ApplicationName);
        switch (settings.Store.ToLowerInvariant())
        {
            case "database":
                // MultiTenant: one key ring in the catalog database; tenants are separated by purpose (TenantDataProtectionProvider).
                services.AddSingleton<IConfigureOptions<KeyManagementOptions>>(sp => new ConfigureOptions<KeyManagementOptions>(o =>
                    o.XmlRepository = sp.GetRequiredService<TenancyOptions>().IsMultiTenant
                        ? new CatalogXmlRepository(sp.GetRequiredService<IDbContextFactory<CatalogDbContext>>())
                        : new DatabaseXmlRepository(sp.GetRequiredService<IWorkbenchDbContextFactory>())));
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

        builder.SetDefaultKeyLifetime(TimeSpan.FromDays(Math.Max(7, settings.KeyLifetimeDays)));
        DataProtectionKeyEncryption.Configure(builder, settings);
        return services;
    }

    /// <summary>
    /// <c>/health/live</c> (the process is up) and <c>/health/ready</c> (the database and, in MultiTenant mode, the
    /// catalog answer; email works), anonymous and with each check's status only, for load balancers and orchestrators
    /// (A.8.16). A degraded check (email) still answers 200.
    /// </summary>
    public static IServiceCollection AddWorkbenchHealthChecks(this IServiceCollection services)
    {
        services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database", tags: ["ready"]).AddWorkbenchReadinessChecks();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkbenchHealthChecks(this IEndpointRouteBuilder app)
    {
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous().DisableRateLimiting();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready"), ResponseWriter = Observability.WriteHealthJson }).AllowAnonymous().DisableRateLimiting();
        return app;
    }

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

    private sealed class DatabaseHealthCheck(IServiceProvider services, ITenantContext tenants) : IHealthCheck
    {
        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                // The host site (MultiTenant, no tenant) checks the catalog; a tenant address checks that tenant's database.
                if (tenants.IsHost)
                {
                    await using var catalog = await services.GetRequiredService<IDbContextFactory<CatalogDbContext>>().CreateDbContextAsync(cancellationToken);
                    await catalog.Tenants.AsNoTracking().AnyAsync(cancellationToken);
                    return HealthCheckResult.Healthy();
                }

                await using var db = await services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(cancellationToken);
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
public sealed class RetentionBackgroundService(IServiceScopeFactory scopes, ITenantJobRunner tenants, RetentionOptions options, TimeProvider clock, ILogger<RetentionBackgroundService> logger)
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
                await tenants.ForEachTenantAsync(async ct =>
                {
                    await using var scope = scopes.CreateAsyncScope();
                    await scope.ServiceProvider.GetRequiredService<RetentionService>().RunAsync(dryRun: false, "system", ct);
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Retention run failed");
            }
        }
    }
}

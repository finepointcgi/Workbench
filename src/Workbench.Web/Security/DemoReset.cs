using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Web.Security;

/// <summary>
/// The public demo's nightly reset (docs/demo-hosting.md): deletes the demo database, attachments and picked-up email,
/// then stops the host. The container platform starts it again, and startup migrates and seeds a fresh copy of the demo
/// data, with nothing left in memory from the day before. Only ever runs in demo mode, on a demo database.
/// </summary>
public static class DemoReset
{
    /// <summary>Registers the nightly reset when this host is the public demo.</summary>
    public static WebApplicationBuilder AddWorkbenchDemoReset(this WebApplicationBuilder builder)
    {
        if (DemoMode.IsActive(builder.Configuration, builder.Environment) && builder.Configuration.GetValue("Demo:NightlyReset", true))
        {
            builder.Services.AddHostedService<DemoResetBackgroundService>();
        }

        return builder;
    }

    /// <summary>
    /// <c>Workbench.Web --reset-demo</c> and the nightly job: deletes the demo data. Refuses outside demo mode or on a
    /// database that isn't a demo database (<see cref="DemoMode.EnsureDemoDatabaseAsync"/>). Restart the host afterwards.
    /// </summary>
    public static async Task DeleteDemoDataAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        if (!DemoMode.IsActive(configuration, services.GetService<IHostEnvironment>()))
        {
            throw new InvalidOperationException($"--reset-demo only runs in demo mode (ASPNETCORE_ENVIRONMENT={DemoMode.EnvironmentName}, {DemoMode.EnabledKey}=true).");
        }

        await using (var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken))
        {
            await DemoMode.EnsureDemoDatabaseAsync(db, cancellationToken);
            if (db.Database.IsSqlite())
            {
                SqliteConnection.ClearAllPools();
            }

            await db.Database.EnsureDeletedAsync(cancellationToken);
        }

        EmptyFolder(configuration["Storage:Root"]);
        EmptyFolder(configuration["Email:PickupFolder"]);
    }

    /// <summary>Deletes what's inside the folder but keeps the folder: it's usually a mounted volume.</summary>
    private static void EmptyFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        var folder = new DirectoryInfo(path);
        foreach (var file in folder.EnumerateFiles())
        {
            file.Delete();
        }

        foreach (var child in folder.EnumerateDirectories())
        {
            child.Delete(recursive: true);
        }
    }
}

/// <summary>Resets the demo once a day at <c>Demo:ResetHourUtc</c> (default 08:00 UTC, the small hours in the US).</summary>
public sealed class DemoResetBackgroundService(IServiceProvider services, IConfiguration configuration, IHostApplicationLifetime lifetime, TimeProvider clock,
    ILogger<DemoResetBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var hour = Math.Clamp(configuration.GetValue("Demo:ResetHourUtc", 8), 0, 23);
        var now = clock.GetUtcNow();
        var next = new DateTimeOffset(now.Year, now.Month, now.Day, hour, 0, 0, TimeSpan.Zero);
        if (next <= now)
        {
            next = next.AddDays(1);
        }

        await Task.Delay(next - now, clock, stoppingToken);
        try
        {
            logger.LogWarning("Resetting the demo: deleting its data and stopping so the host restarts with fresh demo data.");
            await DemoReset.DeleteDemoDataAsync(services, stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Demo reset failed; the demo keeps running on yesterday's data.");
            return;
        }

        lifetime.StopApplication();
    }
}

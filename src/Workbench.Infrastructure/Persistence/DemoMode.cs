using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Workbench.Domain.Assistant;
using Workbench.Domain.Identity;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// The public demo (docs/demo-hosting.md): seeded sample data, the dev sign-in page and the fake assistant on a host
/// anyone with the link can use. It takes two deliberate settings, <c>ASPNETCORE_ENVIRONMENT=Demo</c> and
/// <c>Demo:Enabled=true</c>, so one stray variable can't turn a real deployment into an open one: Production refuses
/// <c>Demo:Enabled</c> (<c>ProductionConfigurationCheck</c>), and Development doesn't need it.
/// </summary>
public static class DemoMode
{
    public const string EnvironmentName = "Demo";
    public const string EnabledKey = "Demo:Enabled";

    /// <summary>The seeded administrator. A database without it (but with users) holds someone's real data.</summary>
    public const string DemoAdminEmail = "dev@workbench.local";

    /// <summary>True when this host is the public demo: the Demo environment with <c>Demo:Enabled=true</c>.</summary>
    public static bool IsActive(IConfiguration configuration, IHostEnvironment? environment) =>
        environment is not null && environment.IsEnvironment(EnvironmentName) && configuration.GetValue(EnabledKey, false);

    /// <summary>
    /// Demo mode only ever seeds, resets or opens up a demo database: empty, or one that has the seeded demo
    /// administrator. Anything else is refused before a single row is written or deleted.
    /// </summary>
    public static async Task EnsureDemoDatabaseAsync(WorkbenchDbContext db, CancellationToken cancellationToken = default)
    {
        if (db.Database.IsRelational())
        {
            var creator = db.GetService<IRelationalDatabaseCreator>();
            if (!await creator.ExistsAsync(cancellationToken) || !await creator.HasTablesAsync(cancellationToken))
            {
                return;
            }
        }

        var users = db.Set<AppUser>();
        if (await users.AnyAsync(cancellationToken) && !await users.AnyAsync(u => u.Email == DemoAdminEmail, cancellationToken))
        {
            throw new InvalidOperationException(
                $"Demo mode is on, but this database has users and no demo administrator ({DemoAdminEmail}): it isn't a demo database. "
                + "Point the demo at its own empty database, or turn Demo:Enabled off.");
        }
    }

    /// <summary>
    /// After the sample data: the assistant turned on with the fake provider (no model, no cost), its notice accepted by
    /// the demo administrator, so testers can try it straight away. Leaves settings someone already saved alone.
    /// </summary>
    public static async Task SeedDemoSettingsAsync(WorkbenchDbContext db, CancellationToken cancellationToken = default)
    {
        if (await db.Set<AssistantSettings>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Set<AssistantSettings>().Add(new AssistantSettings
        {
            IsEnabled = true,
            Connection = Application.Assistant.AssistantOptions.FakeConnection,
            DailyQuestionLimitPerUser = 100,
            NoticeAcceptedBy = DemoAdminEmail,
            NoticeAcceptedAtUtc = now,
            NoticeVersion = Contracts.Assistant.AssistantNotice.Version,
            UpdatedBy = DemoAdminEmail,
            UpdatedAtUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

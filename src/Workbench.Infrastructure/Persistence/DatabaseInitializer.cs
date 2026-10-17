using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Creates the schema and seeds sample data for local development.
    /// TODO: replace EnsureCreated with EF Core migrations before the first shared environment.
    /// </summary>
    public static async Task InitializeWorkbenchDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);

        if (await db.Workspaces.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var workspace = new Workspace { Name = "Program Office" };
        var schedule = new Sheet
        {
            Workspace = workspace,
            Name = "Integrated Master Schedule",
            Description = "Sample project sheet with scheduling enabled",
            SchedulingEnabled = true,
            CostpointProjectId = "1001.01",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        schedule.Items.AddRange(
        [
            new Item { Title = "Kickoff", Order = 0, Start = DateOnly.FromDateTime(now), Finish = DateOnly.FromDateTime(now), CostpointWbsId = "1001.01.01" },
            new Item { Title = "Requirements review", Order = 1, Start = DateOnly.FromDateTime(now.AddDays(1)), Finish = DateOnly.FromDateTime(now.AddDays(10)), CostpointWbsId = "1001.01.02" },
        ]);
        var tracker = new Sheet
        {
            Workspace = workspace,
            Name = "Risk Register",
            Description = "Sample tracking sheet",
            CreatedAtUtc = now.AddMinutes(-5),
            UpdatedAtUtc = now.AddMinutes(-5),
        };

        db.AddRange(workspace, schedule, tracker);
        await db.SaveChangesAsync(cancellationToken);
    }
}

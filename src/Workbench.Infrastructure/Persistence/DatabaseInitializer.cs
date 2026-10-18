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
        var status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
        var owner = new Column { Name = "Owner", Type = ColumnType.Person, Order = 1 };
        var hours = new Column { Name = "Planned hours", Type = ColumnType.Number, Order = 2 };
        var budget = new Column { Name = "Budget", Type = ColumnType.Currency, Order = 3 };
        var costPerHour = new Column { Name = "Cost per hour", Type = ColumnType.Currency, Order = 4, Formula = "[Budget] / [Planned hours]" };
        schedule.Columns.AddRange([status, owner, hours, budget, costPerHour]);

        var today = DateOnly.FromDateTime(now);
        var phase = new Item { Title = "Phase 1: Planning", Order = 0, Start = today, Finish = today.AddDays(20), CostpointWbsId = "1001.01.01" };
        schedule.Items.AddRange(
        [
            phase,
            new Item { Title = "Kickoff meeting", ParentId = phase.Id, Order = 0, Start = today, Finish = today, CostpointWbsId = "1001.01.01",
                Values = new() { [status.Id.ToString()] = "Done", [owner.Id.ToString()] = "pm@example.com", [hours.Id.ToString()] = "8", [budget.Id.ToString()] = "1200" } },
            new Item { Title = "Requirements review", ParentId = phase.Id, Order = 1, Start = today.AddDays(1), Finish = today.AddDays(10), CostpointWbsId = "1001.01.02",
                Values = new() { [status.Id.ToString()] = "In progress", [owner.Id.ToString()] = "analyst@example.com", [hours.Id.ToString()] = "60", [budget.Id.ToString()] = "9000" } },
            new Item { Title = "Baseline schedule", ParentId = phase.Id, Order = 2, Start = today.AddDays(11), Finish = today.AddDays(20), CostpointWbsId = "1001.01.03",
                Values = new() { [status.Id.ToString()] = "Not started", [owner.Id.ToString()] = "pm@example.com", [hours.Id.ToString()] = "40", [budget.Id.ToString()] = "6000" } },
            new Item { Title = "Phase 2: Execution", Order = 1, Start = today.AddDays(21), Finish = today.AddDays(90), CostpointWbsId = "1001.02" },
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

        // Scheduling sample: a finish-to-start chain from kickoff into Phase 2, and an earlier baseline.
        var tasks = schedule.Items.ToDictionary(i => i.Title);
        Guid[] chain = [tasks["Kickoff meeting"].Id, tasks["Requirements review"].Id, tasks["Baseline schedule"].Id, tasks["Phase 2: Execution"].Id];
        db.AddRange(chain.Zip(chain.Skip(1), (p, s) => new Domain.Scheduling.Dependency { SheetId = schedule.Id, PredecessorId = p, SuccessorId = s }));
        var original = new Domain.Scheduling.Baseline { SheetId = schedule.Id, Name = "Original plan", CapturedAtUtc = now.AddDays(-14) };
        original.Entries.AddRange(schedule.Items.Select(i => new Domain.Scheduling.BaselineEntry
        {
            ItemId = i.Id,
            Start = i.ParentId is null ? i.Start : i.Start?.AddDays(-3),
            Finish = i.ParentId is null ? i.Finish : i.Finish?.AddDays(-3),
        }));
        db.Add(original);
        await db.SaveChangesAsync(cancellationToken);

        await ProjectAccountingSeed.SeedAsync(db, schedule, cancellationToken);
        await FinancialsSeed.SeedAsync(db, cancellationToken);
    }
}

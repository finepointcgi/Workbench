using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workbench.Domain;
using Workbench.Domain.Identity;

namespace Workbench.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Startup: applies pending migrations when <c>Database:MigrateOnStartup</c> is true (the default in Development
    /// only), then, in Development, seeds the sample data (every tenant's database in MultiTenant mode). Production
    /// deploys run <c>Workbench.Web --migrate</c> first instead.
    /// </summary>
    public static async Task InitializeWorkbenchDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetService<IHostEnvironment>();
        var development = environment?.IsDevelopment() ?? true;
        if (configuration.GetValue("Database:MigrateOnStartup", development))
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
        }

        if (development)
        {
            await services.GetRequiredService<DatabaseMigrator>().SeedDevelopmentAsync(cancellationToken);
        }
    }

    /// <summary>The development sample data, for the database the ambient tenant (or the single database) points at. Idempotent.</summary>
    public static async Task SeedSampleDataAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var factory = services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>();
        await using var db = await factory.CreateDbContextAsync(cancellationToken);
        await SeedUsersAsync(db, cancellationToken);

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
        // Finance and owners only; "Cost per hour" reads it, so it is restricted too.
        var budget = new Column { Name = "Budget", Type = ColumnType.Currency, Order = 3, IsRestricted = true };
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
            // A milestone: a key date with no duration (the calendar and Gantt show it as a diamond).
            new Item { Title = "Planning complete (baseline approved)", ParentId = phase.Id, Order = 3, Start = today.AddDays(20), Finish = today.AddDays(20),
                IsMilestone = true, Values = new() { [status.Id.ToString()] = "Not started", [owner.Id.ToString()] = "pm@example.com" } },
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

        db.AddRange(
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "pm@example.com", Role = WorkspaceRole.Owner },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "analyst@example.com", Role = WorkspaceRole.Editor },
            new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = "viewer@example.com", Role = WorkspaceRole.Viewer });
        await db.SaveChangesAsync(cancellationToken);

        await ProjectAccountingSeed.SeedAsync(db, schedule, cancellationToken);
        // Dashboards sample: one dashboard over the schedule and its contract (runs after the project-accounting seed).
        static string Config(Contracts.Dashboards.WidgetConfig config) =>
            System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        var overview = new Domain.Dashboards.Dashboard
        {
            Name = "Program Office overview",
            Description = "Budget, schedule and status across the Integrated Master Schedule",
            OwnerEmail = "pm@example.com",
            WorkspaceId = workspace.Id,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        Domain.Dashboards.Widget Widget(Domain.Dashboards.WidgetType type, string title, int width, Domain.Dashboards.WidgetHeight height,
            Contracts.Dashboards.WidgetConfig config) =>
            new() { DashboardId = overview.Id, Type = type, Title = title, Order = overview.Widgets.Count, Width = width, Height = height, ConfigJson = Config(config) };
        var sum = Contracts.Dashboards.MetricAggregate.Sum;
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "Total budget", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, sum, budget.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "Planned hours", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, sum, hours.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "In progress", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, FilterColumnId: status.Id, FilterValue: "In progress")));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "Average cost per hour", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, Contracts.Dashboards.MetricAggregate.Average, costPerHour.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Chart, "Tasks by status", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, Chart: Contracts.Dashboards.ChartKind.Donut, GroupColumnId: status.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Chart, "Planned hours by owner", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, ValueColumnId: hours.Id, GroupColumnId: owner.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.ScheduleHealth, "Schedule health", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.ItemList, "PM's tasks", 6, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, FilterColumnId: owner.Id, FilterValue: "pm@example.com")));
        var contract = await db.Set<Domain.ProjectAccounting.Contract>().OrderBy(c => c.ContractNumber).FirstOrDefaultAsync(cancellationToken);
        if (contract is not null)
        {
            overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.ContractFunding, "Contracts and funding", 6, Domain.Dashboards.WidgetHeight.Medium,
                new(ContractId: contract.Id)));
        }

        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.RichText, "About this dashboard", 12, Domain.Dashboards.WidgetHeight.Small,
            new(Markdown: "## Program Office overview\nFigures come from the **Integrated Master Schedule** and the program's contract.\n\n- Widgets refresh when the sheet changes\n- Choose *Edit* to add, move or resize widgets")));
        db.Add(overview);
        await db.SaveChangesAsync(cancellationToken);

        await FinancialsSeed.SeedAsync(db, cancellationToken);
        await AccountingSeed.SeedAsync(db, cancellationToken);
        await ContractDetailSeed.SeedAsync(db, cancellationToken);
        await IndirectRatesSeed.SeedAsync(db, cancellationToken);
        await BillingSeed.SeedAsync(db, factory, cancellationToken);
        await PurchasingSeed.SeedAsync(db, services, cancellationToken);
        await ProcurementSeed.SeedAsync(db, services, cancellationToken);
        await AssetsSeed.SeedAsync(db, services, cancellationToken);
        await CashSeed.SeedAsync(db, services, cancellationToken);
        await PayrollSeed.SeedAsync(db, cancellationToken);
        await LeaveSeed.SeedAsync(db, cancellationToken);
        await PerDiemSeed.SeedAsync(db, cancellationToken);
        await ResourcingSeed.SeedAsync(db, cancellationToken);
        await RevenueSeed.SeedAsync(db, services, cancellationToken);
        await IncurredCostSeed.SeedAsync(db, cancellationToken); // Incurred cost: a FY2026 draft from the seeded books (last: reads every ledger seed).
        await EvmSeed.SeedAsync(db, services, cancellationToken); // Earned value on 1001: PMB, a closed period, a VAR and baseline changes.
        await CeilingsSeed.SeedAsync(db, cancellationToken);
        await ProjectManagerSeed.SeedAsync(db, cancellationToken); // Sam Patel (Project manager only) runs 1002; a few assigned tasks.
        await HrSeed.SeedAsync(db, cancellationToken); // HR: Harper Reed (HR role), positions, records, clearances, benefit plans, open enrollment, checklists.
        await MaterialsSeed.SeedAsync(db, services, cancellationToken); // Mia Torres (Materials), hardware project 3001: parts, 3-level BOM, stock, GFM, work orders.
    }

    /// <summary>
    /// The people offered by the development sign-in page, each matching a seeded employee by email
    /// (<see cref="ProjectAccountingSeed"/>, <see cref="ProjectManagerSeed"/>). dev@ is an Admin, pm@ is Finance and a Project
    /// manager, sam.pm@ is only a Project manager, hr@ is HR (<see cref="HrSeed"/>), the others are plain Employees.
    /// </summary>
    public static IReadOnlyList<(string Email, string DisplayName, bool IsAdmin, bool IsFinance, bool IsProjectManager, bool IsHr)> DevUsers { get; } =
    [
        ("dev@workbench.local", "Dana Lee", true, false, false, false),
        ("pm@example.com", "Pat Morgan", false, true, true, false),
        (ProjectManagerSeed.SamEmail, "Sam Patel", false, false, true, false),
        (HrSeed.HarperEmail, "Harper Reed", false, false, false, true),
        ("analyst@example.com", "Alex Rivera", false, false, false, false),
        ("viewer@example.com", "Val Chen", false, false, false, false),
    ];

    private static async Task SeedUsersAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<AppUser>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        db.Set<AppUser>().AddRange(DevUsers.Select(u => new AppUser { Email = u.Email, DisplayName = u.DisplayName, IsAdmin = u.IsAdmin, IsFinance = u.IsFinance, IsProjectManager = u.IsProjectManager, IsHr = u.IsHr, CreatedAtUtc = now }));
        await db.SaveChangesAsync(cancellationToken);
    }
}

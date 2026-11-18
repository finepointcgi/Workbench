using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Domain;
using Workbench.Domain.Identity;

namespace Workbench.Infrastructure.Persistence;

public static class DatabaseInitializer
{
    /// <summary>
    /// Startup: applies pending migrations when <c>Database:MigrateOnStartup</c> is true (the default in Development
    /// and the public demo only), then, in Development or the demo, seeds the sample data (every tenant's database in
    /// MultiTenant mode). Production deploys run <c>Workbench.Web --migrate</c> first instead.
    /// </summary>
    public static async Task InitializeWorkbenchDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        var configuration = services.GetRequiredService<IConfiguration>();
        var environment = services.GetService<IHostEnvironment>();
        var demo = DemoMode.IsActive(configuration, environment);
        var development = (environment?.IsDevelopment() ?? true) || demo;
        if (configuration.GetValue("Database:MigrateOnStartup", development))
        {
            await services.GetRequiredService<DatabaseMigrator>().MigrateAsync(cancellationToken);
        }

        if (demo)
        {
            await using var existing = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken);
            await DemoMode.EnsureDemoDatabaseAsync(existing, cancellationToken);
        }

        if (development)
        {
            await services.GetRequiredService<DatabaseMigrator>().SeedDevelopmentAsync(cancellationToken);
        }

        if (demo)
        {
            await using var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken);
            await DemoMode.SeedDemoSettingsAsync(db, cancellationToken);
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

        // Demo:Dataset=Commercial: a professional-services firm instead of the government contractor below.
        var configuration = services.GetService<IConfiguration>();
        if (configuration is not null)
        {
            DemoMode.ValidateDataset(configuration);
        }

        if (DemoMode.IsCommercial(configuration))
        {
            await CommercialDemoSeed.SeedAsync(db, services, factory, cancellationToken);
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
        // Who works a task and for how many hours is the plan's people (assignees and staffing), not sheet columns.
        var status = new Column { Name = "Status", Type = ColumnType.Dropdown, Order = 0, Options = ["Not started", "In progress", "Blocked", "Done"] };
        // Finance and owners only.
        var budget = new Column { Name = "Budget", Type = ColumnType.Currency, Order = 1, IsRestricted = true };
        schedule.Columns.AddRange([status, budget]);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var phase = new Item { Title = "Phase 1: Planning", Order = 0, Start = today, Finish = today.AddDays(20), CostpointWbsId = "1001.01.01" };
        schedule.Items.AddRange(
        [
            phase,
            new Item { Title = "Kickoff meeting", ParentId = phase.Id, Order = 0, Start = today, Finish = today, CostpointWbsId = "1001.01.01",
                Values = new() { [status.Id.ToString()] = "Done", [budget.Id.ToString()] = "1200" } },
            new Item { Title = "Requirements review", ParentId = phase.Id, Order = 1, Start = today.AddDays(1), Finish = today.AddDays(10), CostpointWbsId = "1001.01.02",
                Values = new() { [status.Id.ToString()] = "In progress", [budget.Id.ToString()] = "9000" } },
            new Item { Title = "Baseline schedule", ParentId = phase.Id, Order = 2, Start = today.AddDays(11), Finish = today.AddDays(20), CostpointWbsId = "1001.01.03",
                Values = new() { [status.Id.ToString()] = "Not started", [budget.Id.ToString()] = "6000" } },
            // A milestone: a key date with no duration (the calendar and Gantt show it as a diamond).
            new Item { Title = "Planning complete (baseline approved)", ParentId = phase.Id, Order = 3, Start = today.AddDays(20), Finish = today.AddDays(20),
                IsMilestone = true, Values = new() { [status.Id.ToString()] = "Not started" } },
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
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "In progress", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, FilterColumnId: status.Id, FilterValue: "In progress")));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Metric, "Done", 3, Domain.Dashboards.WidgetHeight.Small,
            new(schedule.Id, FilterColumnId: status.Id, FilterValue: "Done")));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Chart, "Tasks by status", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, Chart: Contracts.Dashboards.ChartKind.Donut, GroupColumnId: status.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.Chart, "Budget by status", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, ValueColumnId: budget.Id, GroupColumnId: status.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.ScheduleHealth, "Schedule health", 4, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id)));
        overview.Widgets.Add(Widget(Domain.Dashboards.WidgetType.ItemList, "Tasks in progress", 6, Domain.Dashboards.WidgetHeight.Medium,
            new(schedule.Id, FilterColumnId: status.Id, FilterValue: "In progress")));
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
        await ModuleSeedsAsync(ModuleSeedStage.AfterProcurement); // The sample fixed assets and bank accounts come from the accounting module, at the stage above.
        await ModuleSeedsAsync(ModuleSeedStage.AfterCash); // The sample pay group, periods, pay rates and payroll profiles come from the workforce module, at the stage above.
        await LeaveSeed.SeedAsync(db, cancellationToken);
        await PerDiemSeed.SeedAsync(db, cancellationToken);
        await ResourcingSeed.SeedAsync(db, cancellationToken);
        await ResourcingSeed.SeedSkillsAsync(db, cancellationToken);
        await RevenueSeed.SeedAsync(db, services, cancellationToken);
        await ModuleSeedsAsync(ModuleSeedStage.AfterRevenue);
        // Earned value on 1001 (PMB, a closed period, a VAR, baseline changes) and the sample ceilings come from the project controls module's seed, at the stage above.
        await ProjectManagerSeed.SeedAsync(db, cancellationToken); // Sam Patel (Project manager only) runs 1002; a few assigned tasks.
        await LifecycleSeed.SeedAsync(db, cancellationToken); // A project at each stage of its life (5101 set up, 5102 planning, 5103 awaiting approval, 5104 executing).
        // HR sample data (positions, records, clearances, benefit plans, open enrollment, checklists) comes from the workforce module, at the stage below.
        await ModuleSeedsAsync(ModuleSeedStage.AfterHr);
        await AssistantSeed.SeedAsync(db, cancellationToken); // Timesheet flags: the analyst's pending timesheet gets weekend hours, a late day and a moved-hours revision.
        // Planning (this year's adopted budget, next year's cycle, rent and accrual recurring journals) comes from the accounting module, at the stage below.
        await ModuleSeedsAsync(ModuleSeedStage.AfterPlanning);
        await CurrencySeed.SeedAsync(db, services, cancellationToken); // Multicurrency: EUR and GBP with rates, a posted EUR vendor bill (BTG) and an approved GBP invoice (UKMOD).
        await CompanySeed.SeedAsync(db, services, cancellationToken); // Multicompany: subsidiary FED (contract, project 4001, Jordan Blake E1041) and two weeks of intercompany labor with MAIN.
        await ModuleSeedsAsync(ModuleSeedStage.AfterMulticompany);
        await TravelSeed.SeedAsync(db, cancellationToken); // Travel: analyst@'s approved DC trip TA-000001 on its expense report; Citi corporate card ••4417 with imported charges.
        await ModuleSeedsAsync(ModuleSeedStage.AfterTravel);
        await SourcingSeed.SeedAsync(db, services, cancellationToken); // Sourcing: RFQ-000001 (three quotes, awarded to PRIMA on an issued PO), PRIMA's portal contact Jordan Reyes, DIGI as an EDI partner.
        // Bank files and sales tax (ACH settings, vendor NETS with approved ACH details, a bill with use tax and an approved ACH run; Fairfax/MD/DC rates and a taxed NVDC invoice)
        // and the tax books (two assets, an asset transfer) come from the accounting module, at the stage below.
        await ModuleSeedsAsync(ModuleSeedStage.AfterSourcing);
        await CpsrPurchaseOrderSeed.SeedAsync(db, services, cancellationToken); // Three issued purchase orders for the CPSR sample (the government contracting module adds the files).
        await ModuleSeedsAsync(ModuleSeedStage.AfterTaxBooks);
        await CustomizationSeed.SeedAsync(db, cancellationToken); // Customization: "Program office" and "Customer POC" on projects, "Clearance sponsor" and a sensitive "Badge number" on employees, sub-statuses under active projects.
        await AutomationSeed.SeedAsync(db, cancellationToken); // Admin › Automation: funding under 25% → PM, receipts over $75 (validation), timesheet approval escalation after 2 days.
        await LayoutsSeed.SeedAsync(db, cancellationToken); // Customization: a shared "My open projects" view on Projects and a Finance home page (KPIs, to-dos, shortcuts, a chart of that view).
        await ModuleSeedsAsync(ModuleSeedStage.End);

        // Sample data of the license modules that are loaded (nothing without any), at the points in the order above where each belongs.
        async Task ModuleSeedsAsync(ModuleSeedStage stage)
        {
            await ModuleSeeds.RunAsync(services, db, stage, cancellationToken);
        }
    }

    /// <summary>
    /// The people offered by the development sign-in page, each matching a seeded employee by email
    /// (<see cref="ProjectAccountingSeed"/>, <see cref="ProjectManagerSeed"/>). dev@ is an Admin, pm@ is Finance and a Project
    /// manager, sam.pm@ is only a Project manager, hr@ is HR (the workforce module's HR sample data), the others are plain Employees.
    /// </summary>
    public static IReadOnlyList<(string Email, string DisplayName, bool IsAdmin, bool IsFinance, bool IsProjectManager, bool IsHr)> DevUsers { get; } =
    [
        ("dev@workbench.local", "Dana Lee", true, false, false, false),
        ("pm@example.com", "Pat Morgan", false, true, true, false),
        (ProjectManagerSeed.SamEmail, "Sam Patel", false, false, true, false),
        ("hr@example.com", "Harper Reed", false, false, false, true),
        ("analyst@example.com", "Alex Rivera", false, false, false, false),
        ("viewer@example.com", "Val Chen", false, false, false, false),
    ];

    private static async Task SeedUsersAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        // The built-in roles (migrations seed them; a database made another way gets them here).
        await RoleStore.EnsureBuiltInRolesAsync(db, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (await db.Set<AppUser>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        foreach (var u in DevUsers)
        {
            var roles = (u.IsAdmin ? Contracts.Identity.OrgRoles.Admin : 0) | (u.IsFinance ? Contracts.Identity.OrgRoles.Finance : 0)
                | (u.IsProjectManager ? Contracts.Identity.OrgRoles.ProjectManager : 0) | (u.IsHr ? Contracts.Identity.OrgRoles.Hr : 0);
            await RoleStore.AddUserAsync(db, new AppUser { Email = u.Email, DisplayName = u.DisplayName, CreatedAtUtc = now }, roles, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

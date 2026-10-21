using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Automations;
using Workbench.Contracts.Automations;
using Workbench.Contracts.Odcs;
using Workbench.Domain.Automations;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.AutomationBuilder;

/// <summary>
/// The real application registrations (CollaborationHost) with: admin@ (Admin), finance@ (Finance), pm@ (Pat, PM of 1001,
/// supervises dev@), dev@ (Dana, an employee charging 1001), viewer@ (a user with no roles and no employee record), and
/// contract C1 (funded $1,000, ends in 30 days) behind project 1001, where dev@ has 8 approved hours at $100 (so 20% of the
/// funding is left).
/// </summary>
internal sealed class AutomationKit : IAsyncDisposable
{
    public const string Admin = "admin@example.com";
    public const string Finance = "finance@example.com";
    public const string Pm = "pm@example.com";
    public const string Dev = "dev@example.com";
    public const string Viewer = "viewer@example.com";
    public static readonly DateOnly Day = new(2026, 10, 14);

    private AutomationKit(CollaborationHost host) => Host = host;

    public CollaborationHost Host { get; }
    public Guid ContractId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid LeafId { get; private set; }
    public Guid DevId { get; private set; }
    public Guid PmId { get; private set; }

    public IAutomationService Automations => Host.Get<IAutomationService>();
    public IWorkbenchDbContextFactory Factory => Host.Get<IWorkbenchDbContextFactory>();

    public static async Task<AutomationKit> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var kit = new AutomationKit(new CollaborationHost(configure));
        await kit.SeedAsync();
        kit.As(Admin);
        return kit;
    }

    public AutomationKit As(string email)
    {
        Host.User.Email = email;
        return this;
    }

    private async Task SeedAsync()
    {
        await using var db = await Factory.CreateAsync();
        db.Set<AppUser>().AddRange(
            new AppUser { Email = Admin, DisplayName = "Ada Admin", IsAdmin = true },
            new AppUser { Email = Finance, DisplayName = "Fin", IsFinance = true },
            new AppUser { Email = Pm, DisplayName = "Pat Morgan" },
            new AppUser { Email = Dev, DisplayName = "Dana Lee" },
            new AppUser { Email = Viewer, DisplayName = "Val Viewer" });
        var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
        db.Set<FiscalYear>().Add(year);
        db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = year.Id, Name = "2026-10", Number = 1, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2026, 10, 31) });
        var category = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        db.Set<LaborCategory>().Add(category);
        var hired = new DateOnly(2024, 1, 1);
        var pm = new Employee { EmployeeNumber = "E1", Email = Pm, DisplayName = "Pat Morgan", DefaultLaborCategoryId = category.Id, HireDate = hired };
        var dev = new Employee { EmployeeNumber = "E2", Email = Dev, DisplayName = "Dana Lee", SupervisorId = pm.Id, DefaultLaborCategoryId = category.Id, HireDate = hired };
        db.Set<Employee>().AddRange(pm, dev);
        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = dev.Id, EffectiveFrom = hired, HourlyCost = 100m });
        var customer = new Customer { Code = "ARMY", Name = "Army" };
        db.Set<Customer>().Add(customer);
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var contract = new Contract
        {
            CustomerId = customer.Id, ContractNumber = "C1", Name = "Support", Status = ContractStatus.Active, PeriodStart = hired, PeriodEnd = today.AddDays(30),
            CeilingValue = 10_000m, FundedValue = 1_000m,
        };
        db.Set<Contract>().Add(contract);
        var project = new Project { Code = "1001", Name = "Modernization", ContractId = contract.Id, ProjectManagerId = pm.Id, Status = ProjectStatus.Active };
        db.Set<Project>().Add(project);
        var leaf = new WbsElement { ProjectId = project.Id, Code = "1001.01", Name = "Work" };
        db.Set<WbsElement>().Add(leaf);
        db.Set<ChargeAssignment>().AddRange(new ChargeAssignment { WbsElementId = leaf.Id, EmployeeId = dev.Id }, new ChargeAssignment { WbsElementId = leaf.Id, EmployeeId = pm.Id });
        var now = DateTime.UtcNow;
        var sheet = new Timesheet { EmployeeId = dev.Id, PeriodStart = new DateOnly(2026, 10, 12), PeriodEnd = new DateOnly(2026, 10, 18), Status = TimesheetStatus.Approved, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Set<Timesheet>().Add(sheet);
        db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = leaf.Id, LaborCategoryId = category.Id, WorkDate = Day, Hours = 8m, EnteredAtUtc = now, UpdatedAtUtc = now });
        await db.SaveChangesAsync();
        (ContractId, ProjectId, LeafId, DevId, PmId) = (contract.Id, project.Id, leaf.Id, dev.Id, pm.Id);
    }

    /// <summary>Saves an automation as the admin.</summary>
    public async Task<AutomationDto> CreateAsync(string name, AutomationTrigger trigger, IReadOnlyList<AutomationAction> actions,
        AutomationConditionGroup? conditions = null, AutomationRunAs runAs = AutomationRunAs.Actor, string? principal = null)
    {
        var user = Host.User.Email;
        As(Admin);
        var created = await Automations.CreateAsync(new SaveAutomationRequest(name, new AutomationDefinition(trigger, conditions, actions, runAs, principal)));
        As(user);
        return created;
    }

    /// <summary>Runs everything queued (and anything those runs queue).</summary>
    public Task<int> ProcessAsync() => Host.Get<AutomationProcessor>().RunOnceAsync(CancellationToken.None);

    public async Task<List<AutomationRun>> RunsAsync(Guid automationId)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<AutomationRun>().AsNoTracking().Where(r => r.AutomationId == automationId).OrderBy(r => r.StartedAtUtc).ToListAsync();
    }

    public async Task<List<Notification>> NotificationsAsync(string kind = AutomationExecutor.NotificationKind)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Notification>().AsNoTracking().Where(n => n.Kind == kind).ToListAsync();
    }

    /// <summary>As dev@: an expense report with lines of the given amounts (no receipts).</summary>
    public async Task<ExpenseReportDto> DevReportAsync(string? purpose, params decimal[] amounts)
    {
        As(Dev);
        var expenses = Host.Get<IExpenseReportService>();
        var report = await expenses.CreateReportAsync(new SaveExpenseReportRequest("Site visit", Day, Day.AddDays(1), purpose));
        foreach (var amount in amounts)
        {
            await expenses.AddLineAsync(report.Id, new SaveExpenseLineRequest(LeafId, OdcCostType.Travel, Day, amount, "Taxi to site", "Yellow Cab"));
        }

        return report;
    }

    public async Task<T> WithDbAsync<T>(Func<IWorkbenchDbContext, Task<T>> work)
    {
        await using var db = await Factory.CreateAsync();
        return await work(db);
    }

    public ValueTask DisposeAsync() => Host.DisposeAsync();

    public static AutomationAction Notify(params AutomationRecipient[] to) =>
        new(AutomationActionKind.Notify, to, "{automation}: {record}", "Funding left: {fundingRemainingPercent}%");

    public static AutomationRecipient Person(string email) => new(RecipientKind.Person, email);
}

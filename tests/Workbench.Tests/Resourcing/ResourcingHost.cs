using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Collaboration;
using Workbench.Application.PerDiem;
using Workbench.Application.Resourcing;
using Workbench.Contracts.Collaboration;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// The real registrations over an in-memory database, a fixed clock (Wednesday 2026-10-14, so this week starts Monday
/// Oct 12), a fake leave calendar and a fake GSA client. Seeded with FY2027 (Oct 2026 – Sep 2027, monthly periods);
/// labor categories SE2 and BA2; pm@ (manages 1001, plan sheet "Plan"), analyst@ and dev@ reporting to pm@, pm2@ (manages
/// 2002), and users for every org role; WBS 1001.01 (root) with leaves 1001.01.01 (task "Design") and 1001.01.02 (task
/// "Build"), 2002.01, and indirect / leave codes.
/// </summary>
internal sealed class ResourcingHost : IAsyncDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 14);
    public static readonly DateOnly ThisWeek = new(2026, 10, 12);

    public const string Admin = "admin@example.com";
    public const string Pm = "pm@example.com";
    public const string Pm2 = "pm2@example.com";
    public const string Analyst = "analyst@example.com";
    public const string Dev = "dev@example.com";
    public const string Finance = "finance@example.com";
    public const string ResourceManager = "rm@example.com";
    public const string Timekeeper = "timekeeper@example.com";

    private readonly ServiceProvider _provider;
    private readonly AsyncServiceScope _scope;

    private ResourcingHost(Action<IServiceCollection>? configure)
    {
        var services = new ServiceCollection();
        services.AddSingleton(TestDb.CreateFactory());
        services.AddSingleton<IBlobStore>(new MemoryBlobStore());
        services.AddSingleton<ICurrentUser>(User);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddWorkbenchApplication();
        services.AddSingleton<TimeProvider>(new FixedClock(new DateTimeOffset(Today.ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero)));
        services.AddSingleton<IResourceLeaveCalendar>(Leave);
        services.AddSingleton<IGsaPerDiemClient>(Gsa);
        configure?.Invoke(services);
        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        _scope = _provider.CreateAsyncScope();
    }

    public TestUser User { get; } = new() { Email = Pm };
    public FakeLeaveCalendar Leave { get; } = new();
    public FakeGsaClient Gsa { get; } = new();

    public Guid PmId { get; private set; }
    public Guid Pm2Id { get; private set; }
    public Guid AnalystId { get; private set; }
    public Guid DevId { get; private set; }
    public Guid Se2 { get; private set; }
    public Guid Ba2 { get; private set; }
    public Guid Project1 { get; private set; }
    public Guid Project2 { get; private set; }
    public Guid Root1 { get; private set; }
    public Guid Design { get; private set; }
    public Guid Build { get; private set; }
    public Guid Wbs2 { get; private set; }
    public Guid IndirectWbs { get; private set; }
    public Guid LeaveWbs { get; private set; }
    public Guid SheetId { get; private set; }
    public Guid OwnerColumn { get; private set; }
    public Guid HoursColumn { get; private set; }

    public T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public IWorkbenchDbContextFactory Db => Get<IWorkbenchDbContextFactory>();

    public ResourcingHost As(string email)
    {
        User.Email = email;
        return this;
    }

    public static async Task<ResourcingHost> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var host = new ResourcingHost(configure);
        await host.SeedAsync();
        return host;
    }

    private async Task SeedAsync()
    {
        await using var db = await Db.CreateAsync();
        var year = new FiscalYear { Year = 2027, Start = new DateOnly(2026, 10, 1), End = new DateOnly(2027, 9, 30) };
        db.Set<FiscalYear>().Add(year);
        for (var i = 0; i < 12; i++)
        {
            var start = year.Start.AddMonths(i);
            db.Set<FiscalPeriod>().Add(new FiscalPeriod { FiscalYearId = year.Id, Number = i + 1, Name = start.ToString("yyyy-MM"), Start = start, End = start.AddMonths(1).AddDays(-1) });
        }

        db.Set<AppUser>().AddRange(
            new AppUser { Email = Admin, DisplayName = "Admin", IsAdmin = true },
            new AppUser { Email = Pm, DisplayName = "Pat" },
            new AppUser { Email = Finance, DisplayName = "Fin", IsFinance = true },
            new AppUser { Email = ResourceManager, DisplayName = "Rae", IsResourceManager = true },
            new AppUser { Email = Timekeeper, DisplayName = "Tim", IsTimekeeper = true });

        var se2 = new LaborCategory { Code = "SE2", Name = "Systems Engineer II" };
        var ba2 = new LaborCategory { Code = "BA2", Name = "Business Analyst II" };
        db.Set<LaborCategory>().AddRange(se2, ba2);
        var org = new OrgUnit { Code = "ENG", Name = "Engineering" };
        db.Set<OrgUnit>().Add(org);
        var hired = new DateOnly(2024, 1, 1);
        Employee Person(string number, string email, string name, LaborCategory cat, Guid? supervisor = null) =>
            new() { EmployeeNumber = number, Email = email, DisplayName = name, DefaultLaborCategoryId = cat.Id, SupervisorId = supervisor, HireDate = hired, OrgUnitId = org.Id };
        var pm = Person("E1", Pm, "Pat Morgan", se2);
        var analyst = Person("E2", Analyst, "Alex Rivera", ba2, pm.Id);
        var dev = Person("E3", Dev, "Dana Lee", se2, pm.Id);
        var pm2 = Person("E4", Pm2, "Quinn Park", se2);
        db.Set<Employee>().AddRange(pm, analyst, dev, pm2);
        db.Set<EmployeeCostRate>().AddRange(
            new EmployeeCostRate { EmployeeId = pm.Id, EffectiveFrom = hired, HourlyCost = 90m },
            new EmployeeCostRate { EmployeeId = analyst.Id, EffectiveFrom = hired, HourlyCost = 50m },
            new EmployeeCostRate { EmployeeId = dev.Id, EffectiveFrom = hired, HourlyCost = 60m },
            new EmployeeCostRate { EmployeeId = dev.Id, EffectiveFrom = new DateOnly(2026, 11, 2), HourlyCost = 70m });

        var workspace = new Workspace { Name = "Program Office" };
        var sheet = new Sheet { Workspace = workspace, Name = "Plan", SchedulingEnabled = true };
        var owner = new Column { Name = "Owner", Type = ColumnType.Person, Order = 0 };
        var hours = new Column { Name = "Planned hours", Type = ColumnType.Number, Order = 1 };
        sheet.Columns.AddRange([owner, hours]);
        var phase = new Item { Title = "Phase 1", Order = 0, Start = new DateOnly(2026, 10, 12), Finish = new DateOnly(2026, 10, 30) };
        var design = new Item { Title = "Design", ParentId = phase.Id, Order = 0, Start = new DateOnly(2026, 10, 12), Finish = new DateOnly(2026, 10, 23) };
        var build = new Item { Title = "Build", ParentId = phase.Id, Order = 1, Start = new DateOnly(2026, 10, 19), Finish = new DateOnly(2026, 10, 30) };
        sheet.Items.AddRange([phase, design, build]);
        db.Workspaces.Add(workspace);
        db.Sheets.Add(sheet);
        db.Set<WorkspaceMember>().Add(new WorkspaceMember { WorkspaceId = workspace.Id, UserEmail = Pm, Role = WorkspaceRole.Owner });

        var customer = new Customer { Code = "ARMY", Name = "Army" };
        var contract = new Contract { CustomerId = customer.Id, ContractNumber = "C1", Name = "Support", PeriodStart = hired, PeriodEnd = hired.AddYears(5), CeilingValue = 1_000_000m, FundedValue = 500_000m };
        db.Set<Customer>().Add(customer);
        db.Set<Contract>().Add(contract);
        var p1 = new Project { Code = "1001", Name = "Modernization", ContractId = contract.Id, ProjectManagerId = pm.Id, SheetId = sheet.Id, Start = hired };
        var p2 = new Project { Code = "2002", Name = "Other", ProjectManagerId = pm2.Id, Start = hired };
        var ind = new Project { Code = "IND", Name = "Indirect", Start = hired };
        db.Set<Project>().AddRange(p1, p2, ind);
        var root = new WbsElement { ProjectId = p1.Id, Code = "1001.01", Name = "Modernization", IsChargeable = false, ItemId = phase.Id };
        var designWbs = new WbsElement { ProjectId = p1.Id, ParentId = root.Id, Code = "1001.01.01", Name = "Design", ItemId = design.Id, Order = 0 };
        var buildWbs = new WbsElement { ProjectId = p1.Id, ParentId = root.Id, Code = "1001.01.02", Name = "Build", ItemId = build.Id, Order = 1 };
        var w2 = new WbsElement { ProjectId = p2.Id, Code = "2002.01", Name = "Other work" };
        var indirect = new WbsElement { ProjectId = ind.Id, Code = "IND.OH", Name = "Overhead", Kind = ChargeKind.Indirect, RequiresAssignment = false };
        var leave = new WbsElement { ProjectId = ind.Id, Code = "LV.PTO", Name = "PTO", Kind = ChargeKind.Leave, RequiresAssignment = false };
        db.Set<WbsElement>().AddRange(root, designWbs, buildWbs, w2, indirect, leave);
        foreach (var e in new[] { pm, analyst, dev })
        {
            db.Set<ChargeAssignment>().AddRange(
                new ChargeAssignment { WbsElementId = designWbs.Id, EmployeeId = e.Id },
                new ChargeAssignment { WbsElementId = buildWbs.Id, EmployeeId = e.Id });
        }

        await db.SaveChangesAsync();
        (PmId, Pm2Id, AnalystId, DevId, Se2, Ba2) = (pm.Id, pm2.Id, analyst.Id, dev.Id, se2.Id, ba2.Id);
        (Project1, Project2, Root1, Design, Build, Wbs2, IndirectWbs, LeaveWbs) = (p1.Id, p2.Id, root.Id, designWbs.Id, buildWbs.Id, w2.Id, indirect.Id, leave.Id);
        (SheetId, OwnerColumn, HoursColumn) = (sheet.Id, owner.Id, hours.Id);
    }

    /// <summary>Adds an approved timesheet with entries (employee, WBS, labor category, date, hours).</summary>
    public async Task AddApprovedTimeAsync(Guid employeeId, params (Guid Wbs, Guid Category, DateOnly Date, decimal Hours)[] entries)
    {
        await using var db = await Db.CreateAsync();
        var now = DateTime.UtcNow;
        foreach (var week in entries.GroupBy(e => e.Date.AddDays(-(((int)e.Date.DayOfWeek + 6) % 7))))
        {
            var sheet = new Timesheet { EmployeeId = employeeId, PeriodStart = week.Key, PeriodEnd = week.Key.AddDays(6), Status = TimesheetStatus.Approved, CreatedAtUtc = now, UpdatedAtUtc = now };
            db.Set<Timesheet>().Add(sheet);
            foreach (var e in week)
            {
                db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = e.Wbs, LaborCategoryId = e.Category, WorkDate = e.Date, Hours = e.Hours, EnteredAtUtc = now, UpdatedAtUtc = now });
            }
        }

        await db.SaveChangesAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _scope.DisposeAsync();
        await _provider.DisposeAsync();
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

internal sealed class FakeLeaveCalendar : IResourceLeaveCalendar
{
    public List<HolidayDay> Holidays { get; } = [];
    public List<ApprovedLeaveDay> Leave { get; } = [];

    public Task<IReadOnlyList<HolidayDay>> GetHolidaysAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<HolidayDay>>(Holidays.Where(h => h.Date >= from && h.Date <= to).ToList());

    public Task<IReadOnlyList<ApprovedLeaveDay>> GetApprovedLeaveAsync(DateOnly from, DateOnly to, IReadOnlyCollection<Guid> employeeIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<ApprovedLeaveDay>>(Leave.Where(l => l.Date >= from && l.Date <= to && employeeIds.Contains(l.EmployeeId)).ToList());
}

internal sealed class FakeGsaClient : IGsaPerDiemClient
{
    public bool IsConfigured { get; set; } = true;
    public List<GsaLodgingRate> Lodging { get; } = [];
    public List<ParsedMieTier> Tiers { get; } = [];
    public int Calls { get; private set; }

    public Task<IReadOnlyList<GsaLodgingRate>> GetConusLodgingAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<GsaLodgingRate>>(Lodging);
    }

    public Task<IReadOnlyList<ParsedMieTier>> GetMieBreakdownAsync(int fiscalYear, CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<ParsedMieTier>>(Tiers);
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.IndirectRates;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.IndirectRates;

/// <summary>
/// A small, hand-checkable ledger: FY2026 and FY2027 calendars (Oct–Sep), one employee at $100/h, contract project
/// 2001 with direct elements 2001.01 (on a CLIN) and 2001.02, an indirect project with IND.OH, the default chart and pools.
/// </summary>
internal sealed class IndirectRatesKit
{
    public static readonly DateOnly October = new(2026, 10, 1);

    private IndirectRatesKit(IWorkbenchDbContextFactory factory, TestUser user, FixedClock clock, LedgerKit gl)
    {
        Factory = factory;
        User = user;
        Clock = clock;
        Gl = gl;
        Audit = new AuditRecorder(user, clock);
        Engine = new AllocationEngine(gl.Ledger, Audit);
        Rates = new IndirectRateService(factory, Engine, Audit, clock);
        Burdened = new BurdenedCostService(factory);
        Close = new Workbench.Application.Accounting.PeriodCloseService(factory, gl.Engine, Audit, [new AllocationCloseCheck(Engine)]);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; }
    public FixedClock Clock { get; }
    public LedgerKit Gl { get; }
    public AuditRecorder Audit { get; }
    public AllocationEngine Engine { get; }
    public IndirectRateService Rates { get; }
    public BurdenedCostService Burdened { get; }
    public Workbench.Application.Accounting.PeriodCloseService Close { get; }

    public Employee Employee { get; private set; } = default!;
    public Project Project { get; private set; } = default!;
    public Contract Contract { get; private set; } = default!;
    public WbsElement Task1 { get; private set; } = default!;
    public WbsElement Task2 { get; private set; } = default!;
    public WbsElement Overhead { get; private set; } = default!;
    public Guid ClinId { get; } = Guid.NewGuid();

    public LaborCategory Category { get; } = new() { Code = "ENG2", Name = "Engineer II" };
    public Dictionary<string, FiscalPeriod> Periods { get; } = [];

    public FiscalPeriod Oct => Periods["2026-10"];

    public static async Task<IndirectRatesKit> CreateAsync(bool installPools = true)
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser { Email = "finance@example.com" };
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var gl = await LedgerKit.CreateAsync(factory, user, clock);
        var kit = new IndirectRatesKit(factory, user, clock, gl);
        await kit.SeedAsync();
        if (installPools)
        {
            Assert.Equal(3, await kit.Rates.InstallDefaultPoolsAsync());
        }

        return kit;
    }

    private async Task SeedAsync()
    {
        await using var db = await Factory.CreateAsync();
        foreach (var start in new[] { new DateOnly(2025, 10, 1), October })
        {
            var year = new FiscalYear { Year = start.Year + 1, Start = start, End = start.AddYears(1).AddDays(-1) };
            db.Set<FiscalYear>().Add(year);
            for (var i = 0; i < 12; i++)
            {
                var p = new FiscalPeriod { FiscalYearId = year.Id, Number = i + 1, Name = $"{start.AddMonths(i):yyyy-MM}", Start = start.AddMonths(i), End = start.AddMonths(i + 1).AddDays(-1) };
                db.Set<FiscalPeriod>().Add(p);
                Periods[p.Name] = p;
            }
        }

        Employee = new Employee { EmployeeNumber = "E1", Email = "dana@example.com", DisplayName = "Dana", HireDate = new DateOnly(2020, 1, 1) };
        var customer = new Customer { Code = "ARMY", Name = "U.S. Army" };
        Contract = new Contract { ContractNumber = "W91-26-C-0001", Name = "Modernization", CustomerId = customer.Id, PeriodStart = new DateOnly(2025, 1, 1), PeriodEnd = new DateOnly(2028, 1, 1) };
        var clin = new Clin { Id = ClinId, ContractId = Contract.Id, Number = "0001", Description = "Engineering", PeriodStart = Contract.PeriodStart, PeriodEnd = Contract.PeriodEnd };
        Project = new Project { Code = "2001", Name = "Modernization", ContractId = Contract.Id };
        Task1 = new WbsElement { ProjectId = Project.Id, Code = "2001.01", Name = "Design", ClinId = ClinId };
        Task2 = new WbsElement { ProjectId = Project.Id, Code = "2001.02", Name = "Build", Order = 1 };
        var indirect = new Project { Code = "IND", Name = "Indirect" };
        Overhead = new WbsElement { ProjectId = indirect.Id, Code = "IND.OH", Name = "Overhead", Kind = ChargeKind.Indirect, RequiresAssignment = false };
        db.Set<LaborCategory>().Add(Category);
        db.Set<Employee>().Add(Employee);
        db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = Employee.Id, EffectiveFrom = new DateOnly(2020, 1, 1), HourlyCost = 100m });
        db.Set<Customer>().Add(customer);
        db.Set<Contract>().Add(Contract);
        db.Set<Clin>().Add(clin);
        db.Set<Project>().AddRange(Project, indirect);
        db.Set<WbsElement>().AddRange(Task1, Task2, Overhead);
        await db.SaveChangesAsync();
    }

    /// <summary>An approved one-week timesheet with the given hours.</summary>
    public async Task AddTimeAsync(params (WbsElement Wbs, DateOnly Date, decimal Hours)[] entries)
    {
        await using var db = await Factory.CreateAsync();
        var start = entries.Min(e => e.Date);

        // One timesheet per employee and period (a unique index): more hours for a period go on the same sheet.
        var sheet = await db.Set<Timesheet>().FirstOrDefaultAsync(t => t.EmployeeId == Employee.Id && t.PeriodStart == start);
        if (sheet is null)
        {
            sheet = new Timesheet { EmployeeId = Employee.Id, PeriodStart = start, PeriodEnd = start.AddDays(6), Status = TimesheetStatus.Approved, ApprovedAtUtc = DateTime.UtcNow };
            db.Set<Timesheet>().Add(sheet);
        }

        db.Set<TimeEntry>().AddRange(entries.Select(e => new TimeEntry { TimesheetId = sheet.Id, WbsElementId = e.Wbs.Id, WorkDate = e.Date, Hours = e.Hours, LaborCategoryId = Category.Id }));
        await db.SaveChangesAsync();
    }

    public async Task AddOdcAsync(WbsElement wbs, DateOnly on, decimal amount, bool unallowable = false)
    {
        await using var db = await Factory.CreateAsync();
        db.Set<OdcEntry>().Add(new OdcEntry
        {
            ProjectId = wbs.ProjectId, WbsElementId = wbs.Id, Type = OdcType.Other, IncurredOn = on, Amount = amount, Description = unallowable ? "Team dinner" : "Software",
            Vendor = "Acme", IsUnallowable = unallowable, UnallowableReason = unallowable ? "Alcohol" : null, Status = OdcStatus.Approved, SubmittedBy = "dana@example.com",
        });
        await db.SaveChangesAsync();
    }

    public Task SetRateAsync(CostPool pool, int year, RateBasis basis, decimal rate, string? reason = null) =>
        Rates.SaveRateAsync(new SaveIndirectRateRequest(pool, year, basis, rate, Reason: reason, Approve: true));

    public async Task SetRatesAsync(int year, RateBasis basis, decimal fringe, decimal overhead, decimal ga)
    {
        await SetRateAsync(CostPool.Fringe, year, basis, fringe);
        await SetRateAsync(CostPool.Overhead, year, basis, overhead);
        await SetRateAsync(CostPool.GeneralAndAdministrative, year, basis, ga);
    }

    /// <summary>
    /// The worked example: in October 2026, 10 h on 2001.01 ($1,000 direct labor), a $500 allowable ODC and a $200
    /// unallowable ODC on it; FY2027 provisional rates fringe 30%, overhead 50%, G&amp;A 10%.
    /// Fringe 1,000 × 30% = 300; overhead (1,000 + 300) × 50% = 650; G&amp;A (1,000 + 300 + 650 + 500) × 10% = 245.
    /// </summary>
    public async Task WorkedExampleAsync()
    {
        await AddTimeAsync((Task1, October.AddDays(5), 10m));
        await AddOdcAsync(Task1, October.AddDays(6), 500m);
        await AddOdcAsync(Task1, October.AddDays(7), 200m, unallowable: true);
        await SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
    }

    public async Task CloseAsync(FiscalPeriod period)
    {
        await using var db = await Factory.CreateAsync();
        var p = await db.Set<FiscalPeriod>().SingleAsync(x => x.Id == period.Id);
        p.Status = PeriodStatus.Closed;
        await db.SaveChangesAsync();
    }
}

internal sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

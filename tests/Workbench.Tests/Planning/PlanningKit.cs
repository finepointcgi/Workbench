using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Planning;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Accounting;
using Workbench.Tests.Collaboration;

namespace Workbench.Tests.Planning;

/// <summary>A clock tests can move.</summary>
internal sealed class PlanningClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;

    public override DateTimeOffset GetUtcNow() => Now;

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

/// <summary>
/// The planning services over a test database: FY2026 and FY2027 (Oct–Sep), org units OPS and ENG, the default chart,
/// and "today" 2026-11-15 (FY2027 period 2).
/// </summary>
internal sealed class PlanningKit
{
    private PlanningKit(IWorkbenchDbContextFactory factory, TestUser user, PlanningClock clock, LedgerKit gl)
    {
        Factory = factory;
        User = user;
        Clock = clock;
        Gl = gl;
        Audit = new AuditRecorder(user, clock);
        Recurring = new RecurringJournalService(factory, gl.Ledger, Audit, clock);
        Budgets = new GlBudgetService(factory, Audit, clock);
        Reports = new BudgetReportService(factory, clock);
    }

    public IWorkbenchDbContextFactory Factory { get; }
    public TestUser User { get; }
    public PlanningClock Clock { get; }
    public LedgerKit Gl { get; }
    public AuditRecorder Audit { get; }
    public RecurringJournalService Recurring { get; }
    public GlBudgetService Budgets { get; }
    public BudgetReportService Reports { get; }
    public Dictionary<string, FiscalPeriod> Periods { get; } = [];
    public Guid Ops { get; private set; }
    public Guid Eng { get; private set; }

    public static async Task<PlanningKit> CreateAsync()
    {
        var factory = TestDb.CreateFactory();
        var user = new TestUser { Email = "finance@example.com" };
        var clock = new PlanningClock(new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero));
        var gl = await LedgerKit.CreateAsync(factory, user, clock);
        var kit = new PlanningKit(factory, user, clock, gl);
        await kit.SeedAsync();
        return kit;
    }

    public Guid this[string account] => Gl[account];

    private async Task SeedAsync()
    {
        await using var db = await Factory.CreateAsync();
        foreach (var start in new[] { new DateOnly(2025, 10, 1), new DateOnly(2026, 10, 1) })
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

        var ops = new OrgUnit { Code = "OPS", Name = "Operations" };
        var eng = new OrgUnit { Code = "ENG", Name = "Engineering", ParentId = ops.Id };
        db.Set<OrgUnit>().AddRange(ops, eng);
        await db.SaveChangesAsync();
        (Ops, Eng) = (ops.Id, eng.Id);
    }

    public async Task SetPeriodStatusAsync(string name, PeriodStatus status)
    {
        await using var db = await Factory.CreateAsync();
        var period = await db.Set<FiscalPeriod>().FirstAsync(p => p.Id == Periods[name].Id);
        period.Status = status;
        await db.SaveChangesAsync();
    }

    /// <summary>Posts a manual entry: (account, org, debit, credit) lines.</summary>
    public async Task PostAsync(DateOnly date, params (string Account, Guid? Org, decimal Debit, decimal Credit)[] lines) =>
        await Gl.Journal.CreateAndPostAsync(new SaveJournalEntryRequest(date, "Actual",
            lines.Select(l => new JournalLineInput(Gl[l.Account], l.Debit, l.Credit, OrgUnitId: l.Org)).ToList()));
}

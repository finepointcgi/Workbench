using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Contracts.Leave;
using Workbench.Contracts.MyTodo;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using Workbench.Tests.Time;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.MyTodo;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (pm@ manages 1001 and supervises analyst@ and
/// viewer@; each has an approved timesheet for Oct 12–18) with the clock at noon UTC on Wed 2026-10-21, weekly periods
/// (this one runs Mon Oct 19 – Sun Oct 25) and a PTO policy that may go negative.
/// </summary>
internal sealed class MyTodoKit : IAsyncDisposable
{
    public static readonly DateOnly Today = new(2026, 10, 21);
    public static readonly DateOnly Monday = new(2026, 10, 19);

    private MyTodoKit(SeededHost seeded, ProjectAccountingFixture fx, TestClock clock)
    {
        Seeded = seeded;
        Fx = fx;
        Clock = clock;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public TestClock Clock { get; }
    public LeavePolicyDto Pto { get; private set; } = default!;
    public Guid CategoryId { get; private set; }

    public IMyTodoService Service => Get<IMyTodoService>();

    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public MyTodoKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public Task<MyTodoDto> GetAsync(string email) => As(email).Service.GetAsync();

    public static async Task<MyTodoKit> CreateAsync(Action<IServiceCollection>? configure = null)
    {
        var clock = new TestClock(new DateTime(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc));
        var seeded = await SeededHost.CreateAsync(services =>
        {
            services.AddSingleton<TimeProvider>(clock);
            configure?.Invoke(services);
        });
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new MyTodoKit(seeded, fx, clock);
        Guid ptoWbs;
        await using (var db = await kit.Factory.CreateAsync())
        {
            var indirect = new Project { Code = "IND", Name = "Indirect" };
            var pto = new WbsElement { ProjectId = indirect.Id, Code = "LV.PTO", Name = "Paid time off", Kind = ChargeKind.Leave, RequiresAssignment = false };
            db.Set<Project>().Add(indirect);
            db.Set<WbsElement>().Add(pto);
            await db.SaveChangesAsync();
            ptoWbs = pto.Id;
            kit.CategoryId = await db.Set<LaborCategory>().Select(c => c.Id).FirstAsync();
        }

        kit.As(Admin);
        kit.Pto = await kit.Get<ILeavePolicyService>().CreateAsync(new SaveLeavePolicyRequest("PTO", "Paid time off", LeaveTypeKind.Pto, ptoWbs,
            AccrualMethodKind.None, [], AllowNegativeBalance: true));
        return kit;
    }

    /// <summary>A timesheet for <paramref name="employeeId"/> for the week starting <paramref name="weekStart"/>, with hours on the given days.</summary>
    public async Task<Guid> TimesheetAsync(Guid employeeId, DateOnly weekStart, TimesheetStatus status, string? rejection = null, params (DateOnly Day, decimal Hours)[] hours)
    {
        await using var db = await Factory.CreateAsync();
        var sheet = new Timesheet { EmployeeId = employeeId, PeriodStart = weekStart, PeriodEnd = weekStart.AddDays(6), Status = status, RejectionReason = rejection };
        db.Set<Timesheet>().Add(sheet);
        foreach (var (day, h) in hours)
        {
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = Fx.Wbs1, LaborCategoryId = CategoryId, WorkDate = day, Hours = h });
        }

        await db.SaveChangesAsync();
        return sheet.Id;
    }

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}

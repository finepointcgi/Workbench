using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.Leave;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Leave;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (analyst@, viewer@ and outsider@ have approved
/// hours on Wed 2026-10-14; analyst@ and viewer@ report to pm@) with the default chart, an indirect project with leave
/// codes LV.PTO and LV.HOL, and the pay calendar and pay profiles the leave engine reads, written straight to the core records (no payroll module in
/// the shipped product): a biweekly pay group whose first period runs Mon 2026-10-05 to Sun 2026-10-18, and profiles for pm@, analyst@ and viewer@
/// in it. outsider@ isn't in the group. Calls run as finance@ unless switched.
/// </summary>
internal sealed class LeaveKit : IAsyncDisposable
{
    public static readonly DateOnly Day = ProjectAccountingFixture.Day;
    public static readonly DateOnly PeriodStart = new(2026, 10, 5);

    private LeaveKit(SeededHost seeded, ProjectAccountingFixture fx)
    {
        Seeded = seeded;
        Fx = fx;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public Guid PtoWbs { get; private set; }
    public Guid HolidayWbs { get; private set; }
    public Guid LeaveProject { get; private set; }
    public PayGroup Group { get; private set; } = default!;
    public PayPeriod Period { get; private set; } = default!;
    public Guid CategoryId { get; private set; }

    public ILeavePolicyService Policies => Get<ILeavePolicyService>();
    public ILeaveBalanceService Balances => Get<ILeaveBalanceService>();
    public ILeaveRequestService Requests => Get<ILeaveRequestService>();
    public IHolidayService Holidays => Get<IHolidayService>();
    public ILeaveTimesheetService LeaveTimesheets => Get<ILeaveTimesheetService>();
    public ITimesheetService Timesheets => Get<ITimesheetService>();
    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public LeaveKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public static async Task<LeaveKit> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new LeaveKit(seeded, fx);
        kit.As(Finance);
        await kit.Get<IChartOfAccountsService>().InstallDefaultChartAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            var indirect = new Project { Code = "IND", Name = "Indirect" };
            db.Set<Project>().Add(indirect);
            var root = new WbsElement { ProjectId = indirect.Id, Code = "LV", Name = "Leave", Kind = ChargeKind.Leave, IsChargeable = false, RequiresAssignment = false };
            var pto = new WbsElement { ProjectId = indirect.Id, ParentId = root.Id, Code = "LV.PTO", Name = "Paid time off", Kind = ChargeKind.Leave, RequiresAssignment = false };
            var hol = new WbsElement { ProjectId = indirect.Id, ParentId = root.Id, Code = "LV.HOL", Name = "Holiday", Kind = ChargeKind.Leave, RequiresAssignment = false };
            db.Set<WbsElement>().AddRange(root, pto, hol);
            await db.SaveChangesAsync();
            (kit.PtoWbs, kit.HolidayWbs, kit.LeaveProject) = (pto.Id, hol.Id, indirect.Id);
            kit.CategoryId = await db.Set<LaborCategory>().Select(c => c.Id).FirstAsync();
        }

        kit.Group = await kit.AddGroupAsync("BW", PayFrequency.Biweekly, PeriodStart);
        kit.Period = await kit.PeriodAsync(PeriodStart);
        foreach (var (employee, type) in new[] { (fx.PmId, PayType.Salaried), (fx.AnalystId, PayType.Hourly), (fx.ViewerId, PayType.Hourly) })
        {
            await kit.SetProfileAsync(employee, type);
        }

        return kit;
    }

    /// <summary>A pay group with biweekly-style periods (14 days from <paramref name="anchor"/>, four of them), written to the core pay calendar records.</summary>
    public async Task<PayGroup> AddGroupAsync(string code, PayFrequency frequency, DateOnly anchor)
    {
        await using var db = await Factory.CreateAsync();
        var group = new PayGroup { Code = code, Name = code, Frequency = frequency, ProviderKey = "paylocity", CompanyCode = "W1234", AnchorDate = anchor, CreatedAtUtc = DateTime.UtcNow };
        db.Set<PayGroup>().Add(group);
        for (var n = 0; n < 4; n++)
        {
            var start = anchor.AddDays(14 * n);
            db.Set<PayPeriod>().Add(new PayPeriod { PayGroupId = group.Id, Year = start.Year, Number = n + 1, Start = start, End = start.AddDays(13), CheckDate = start.AddDays(18) });
        }

        await db.SaveChangesAsync();
        return group;
    }

    public async Task<PayPeriod> PeriodAsync(DateOnly start)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<PayPeriod>().AsNoTracking().SingleAsync(p => p.PayGroupId == Group.Id && p.Start == start);
    }

    /// <summary>The employee's pay profile (what leave eligibility reads: pay group and employment type), saved or changed.</summary>
    public async Task SetProfileAsync(Guid employeeId, PayType type = PayType.Hourly, EmploymentType employment = EmploymentType.FullTime)
    {
        await using var db = await Factory.CreateAsync();
        var profile = await db.Set<EmployeePayProfile>().FirstOrDefaultAsync(p => p.EmployeeId == employeeId);
        if (profile is null)
        {
            profile = new EmployeePayProfile { EmployeeId = employeeId, EffectiveFrom = PeriodStart, UpdatedAtUtc = DateTime.UtcNow };
            db.Set<EmployeePayProfile>().Add(profile);
        }

        (profile.PayGroupId, profile.PayType, profile.EmploymentType) = (Group.Id, type, employment);
        await db.SaveChangesAsync();
    }

    /// <summary>Adds approved hours to an employee's timesheet for the week containing the dates (created approved if missing).</summary>
    public async Task AddApprovedHoursAsync(Guid employeeId, Guid wbsId, params (DateOnly Date, decimal Hours)[] hours)
    {
        await using var db = await Factory.CreateAsync();
        foreach (var week in hours.GroupBy(h => h.Date.AddDays(-(((int)h.Date.DayOfWeek + 6) % 7))))
        {
            var sheet = await db.Set<Timesheet>().FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.PeriodStart == week.Key);
            if (sheet is null)
            {
                sheet = new Timesheet { EmployeeId = employeeId, PeriodStart = week.Key, PeriodEnd = week.Key.AddDays(6), Status = TimesheetStatus.Approved };
                db.Set<Timesheet>().Add(sheet);
            }

            foreach (var (date, h) in week)
            {
                db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbsId, LaborCategoryId = CategoryId, WorkDate = date, Hours = h });
            }
        }

        await db.SaveChangesAsync();
    }

    public async Task SetTimesheetStatusAsync(Guid employeeId, DateOnly weekStart, TimesheetStatus status)
    {
        await using var db = await Factory.CreateAsync();
        var sheet = await db.Set<Timesheet>().FirstOrDefaultAsync(t => t.EmployeeId == employeeId && t.PeriodStart == weekStart);
        if (sheet is null)
        {
            db.Set<Timesheet>().Add(new Timesheet { EmployeeId = employeeId, PeriodStart = weekStart, PeriodEnd = weekStart.AddDays(6), Status = status });
        }
        else
        {
            sheet.Status = status;
        }

        await db.SaveChangesAsync();
    }

    public async Task UpdateEmployeeAsync(Guid employeeId, Action<Employee> change)
    {
        await using var db = await Factory.CreateAsync();
        change(await db.Set<Employee>().FirstAsync(e => e.Id == employeeId));
        await db.SaveChangesAsync();
    }

    public async Task<LeavePolicyDto> PtoPolicyAsync(
        AccrualMethodKind method = AccrualMethodKind.PerPayPeriod, IReadOnlyList<AccrualTierDto>? tiers = null, decimal? cap = null, decimal? carryover = null,
        int waiting = 0, bool allowNegative = false)
    {
        var previous = Seeded.Host.User.Email;
        As(Admin);
        var pto = await Policies.CreateAsync(new SaveLeavePolicyRequest("PTO", "Paid time off", LeaveTypeKind.Pto, PtoWbs, method,
            tiers ?? [new(0, 4m), new(5, 6m)], cap, carryover, waiting, allowNegative));
        await Policies.CreateAsync(new SaveLeavePolicyRequest("HOL", "Holiday", LeaveTypeKind.Holiday, HolidayWbs, AccrualMethodKind.None, [], AllowNegativeBalance: true));
        As(previous);
        return pto;
    }

    public async Task<decimal> LedgerHoursAsync(Guid employeeId, Guid policyId)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<LeaveLedgerEntry>().Where(l => l.EmployeeId == employeeId && l.PolicyId == policyId).SumAsync(l => l.Hours);
    }


    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Leave;
using Workbench.Contracts.Payroll;
using Workbench.Contracts.Time;
using Workbench.Domain.Accounting;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Payroll;

/// <summary>
/// The real registrations over <see cref="ProjectAccountingFixture"/> (analyst@, viewer@ and outsider@ have approved
/// hours on Wed 2026-10-14; analyst@ and viewer@ report to pm@) with the default chart, an indirect project with leave
/// codes LV.PTO and LV.HOL, a biweekly Paylocity pay group whose period runs Mon 2026-10-05 to Sun 2026-10-18 (check
/// date 10-23), and payroll profiles: pm@ salaried 104,000 (id 1001), analyst@ hourly 50 (2002), viewer@ hourly 30
/// (3003). outsider@ isn't in the group. Calls run as finance@ unless switched.
/// </summary>
internal sealed class PayrollKit : IAsyncDisposable
{
    public static readonly DateOnly Day = ProjectAccountingFixture.Day;
    public static readonly DateOnly PeriodStart = new(2026, 10, 5);

    private PayrollKit(SeededHost seeded, ProjectAccountingFixture fx)
    {
        Seeded = seeded;
        Fx = fx;
    }

    public SeededHost Seeded { get; }
    public ProjectAccountingFixture Fx { get; }
    public Guid PtoWbs { get; private set; }
    public Guid HolidayWbs { get; private set; }
    public Guid LeaveProject { get; private set; }
    public PayGroupDto Group { get; private set; } = default!;
    public PayPeriodDto Period { get; private set; } = default!;
    public Guid CategoryId { get; private set; }

    public IPayGroupService Groups => Get<IPayGroupService>();
    public IPayrollProfileService Profiles => Get<IPayrollProfileService>();
    public IPayrollProviderService Providers => Get<IPayrollProviderService>();
    public IPayrollRunService Runs => Get<IPayrollRunService>();
    public ILeavePolicyService Policies => Get<ILeavePolicyService>();
    public ILeaveBalanceService Balances => Get<ILeaveBalanceService>();
    public ILeaveRequestService Requests => Get<ILeaveRequestService>();
    public IHolidayService Holidays => Get<IHolidayService>();
    public ILeaveTimesheetService LeaveTimesheets => Get<ILeaveTimesheetService>();
    public ITimesheetService Timesheets => Get<ITimesheetService>();
    public IWorkbenchDbContextFactory Factory => Get<IWorkbenchDbContextFactory>();

    public T Get<T>() where T : notnull => Seeded.Host.Get<T>();

    public PayrollKit As(string email)
    {
        Seeded.As(email);
        return this;
    }

    public static async Task<PayrollKit> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        var kit = new PayrollKit(seeded, fx);
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

        kit.Group = await kit.Groups.CreateAsync(new SavePayGroupRequest("BW", "Biweekly", PayFrequencyKind.Biweekly, "paylocity", "W1234", PeriodStart, 5));
        await kit.Groups.GeneratePeriodsAsync(kit.Group.Id, 2026);
        kit.Period = (await kit.Groups.ListPeriodsAsync(kit.Group.Id, 2026)).Single(p => p.Start == PeriodStart);
        await kit.Profiles.SaveAsync(fx.PmId, new SavePayProfileRequest(kit.Group.Id, "1001", PayTypeKind.Salaried, AnnualSalary: 104_000m));
        await kit.Profiles.SaveAsync(fx.AnalystId, new SavePayProfileRequest(kit.Group.Id, "2002", PayTypeKind.Hourly, HourlyRate: 50m));
        await kit.Profiles.SaveAsync(fx.ViewerId, new SavePayProfileRequest(kit.Group.Id, "3003", PayTypeKind.Hourly, HourlyRate: 30m));
        return kit;
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

    /// <summary>A PTO policy on LV.PTO (per pay period unless changed) and a Holiday policy on LV.HOL, as admin@.</summary>
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

    public async Task<List<JournalEntry>> PayrollEntriesAsync()
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
            .Where(e => e.Source == JournalSource.Payroll || e.Source == JournalSource.Reversal).OrderBy(e => e.Sequence).ToListAsync();
    }

    public async Task<Dictionary<Guid, string>> AccountNumbersAsync()
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, a => a.Number);
    }

    public async Task<decimal> LedgerHoursAsync(Guid employeeId, Guid policyId)
    {
        await using var db = await Factory.CreateAsync();
        return await db.Set<LeaveLedgerEntry>().Where(l => l.EmployeeId == employeeId && l.PolicyId == policyId).SumAsync(l => l.Hours);
    }

    public const string Register = """
        Company Code,Employee Id,Employee Name,Check Date,Gross Pay,EE Taxes,ER Taxes,Deductions,ER Benefits,Net Pay
        W1234,2002,"Rivera, Alex",10/23/2026,400.00,60.00,30.60,20.00,50.00,320.00
        W1234,1001,"Morgan, Pat",10/23/2026,"4,000.00",800.00,306.00,150.00,400.00,"3,050.00"
        """;

    public ValueTask DisposeAsync() => Seeded.DisposeAsync();
}

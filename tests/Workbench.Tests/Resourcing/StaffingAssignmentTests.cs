using Microsoft.EntityFrameworkCore;
using Workbench.Application.Resourcing;
using Workbench.Contracts;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Resourcing;
using Workbench.Domain.Leave;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Resourcing;
using static Workbench.Tests.Resourcing.ResourcingHost;

namespace Workbench.Tests.Resourcing;

/// <summary>
/// Assigning people by total hours (like Unanet project assignments): the even spread over working days, editing by week,
/// hours left for the timesheet, pasting from Excel, copying a team, who may do it, and that it lets people charge.
/// Today is Wed Oct 14 2026; Design runs Oct 12–23 and Build Oct 19–30 on the plan.
/// </summary>
public sealed class StaffingAssignmentTests
{
    private static bool Weekday(DateOnly d) => d.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);

    [Fact]
    public void Spread_skips_holidays_and_rounds_each_week_to_the_quarter_hour_adding_up_to_the_total()
    {
        var thanksgiving = new DateOnly(2026, 11, 26);

        var weeks = AssignmentSpread.Spread(400m, new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 27), d => Weekday(d) && d != thanksgiving);

        // 19 working days at 21.05 h: three full weeks, then a four-day week.
        Assert.Equal([105.25m, 105.25m, 105.25m, 84.25m], weeks.Select(w => w.Hours));
        Assert.Equal([new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 9), new DateOnly(2026, 11, 16), new DateOnly(2026, 11, 23)], weeks.Select(w => w.WeekStart));
        Assert.Equal(400m, weeks.Sum(w => w.Hours));
    }

    [Fact]
    public void Partial_first_and_last_weeks_get_only_their_days_share()
    {
        // Wed Oct 14 – Tue Oct 27: 3 + 5 + 2 working days.
        var weeks = AssignmentSpread.Spread(100m, new DateOnly(2026, 10, 14), new DateOnly(2026, 10, 27), Weekday);

        Assert.Equal([30m, 50m, 20m], weeks.Select(w => w.Hours));
        Assert.Equal(ThisWeek, weeks[0].WeekStart);
    }

    [Theory]
    [InlineData(10, 16, 26)]
    [InlineData(37.5, 12, 30)]
    [InlineData(1000, 12, 30)]
    [InlineData(0.25, 12, 30)]
    public void Rounding_never_front_loads_and_always_adds_up(decimal total, int fromDay, int toDay)
    {
        var weeks = AssignmentSpread.Spread(total, new DateOnly(2026, 10, fromDay), new DateOnly(2026, 10, toDay), Weekday);

        Assert.Equal(total, weeks.Sum(w => w.Hours));
        Assert.All(weeks, w => Assert.Equal(0m, w.Hours * 4 % 1)); // quarter hours
        var full = weeks.Where(w => Enumerable.Range(0, 5).All(i => w.WeekStart.AddDays(i).Day >= fromDay && w.WeekStart.AddDays(i).Day <= toDay)).Select(w => w.Hours).ToList();
        Assert.True(full.Count < 2 || full.Max() - full.Min() <= 0.25m, "full weeks get the same hours, give or take a quarter hour");
    }

    [Fact]
    public void Fixed_weeks_keep_their_hours_and_the_rest_is_spread_over_the_others()
    {
        var weeks = AssignmentSpread.Spread(100m, new DateOnly(2026, 10, 5), new DateOnly(2026, 10, 23), Weekday,
            new Dictionary<DateOnly, decimal> { [new DateOnly(2026, 10, 5)] = 40m });

        Assert.Equal([40m, 30m, 30m], weeks.Select(w => w.Hours));
    }

    [Fact]
    public void A_range_with_no_working_days_is_refused() =>
        Assert.Throws<ArgumentException>(() => AssignmentSpread.Spread(8m, new DateOnly(2026, 10, 17), new DateOnly(2026, 10, 18), Weekday));

    [Fact]
    public async Task Assigning_by_total_uses_the_tasks_dates_skips_company_holidays_and_writes_weekly_staffing()
    {
        await using var host = await CreateAsync();
        await AddHolidayAsync(host, new DateOnly(2026, 10, 26));
        var service = host.As(Pm).Get<IStaffingAssignmentService>();

        var saved = await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));

        // Build runs Mon Oct 19 – Fri Oct 30; Mon Oct 26 is a holiday: 5 + 4 days of 8.89 h.
        Assert.Equal((new DateOnly(2026, 10, 19), new DateOnly(2026, 10, 30), 80m), (saved.From, saved.To, saved.TotalHours));
        Assert.Equal([44.5m, 35.5m], saved.Weeks.Select(w => w.Hours));
        Assert.Equal((host.Se2, "SE2 Systems Engineer II"), (saved.LaborCategoryId, saved.LaborCategory)); // Dana's own category

        await using var db = await host.Db.CreateAsync();
        var rows = await db.Set<ResourceAllocation>().Where(a => a.AssignmentId == saved.Id).OrderBy(a => a.WeekStart).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal((host.DevId, host.Project1, (Guid?)host.Build, (Guid?)host.Se2, AllocationSource.Manual),
            (r.EmployeeId, r.ProjectId, r.WbsElementId, r.LaborCategoryId, r.Source)));

        // The existing staffing views read the weekly rows.
        var plan = await host.Get<IProjectStaffingService>().GetPlanAsync(host.Project1);
        Assert.Equal(80m, plan.Slots.Single(s => s.WbsElementId == host.Build && s.LaborCategoryId == host.Se2).StaffedHours);
    }

    [Fact]
    public async Task The_whole_project_starts_today_when_it_started_earlier_and_needs_dates_without_a_finish()
    {
        await using var host = await CreateAsync();
        var service = host.As(Pm).Get<IStaffingAssignmentService>();

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 100m)));
        Assert.Contains("has no finish date", ex.Message);

        await SetFinishAsync(host, host.Project1, new DateOnly(2026, 10, 27));
        var saved = await service.SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 100m));
        Assert.Equal((Today, new DateOnly(2026, 10, 27)), (saved.From, saved.To));
        Assert.Equal([30m, 50m, 20m], saved.Weeks.Select(w => w.Hours));
        Assert.Equal("Whole project", saved.WbsLabel);
    }

    [Fact]
    public async Task Adjusting_a_week_changes_the_total_and_saving_without_changes_keeps_the_adjustment()
    {
        await using var host = await CreateAsync();
        var service = host.As(Pm).Get<IStaffingAssignmentService>();
        var saved = await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));
        Assert.Equal([40m, 40m], saved.Weeks.Select(w => w.Hours));

        var adjusted = await service.SetWeekAsync(saved.Id, new DateOnly(2026, 10, 28), 24m); // any day of the week
        Assert.Equal(64m, adjusted.TotalHours);
        Assert.Equal([40m, 24m], adjusted.Weeks.Select(w => w.Hours));

        var zeroed = await service.SetWeekAsync(saved.Id, new DateOnly(2026, 10, 19), 0m);
        Assert.Equal((24m, 1), (zeroed.TotalHours, zeroed.Weeks.Count));

        // Changing only the notes keeps the weeks; changing the total spreads it evenly again.
        var notes = await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 24m, host.Build, saved.From, saved.To, Notes: "Half time", Id: saved.Id));
        Assert.Equal([24m], notes.Weeks.Select(w => w.Hours));
        var respread = await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 60m, host.Build, saved.From, saved.To, Id: saved.Id));
        Assert.Equal([30m, 30m], respread.Weeks.Select(w => w.Hours));

        await Assert.ThrowsAsync<ArgumentException>(() => service.SetWeekAsync(saved.Id, new DateOnly(2026, 11, 2), 8m)); // outside the dates

        await service.DeleteAsync(saved.Id);
        await using var db = await host.Db.CreateAsync();
        Assert.False(await db.Set<ResourceAllocation>().AnyAsync(a => a.EmployeeId == host.DevId && a.ProjectId == host.Project1));
    }

    [Fact]
    public async Task Assigning_takes_over_week_by_week_staffing_of_the_same_person_and_task()
    {
        await using var host = await CreateAsync();
        await host.As(Pm).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project1, new DateOnly(2026, 10, 19), 8m, WbsElementId: host.Build, Weeks: 2));
        var service = host.Get<IStaffingAssignmentService>();
        var before = await service.ListAsync(host.Project1);
        var loose = Assert.Single(before.WeekByWeek);
        Assert.Equal(("Dana Lee", 16m, new DateOnly(2026, 10, 19), new DateOnly(2026, 11, 1)), (loose.EmployeeName, loose.Hours, loose.From, loose.To));

        await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));

        var after = await service.ListAsync(host.Project1);
        Assert.Empty(after.WeekByWeek);
        Assert.Equal(80m, (await host.Get<IProjectStaffingService>().GetPlanAsync(host.Project1)).StaffedHours);
    }

    [Fact]
    public async Task Preview_warns_when_the_person_is_booked_past_their_hours_on_other_projects()
    {
        await using var host = await CreateAsync();
        await host.As(ResourceManager).Get<IResourceAllocationService>().CreateAsync(new SaveAllocationRequest(host.DevId, host.Project2, new DateOnly(2026, 10, 19), 32m, WbsElementId: host.Wbs2));
        var service = host.As(Pm).Get<IStaffingAssignmentService>();

        var preview = await service.PreviewAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));

        var week = Assert.Single(preview.Overbooked);
        Assert.Equal((new DateOnly(2026, 10, 19), 40m, 32m, 40m), (week.WeekStart, week.ThisAssignment, week.OtherHours, week.Capacity));
        Assert.Contains(preview.Warnings, w => w.Contains("Dana Lee would be booked past their hours: 72 of 40 h the week of Oct 19 (32 h on other work)", StringComparison.Ordinal));
        Assert.Equal([40m, 40m], preview.Weeks.Select(w => w.Hours));

        // Nothing is saved by a preview; saving with the warning is allowed.
        Assert.Empty((await service.ListAsync(host.Project1)).Assignments);
        await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));
    }

    [Fact]
    public async Task Hours_left_counts_hours_on_other_timesheets_between_the_dates()
    {
        await using var host = await CreateAsync();
        var service = host.As(Pm).Get<IStaffingAssignmentService>();
        var build = await service.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));
        await SetFinishAsync(host, host.Project1, new DateOnly(2026, 12, 31));
        await service.SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 400m));
        await host.AddApprovedTimeAsync(host.DevId, (host.Build, host.Se2, new DateOnly(2026, 10, 20), 8m), (host.Design, host.Se2, new DateOnly(2026, 10, 21), 4m));
        var timesheetId = await AddDraftAsync(host, host.DevId, new DateOnly(2026, 10, 26), (host.Build, 6m));
        await AddDraftAsync(host, host.DevId, new DateOnly(2026, 10, 12), (host.Build, 5m)); // before Build's dates: not this assignment's

        var mine = await host.As(Dev).Get<IStaffingAssignmentService>().ListMyHoursLeftAsync(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1));
        var left = Assert.Single(mine);
        Assert.Equal((build.Id, "1001", "1001.01.02 Build", 80m, 14m), (left.AssignmentId, left.ProjectCode, left.Label, left.TotalHours, left.ChargedElsewhereHours));
        Assert.Equal([host.Build], left.WbsElementIds);

        // The timesheet being edited adds its own hours live, so it's left out.
        var excluding = await host.Get<IStaffingAssignmentService>().ListMyHoursLeftAsync(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1), timesheetId);
        Assert.Equal(8m, excluding[0].ChargedElsewhereHours);

        // The whole project covers every code on it; the project's list shows what's charged.
        var analyst = Assert.Single(await host.As(Analyst).Get<IStaffingAssignmentService>().ListMyHoursLeftAsync(Today, Today));
        Assert.Equal(("the whole project", 2), (analyst.Label, analyst.WbsElementIds.Count));
        var listed = (await host.As(Pm).Get<IStaffingAssignmentService>().ListAsync(host.Project1)).Assignments.Single(a => a.EmployeeId == host.DevId);
        Assert.Equal((14m, 66m), (listed.ChargedHours, listed.LeftHours));
    }

    [Fact]
    public async Task Being_assigned_lets_the_person_charge_the_task_between_the_dates()
    {
        await using var host = await CreateAsync();
        var projects = host.As(Admin).Get<IProjectService>();
        var days = new[] { new DateOnly(2026, 10, 21), new DateOnly(2026, 11, 4) };
        Assert.DoesNotContain((await projects.ListChargeCodesForDatesAsync(host.Pm2Id, days))[days[0]], c => c.WbsElementId == host.Build);

        // Quinn has no charge assignment on 1001; assigning them to Build (Oct 19–30) authorizes Build those days only.
        await host.As(Pm).Get<IStaffingAssignmentService>().SaveAsync(new SaveAssignmentRequest(host.Pm2Id, host.Project1, 20m, host.Build, LaborCategoryId: host.Ba2));

        var codes = await host.As(Admin).Get<IProjectService>().ListChargeCodesForDatesAsync(host.Pm2Id, days);
        var build = Assert.Single(codes[days[0]], c => c.WbsElementId == host.Build);
        Assert.Equal(host.Ba2, build.LaborCategoryId); // the assignment's labor category
        Assert.DoesNotContain(codes[days[0]], c => c.WbsElementId == host.Design);
        Assert.DoesNotContain(codes[days[1]], c => c.WbsElementId == host.Build);

        // A whole-project assignment covers every task, in weeks whose spread hours are zero too.
        await SetFinishAsync(host, host.Project1, new DateOnly(2026, 12, 31));
        await host.As(Pm).Get<IStaffingAssignmentService>().SaveAsync(new SaveAssignmentRequest(host.Pm2Id, host.Project1, 0.5m, From: new DateOnly(2026, 11, 2), To: new DateOnly(2026, 11, 20)));
        await using (var db = await host.Db.CreateAsync())
        {
            Assert.False(await db.Set<ResourceAllocation>().AnyAsync(a => a.EmployeeId == host.Pm2Id && a.WeekStart == new DateOnly(2026, 11, 9))); // 0.25, 0, 0.25
        }

        var later = (await host.As(Admin).Get<IProjectService>().ListChargeCodesForDatesAsync(host.Pm2Id, [new DateOnly(2026, 11, 11)])).Single().Value;
        Assert.Contains(later, c => c.WbsElementId == host.Design);
        Assert.Contains(later, c => c.WbsElementId == host.Build);
    }

    [Theory]
    [InlineData(Pm, true, false)]
    [InlineData(Pm2, false, true)]
    [InlineData(ResourceManager, true, true)]
    [InlineData(Admin, true, true)]
    [InlineData(Finance, false, false)]
    [InlineData(Analyst, false, false)]
    public async Task Project_managers_assign_on_their_projects_and_resource_managers_on_all(string email, bool project1, bool project2)
    {
        await using var host = await CreateAsync();
        await SetFinishAsync(host, host.Project2, new DateOnly(2026, 12, 31));
        var service = host.As(email).Get<IStaffingAssignmentService>();

        async Task<bool> TryAsync(Guid projectId, Guid? wbs)
        {
            try
            {
                await service.SaveAsync(new SaveAssignmentRequest(host.DevId, projectId, 40m, wbs, Today, new DateOnly(2026, 10, 30)));
                return true;
            }
            catch (Exception e) when (e is WorkbenchForbiddenException or WorkbenchNotFoundException)
            {
                return false;
            }
        }

        Assert.Equal((project1, project2), (await TryAsync(host.Project1, host.Build), await TryAsync(host.Project2, host.Wbs2)));
    }

    [Fact]
    public async Task Finance_reads_assignments_but_only_managers_change_them()
    {
        await using var host = await CreateAsync();
        var saved = await host.As(Pm).Get<IStaffingAssignmentService>().SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));

        var finance = host.As(Finance).Get<IStaffingAssignmentService>();
        Assert.Single((await finance.ListAsync(host.Project1)).Assignments);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.SetWeekAsync(saved.Id, new DateOnly(2026, 10, 19), 8m));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.DeleteAsync(saved.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => finance.PreviewPasteAsync(host.Project1, "Dana Lee\tBuild\t8"));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.As(Analyst).Get<IStaffingAssignmentService>().ListAsync(host.Project1));

        // Another project's manager can't touch it, even by moving it to their own project.
        var pm2 = host.As(Pm2).Get<IStaffingAssignmentService>();
        await Assert.ThrowsAnyAsync<Exception>(() => pm2.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project2, 8m, host.Wbs2, Id: saved.Id)));
        await host.As(ResourceManager).Get<IStaffingAssignmentService>().SetWeekAsync(saved.Id, new DateOnly(2026, 10, 19), 8m);
    }

    [Fact]
    public async Task Pasted_rows_are_previewed_with_errors_per_row_then_saved_together()
    {
        await using var host = await CreateAsync();
        var service = host.As(Pm).Get<IStaffingAssignmentService>();
        const string pasted = "Person\tTask\tHours\tFrom\tTo\r\n"
            + "Dana Lee\t1001.01.02\t80\t\t\r\n"
            + "analyst@example.com\tDesign\t40 h\t10/19/2026\t10/23/2026\r\n"
            + "\r\n"
            + "Nobody Here\tBuild\t10\t\t\r\n"
            + "Lee, Dana\tNo such task\tlots\t\t\r\n"
            + "E1\t01.01\t20\t2026-11-02\t2026-10-30\r\n";

        var preview = await service.PreviewPasteAsync(host.Project1, pasted);

        Assert.Equal(5, preview.Rows.Count);
        Assert.Equal(2, preview.ValidCount);
        var dana = preview.Rows[0];
        Assert.Equal((2, host.DevId, (Guid?)host.Build, 80m), (dana.Line, dana.Request!.EmployeeId, dana.Request.WbsElementId, dana.Request.TotalHours));
        var alex = preview.Rows[1];
        Assert.Equal((host.AnalystId, (Guid?)host.Design, (DateOnly?)new DateOnly(2026, 10, 19), 40m), (alex.Request!.EmployeeId, alex.Request.WbsElementId, alex.Request.From, alex.Request.TotalHours));
        Assert.Contains("Nobody called \"Nobody Here\"", Assert.Single(preview.Rows[2].Errors));
        Assert.Equal(2, preview.Rows[3].Errors.Count); // the task and the hours
        Assert.Contains(preview.Rows[3].Errors, e => e.Contains("no task \"No such task\"", StringComparison.Ordinal));
        Assert.Contains("before the start date", Assert.Single(preview.Rows[4].Errors)); // E1 is Pat, 01.01 is 1001.01; but the dates are backwards

        var saved = await service.SaveManyAsync(host.Project1, preview.Rows.Where(r => r.IsValid).Select(r => r.Request!).ToList());
        Assert.Equal(120m, saved.Sum(a => a.TotalHours));
        Assert.Equal(2, (await service.ListAsync(host.Project1)).Assignments.Count);

        // All or nothing: one bad row saves none.
        var bad = new SaveAssignmentRequest(host.DevId, host.Project1, 0m, host.Build);
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.SaveManyAsync(host.Project1, [dana.Request, bad]));
        Assert.StartsWith("Row 2:", ex.Message);
        Assert.Equal(2, (await service.ListAsync(host.Project1)).Assignments.Count);
    }

    [Fact]
    public async Task Copying_a_team_matches_tasks_by_code_or_name_and_uses_this_projects_dates()
    {
        await using var host = await CreateAsync();
        await SetFinishAsync(host, host.Project2, new DateOnly(2026, 12, 31));
        Guid build2;
        await using (var db = await host.Db.CreateAsync())
        {
            var element = new WbsElement { ProjectId = host.Project2, Code = "2002.02", Name = "Build" };
            db.Set<WbsElement>().Add(element);
            await db.SaveChangesAsync();
            build2 = element.Id;
        }

        var pm = host.As(Pm).Get<IStaffingAssignmentService>();
        await pm.SaveAsync(new SaveAssignmentRequest(host.DevId, host.Project1, 80m, host.Build));
        await pm.SaveAsync(new SaveAssignmentRequest(host.AnalystId, host.Project1, 60m, host.Design, LaborCategoryId: host.Se2));

        // pm2@ manages 2002 but can't see 1001's staffing.
        await Assert.ThrowsAnyAsync<Exception>(() => host.As(Pm2).Get<IStaffingAssignmentService>().PreviewCopyTeamAsync(host.Project2, host.Project1));

        var rm = host.As(ResourceManager).Get<IStaffingAssignmentService>();
        var preview = await rm.PreviewCopyTeamAsync(host.Project2, host.Project1);

        Assert.Equal(2, preview.ValidCount);
        var alex = preview.Rows.Single(r => r.Request!.EmployeeId == host.AnalystId);
        Assert.Equal(("Whole project", (Guid?)null, (Guid?)host.Se2), (alex.Task, alex.Request!.WbsElementId, alex.Request.LaborCategoryId));
        Assert.Contains("has no task like 1001.01.01 Design", Assert.Single(alex.Warnings, w => w.Contains("Design", StringComparison.Ordinal)));
        var dana = preview.Rows.Single(r => r.Request!.EmployeeId == host.DevId);
        Assert.Equal(("2002.02 Build", (Guid?)build2, 80m), (dana.Task, dana.Request!.WbsElementId, dana.Request.TotalHours));

        var saved = await rm.SaveManyAsync(host.Project2, preview.Rows.Select(r => r.Request!).ToList());
        Assert.All(saved, a => Assert.Equal((Today, new DateOnly(2026, 12, 31)), (a.From, a.To)));
    }

    private static async Task AddHolidayAsync(ResourcingHost host, DateOnly date)
    {
        await using var db = await host.Db.CreateAsync();
        db.Set<Holiday>().Add(new Holiday { Date = date, Name = "Holiday" });
        await db.SaveChangesAsync();
    }

    private static async Task SetFinishAsync(ResourcingHost host, Guid projectId, DateOnly finish)
    {
        await using var db = await host.Db.CreateAsync();
        (await db.Set<Project>().SingleAsync(p => p.Id == projectId)).Finish = finish;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddDraftAsync(ResourcingHost host, Guid employeeId, DateOnly weekStart, params (Guid Wbs, decimal Hours)[] entries)
    {
        await using var db = await host.Db.CreateAsync();
        var now = DateTime.UtcNow;
        var sheet = new Timesheet { EmployeeId = employeeId, PeriodStart = weekStart, PeriodEnd = weekStart.AddDays(6), Status = TimesheetStatus.Draft, CreatedAtUtc = now, UpdatedAtUtc = now };
        db.Set<Timesheet>().Add(sheet);
        foreach (var (wbs, hours) in entries)
        {
            db.Set<TimeEntry>().Add(new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs, LaborCategoryId = host.Se2, WorkDate = weekStart.AddDays(1), Hours = hours, EnteredAtUtc = now, UpdatedAtUtc = now });
        }

        await db.SaveChangesAsync();
        return sheet.Id;
    }
}

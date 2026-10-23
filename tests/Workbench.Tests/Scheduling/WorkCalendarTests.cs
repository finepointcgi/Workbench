using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Scheduling;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Scheduling;

/// <summary>
/// Working calendars: company holidays and a plan's own calendar move finish dates, durations and the critical path, and the
/// working-day index the critical path uses agrees with the calendar day by day.
/// </summary>
public sealed class WorkCalendarTests
{
    private static readonly DateOnly Mon = PlanKit.Mon;
    private static readonly DateOnly ColumbusDay = new(2026, 10, 12); // a Monday
    private static readonly DateOnly Thanksgiving = new(2026, 11, 26); // a Thursday

    private const int MondayToThursday = (1 << 1) | (1 << 2) | (1 << 3) | (1 << 4);

    [Fact]
    public void The_index_counts_working_days_and_round_trips_on_any_calendar()
    {
        WorkCalendar[] calendars =
        [
            WorkCalendar.Weekdays,
            new([ColumbusDay, Thanksgiving, Thanksgiving.AddDays(1)]),
            new([ColumbusDay], MondayToThursday, [new DateOnly(2026, 10, 10)]), // a working Saturday
            new([], 1 << (int)DayOfWeek.Wednesday),
        ];

        foreach (var calendar in calendars)
        {
            for (var d = Mon.AddDays(-60); d < Mon.AddDays(400); d = d.AddDays(1))
            {
                Assert.Equal(calendar.IsWorkingDay(d) ? 1 : 0, calendar.Index(d.AddDays(1)) - calendar.Index(d));
                if (calendar.IsWorkingDay(d))
                {
                    Assert.Equal(d, calendar.FromIndex(calendar.Index(d)));
                }
                else
                {
                    Assert.Equal(calendar.OnOrAfter(d), calendar.FromIndex(calendar.Index(d)));
                }
            }
        }

        // The default calendar counts like the Mon–Fri index the EVM code uses.
        Assert.Equal(WorkingDays.Index(Mon.AddDays(30)) - WorkingDays.Index(Mon), WorkCalendar.Weekdays.Between(Mon, Mon.AddDays(30)));
    }

    [Fact]
    public void Holidays_lengthen_finish_dates_and_shorten_durations()
    {
        var calendar = new WorkCalendar([ColumbusDay]);

        Assert.Equal(new DateOnly(2026, 10, 19), calendar.Finish(Mon, 10)); // Mon 19 Oct, not Fri 16 Oct
        Assert.Equal(9, calendar.Duration(Mon, new DateOnly(2026, 10, 16)));
        Assert.Equal(new DateOnly(2026, 10, 13), calendar.OnOrAfter(ColumbusDay));
        Assert.Equal(new DateOnly(2026, 10, 9), calendar.OnOrBefore(ColumbusDay));
        Assert.Equal(Mon, calendar.StartFor(new DateOnly(2026, 10, 19), 10));
        Assert.Equal(1, calendar.Duration(ColumbusDay, ColumbusDay)); // a task on a day off still lasts a day
        Assert.Equal(0, calendar.Duration(ColumbusDay, ColumbusDay, isMilestone: true));
    }

    [Fact]
    public void A_custom_work_week_and_extra_working_days_count()
    {
        var saturday = new DateOnly(2026, 10, 10);
        var fourDays = new WorkCalendar([], MondayToThursday);
        Assert.Equal(Mon.AddDays(7), fourDays.Finish(Mon, 5)); // Mon–Thu, then next Monday
        Assert.False(fourDays.IsWorkingDay(Mon.AddDays(4)));

        var withSaturday = new WorkCalendar([saturday], workingDays: [saturday]); // working wins over a holiday on the same day
        Assert.True(withSaturday.IsWorkingDay(saturday));
        Assert.Equal(saturday, withSaturday.Finish(Mon, 6));

        Assert.Throws<ArgumentException>(() => new WorkCalendar([], 0));
    }

    [Fact]
    public async Task A_company_holiday_moves_a_finish_date_and_its_successors()
    {
        var kit = new PlanKit();
        await kit.HolidayAsync(ColumbusDay, "Columbus Day");
        var sheet = await kit.SheetAsync();
        var design = await kit.TaskAsync(sheet, "Design", Mon, 1);
        var build = await kit.TaskAsync(sheet, "Build", Mon, 1);
        await kit.LinkAsync(sheet, design.Id, build.Id);

        await kit.Plan.SetDurationAsync(sheet, design.Id, 5); // Mon–Fri 9 Oct
        await kit.Plan.SetDurationAsync(sheet, build.Id, 3);

        Assert.Equal((Mon, Mon.AddDays(4)), await kit.DatesAsync(sheet, design.Id));
        Assert.Equal((new DateOnly(2026, 10, 13), new DateOnly(2026, 10, 15)), await kit.DatesAsync(sheet, build.Id)); // Tue, after the holiday

        await kit.Plan.SetDurationAsync(sheet, design.Id, 10);
        Assert.Equal(new DateOnly(2026, 10, 19), (await kit.GetAsync(sheet, design.Id)).Finish);
        var plan = await kit.Plan.GetAsync(sheet);
        Assert.Equal(10, plan.For(design.Id)!.DurationDays);
        Assert.Contains(ColumbusDay, plan.Holidays);
        Assert.False(plan.IsWorkingDay(ColumbusDay));
    }

    [Fact]
    public async Task The_critical_path_counts_holidays_as_days_off()
    {
        var kit = new PlanKit();
        await kit.HolidayAsync(ColumbusDay, "Columbus Day");
        var sheet = await kit.SheetAsync();
        var a = await kit.TaskAsync(sheet, "Long", Mon, 1);
        var b = await kit.TaskAsync(sheet, "Short", Mon, 5); // Mon–Fri
        await kit.Plan.SetDurationAsync(sheet, a.Id, 10); // to Mon 19 Oct, over the holiday

        var analysis = await kit.Links.AnalyzeAsync(sheet);

        Assert.Equal(new DateOnly(2026, 10, 19), analysis.ProjectFinish);
        Assert.Equal([a.Id], analysis.CriticalPath);
        Assert.Equal(5, analysis.Items.Single(i => i.ItemId == b.Id).SlackDays); // 5 working days, not the 6 weekdays to 19 Oct
    }

    [Fact]
    public async Task A_project_calendar_overrides_the_company_one_and_keeps_durations()
    {
        var kit = new PlanKit();
        await kit.HolidayAsync(ColumbusDay, "Columbus Day");
        var sheet = await kit.SheetAsync();
        var other = await kit.SheetAsync("Another plan");
        var task = await kit.TaskAsync(sheet, "Survey", Mon, 5);
        var elsewhere = await kit.TaskAsync(other, "Survey", Mon, 5);

        var calendar = await kit.Plan.SetCalendarAsync(sheet, new SetPlanCalendarRequest(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], UseCompanyHolidays: true,
            [new PlanCalendarDayDto(Mon.AddDays(3), "Site shutdown")]));

        // Five working days: Mon, Tue, Wed (Thu shut, Fri off), (Mon holiday), Tue, Wed.
        Assert.Equal((Mon, new DateOnly(2026, 10, 14)), await kit.DatesAsync(sheet, task.Id));
        Assert.Equal(5, (await kit.Plan.GetAsync(sheet)).For(task.Id)!.DurationDays);
        Assert.Equal((Mon, Mon.AddDays(4)), await kit.DatesAsync(other, elsewhere.Id)); // other plans keep the company calendar
        Assert.True(calendar.IsCustom);
        Assert.Equal("Site shutdown", Assert.Single(calendar.Exceptions).Name);
        Assert.Contains(calendar.CompanyHolidays, h => h.Date == ColumbusDay);
        var plan = await kit.Plan.GetAsync(sheet);
        Assert.Equal(4, plan.WorkWeek!.Count);
        Assert.False(plan.IsWorkingDay(Mon.AddDays(3)));

        // Without company holidays and with a working Saturday.
        await kit.Plan.SetCalendarAsync(sheet, new SetPlanCalendarRequest(
            [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday], UseCompanyHolidays: false,
            [new PlanCalendarDayDto(Mon.AddDays(5), "Catch-up Saturday", IsWorking: true)]));
        Assert.Equal((Mon, Mon.AddDays(5)), await kit.DatesAsync(sheet, task.Id)); // Mon, Tue, Wed, Thu, Sat
        Assert.True((await kit.Plan.GetAsync(sheet)).IsWorkingDay(Mon.AddDays(5)));

        // Back to the company calendar, still five days.
        var reset = await kit.Plan.SetCalendarAsync(sheet, null);
        Assert.False(reset.IsCustom);
        Assert.Equal((Mon, Mon.AddDays(4)), await kit.DatesAsync(sheet, task.Id));
    }

    [Fact]
    public async Task A_bad_calendar_is_refused()
    {
        var kit = new PlanKit();
        var sheet = await kit.SheetAsync();

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetCalendarAsync(sheet, new SetPlanCalendarRequest([], true, [])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetCalendarAsync(sheet, new SetPlanCalendarRequest(
            PlanCalendarDto.MondayToFriday, true, [new PlanCalendarDayDto(Mon, "A"), new PlanCalendarDayDto(Mon, "B")])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Plan.SetCalendarAsync(sheet, new SetPlanCalendarRequest(
            PlanCalendarDto.MondayToFriday, true, [new PlanCalendarDayDto(Mon, " ")])));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => kit.Plan.GetCalendarAsync(Guid.NewGuid()));
        Assert.False((await kit.Plan.GetCalendarAsync(sheet)).IsCustom);
    }

    [Fact]
    public async Task Viewers_see_the_calendar_and_editors_change_it()
    {
        await using var seeded = await SeededHost.CreateAsync();
        var request = new SetPlanCalendarRequest(PlanCalendarDto.MondayToFriday, true, [new PlanCalendarDayDto(Mon, "Offsite")]);

        Assert.False((await seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().GetCalendarAsync(seeded.SheetId)).IsCustom);
        await Assert.ThrowsAnyAsync<UnauthorizedAccessException>(() =>
            seeded.As(SeededHost.Viewer).Host.Get<IPlanScheduleService>().SetCalendarAsync(seeded.SheetId, request));
        var saved = await seeded.As(SeededHost.Editor).Host.Get<IPlanScheduleService>().SetCalendarAsync(seeded.SheetId, request);
        Assert.True(saved.IsCustom);
    }
}

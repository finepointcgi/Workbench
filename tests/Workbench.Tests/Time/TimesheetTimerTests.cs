using Workbench.Application.Time;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Time;

namespace Workbench.Tests.Time;

/// <summary>The start/stop timer: rounding, adding to hours already there, one per person, midnight, locked timesheets.</summary>
public sealed class TimesheetTimerTests
{
    // TimeTestHost's "now" is Wednesday 2026-10-21 15:00 UTC (the time zone is UTC).
    private static readonly DateOnly Wednesday = TimeTestHost.Monday.AddDays(2);
    private static readonly DateTime Now = new(2026, 10, 21, 15, 0, 0, DateTimeKind.Utc);

    private static TimesheetTimerService Timer(TimeTestHost host) =>
        new(host.Factory, host.User, host.Timesheets, host.Projects, new TimekeepingClock(host.Clock, Microsoft.Extensions.Options.Options.Create(host.Options)));

    private static async Task<decimal> HoursAsync(TimeTestHost host, DateOnly date, Guid wbs) =>
        (await host.Timesheets.GetOrCreateMyTimesheetAsync(date)).Entries.Where(e => e.WorkDate == date && e.WbsElementId == wbs).Sum(e => e.Hours);

    [Theory]
    [InlineData(0.12, 0.25, 0)]
    [InlineData(0.125, 0.25, 0.25)] // half an increment rounds up
    [InlineData(1.6667, 0.25, 1.75)]
    [InlineData(1.37, 0.25, 1.25)]
    [InlineData(1.375, 0.25, 1.5)]
    [InlineData(1.6667, 0.1, 1.7)]
    [InlineData(2, 0.5, 2)]
    public void Rounds_to_the_nearest_increment(decimal hours, decimal increment, decimal expected) =>
        Assert.Equal(expected, TimesheetTimerService.RoundTo(hours, increment));

    [Fact]
    public async Task Stopping_adds_the_rounded_time_to_todays_cell_with_the_note()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);

        var running = await timer.StartAsync(new StartTimerRequest(host.Direct.Id, "Requirements workshop"));
        Assert.Equal(Wednesday, running.StartDate);
        Assert.Equal(host.Direct.Id, running.ChargeCode.WbsElementId);

        host.Clock.UtcNow = Now.AddMinutes(100); // 1 h 40 min
        var state = await timer.GetAsync();
        Assert.Equal(1.75m, state.Running!.RoundedHours);
        Assert.False(state.Running.CrossesMidnight);

        var result = await timer.StopAsync(new StopTimerRequest());

        Assert.Equal(TimerStopOutcome.Added, result.Outcome);
        Assert.Equal([new TimerDayHours(Wednesday, 1.75m)], result.Days);
        Assert.Contains("1.75 h on Wed Oct 21", result.Message, StringComparison.Ordinal);
        var entry = Assert.Single((await host.Timesheets.GetOrCreateMyTimesheetAsync(Wednesday)).Entries);
        Assert.Equal((1.75m, "Requirements workshop"), (entry.Hours, entry.Comment));
        Assert.Null((await timer.GetAsync()).Running);
    }

    [Fact]
    public async Task Adds_to_hours_already_there_and_keeps_the_rest_of_the_day()
    {
        var host = await TimeTestHost.CreateAsync();
        var week = await host.DanaWeekAsync(); // 8 h Monday to Wednesday on Direct
        await host.Timesheets.SaveEntriesAsync(week.Timesheet.Id, new SaveTimeEntriesRequest(
            [new TimeEntryInput(host.Direct.Id, Wednesday, 8m, "Morning design review"), new TimeEntryInput(host.Overhead.Id, Wednesday, 0.5m)], Wednesday));
        var timer = Timer(host);

        await timer.StartAsync(new StartTimerRequest(host.Direct.Id, "Afternoon build"));
        host.Clock.UtcNow = Now.AddMinutes(90);
        await timer.StopAsync(new StopTimerRequest());

        var detail = await host.Timesheets.GetOrCreateMyTimesheetAsync(Wednesday);
        var direct = detail.Entries.Single(e => e.WorkDate == Wednesday && e.WbsElementId == host.Direct.Id);
        Assert.Equal(9.5m, direct.Hours);
        Assert.Equal("Morning design review\nAfternoon build", direct.Comment);
        Assert.Equal(0.5m, await HoursAsync(host, Wednesday, host.Overhead.Id));
        Assert.Equal(8m, await HoursAsync(host, TimeTestHost.Monday, host.Direct.Id));
        Assert.Equal(8m, await HoursAsync(host, TimeTestHost.Monday.AddDays(1), host.Direct.Id));
    }

    [Fact]
    public async Task One_running_timer_per_person()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));

        var error = await Assert.ThrowsAsync<ArgumentException>(() => timer.StartAsync(new StartTimerRequest(host.Overhead.Id)));
        Assert.Contains("already running on Requirements (1001.01)", error.Message, StringComparison.Ordinal);

        // Someone else has their own.
        host.SignInAs(host.Sam);
        var sams = await timer.StartAsync(new StartTimerRequest(host.Overhead.Id));
        Assert.Equal(host.Overhead.Id, sams.ChargeCode.WbsElementId);

        // The timer is on the server: a new service (another device) sees it.
        host.SignInAs(host.Dana);
        Assert.Equal(host.Direct.Id, (await Timer(host).GetAsync()).Running!.ChargeCode.WbsElementId);
    }

    [Fact]
    public async Task A_timer_over_midnight_asks_how_to_split_it_then_adds_each_day()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        host.Clock.UtcNow = new DateTime(2026, 10, 20, 22, 30, 0, DateTimeKind.Utc); // Tuesday 10:30 pm
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id, "Release night"));

        host.Clock.UtcNow = new DateTime(2026, 10, 21, 1, 15, 0, DateTimeKind.Utc); // Wednesday 1:15 am
        Assert.True((await timer.GetAsync()).Running!.CrossesMidnight);
        var ask = await timer.StopAsync(new StopTimerRequest());

        Assert.Equal(TimerStopOutcome.NeedsSplit, ask.Outcome);
        Assert.Equal([new TimerDayHours(Wednesday.AddDays(-1), 1.5m), new TimerDayHours(Wednesday, 1.25m)], ask.Days);
        Assert.NotNull((await timer.GetAsync()).Running); // still running, nothing added
        Assert.Equal(0m, await HoursAsync(host, Wednesday, host.Direct.Id));

        // More than the timer ran, or a day it didn't run, is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => timer.StopAsync(new StopTimerRequest(Split: [new TimerDayHours(Wednesday, 3.25m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => timer.StopAsync(new StopTimerRequest(Split: [new TimerDayHours(TimeTestHost.Monday, 1m)])));
        await Assert.ThrowsAsync<ArgumentException>(() => timer.StopAsync(new StopTimerRequest(Split: [new TimerDayHours(Wednesday, 1.1m)])));

        var done = await timer.StopAsync(new StopTimerRequest(Split: ask.Days));
        Assert.Equal(TimerStopOutcome.Added, done.Outcome);
        Assert.Equal(1.5m, await HoursAsync(host, Wednesday.AddDays(-1), host.Direct.Id));
        Assert.Equal(1.25m, await HoursAsync(host, Wednesday, host.Direct.Id));
    }

    [Fact]
    public async Task All_of_a_timer_over_midnight_can_go_on_one_day()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        host.Clock.UtcNow = new DateTime(2026, 10, 20, 23, 0, 0, DateTimeKind.Utc);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));
        host.Clock.UtcNow = new DateTime(2026, 10, 21, 0, 30, 0, DateTimeKind.Utc);

        await timer.StopAsync(new StopTimerRequest(Split: [new TimerDayHours(Wednesday.AddDays(-1), 1.5m), new TimerDayHours(Wednesday, 0m)]));

        Assert.Equal(1.5m, await HoursAsync(host, Wednesday.AddDays(-1), host.Direct.Id));
        Assert.Equal(0m, await HoursAsync(host, Wednesday, host.Direct.Id));
    }

    [Fact]
    public async Task A_submitted_timesheet_asks_to_recall_and_keeps_the_timer_running()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.SubmittedDanaWeekAsync(); // signed in as Dana
        var timer = Timer(host);
        Assert.True((await timer.GetAsync()).TodayIsLocked);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));
        host.Clock.UtcNow = Now.AddHours(2);

        var ask = await timer.StopAsync(new StopTimerRequest());

        Assert.Equal(TimerStopOutcome.NeedsRecall, ask.Outcome);
        Assert.Equal((id, TimesheetState.Submitted), (ask.TimesheetId, ask.TimesheetStatus));
        Assert.Contains("Recall it", ask.Message, StringComparison.Ordinal);
        Assert.NotNull((await timer.GetAsync()).Running);
        Assert.Equal(8m, await HoursAsync(host, Wednesday, host.Direct.Id));

        // After recalling, stopping adds the time; the change is recorded with the timer's reason.
        await host.Timesheets.ReopenAsync(id, "Add the afternoon");
        var done = await timer.StopAsync(new StopTimerRequest());
        Assert.Equal(TimerStopOutcome.Added, done.Outcome);
        Assert.Equal(10m, await HoursAsync(host, Wednesday, host.Direct.Id));
        var revision = Assert.Single((await host.Timesheets.GetAsync(id)).Revisions, r => r.WorkDate == Wednesday && r.NewHours == 10m);
        Assert.StartsWith("Timer Oct 21 3:00 PM to Oct 21 5:00 PM", revision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_timesheet_asks_to_reopen_it()
    {
        var host = await TimeTestHost.CreateAsync();
        var id = await host.ApprovedDanaWeekAsync();
        var timer = Timer(host);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));
        host.Clock.UtcNow = Now.AddHours(1);

        var ask = await timer.StopAsync(new StopTimerRequest(Reason: "Late call"));

        Assert.Equal(TimerStopOutcome.NeedsRecall, ask.Outcome);
        Assert.Equal(TimesheetState.Approved, ask.TimesheetStatus);
        Assert.Contains("Reopen it for correction", ask.Message, StringComparison.Ordinal);
        Assert.Equal(id, ask.TimesheetId);
    }

    [Fact]
    public async Task Too_short_adds_nothing_and_discard_throws_the_timer_away()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));
        host.Clock.UtcNow = Now.AddMinutes(5);

        var result = await timer.StopAsync(new StopTimerRequest());

        Assert.Equal(TimerStopOutcome.TooShort, result.Outcome);
        Assert.Null((await timer.GetAsync()).Running);
        Assert.Equal(0m, await HoursAsync(host, Wednesday, host.Direct.Id));

        await timer.StartAsync(new StartTimerRequest(host.Direct.Id));
        host.Clock.UtcNow = Now.AddHours(3);
        await timer.DiscardAsync();
        Assert.Null((await timer.GetAsync()).Running);
        Assert.Equal(0m, await HoursAsync(host, Wednesday, host.Direct.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => timer.StopAsync(new StopTimerRequest()));
    }

    [Fact]
    public async Task Only_open_chargeable_codes_and_open_periods_can_be_timed()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => timer.StartAsync(new StartTimerRequest(host.Unassigned.Id)));
        Assert.Contains("can't charge that code today", error.Message, StringComparison.Ordinal);

        await host.Calendar.SetPeriodStatusAsync(host.Periods[0].Id, FiscalPeriodState.Closed, "October closed");
        error = await Assert.ThrowsAsync<ArgumentException>(() => timer.StartAsync(new StartTimerRequest(host.Direct.Id)));
        Assert.Contains("open fiscal period", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_code_that_closes_while_running_can_be_swapped_when_stopping()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        await timer.StartAsync(new StartTimerRequest(host.Direct.Id, "Call"));
        host.Clock.UtcNow = Now.AddHours(1);

        var result = await timer.StopAsync(new StopTimerRequest(WbsElementId: host.Overhead.Id, Note: "Staff meeting"));

        Assert.Equal(TimerStopOutcome.Added, result.Outcome);
        var entry = Assert.Single((await host.Timesheets.GetOrCreateMyTimesheetAsync(Wednesday)).Entries);
        Assert.Equal((host.Overhead.Id, 1m, "Staff meeting"), (entry.WbsElementId, entry.Hours, entry.Comment));
        await Assert.ThrowsAsync<ArgumentException>(() => timer.StartAsync(new StartTimerRequest(host.Direct.Id, new string('x', 501))));
    }

    [Fact]
    public async Task Defaults_to_the_last_code_charged_and_rounds_to_the_configured_increment()
    {
        var host = await TimeTestHost.CreateAsync(new TimekeepingOptions { TimerIncrementHours = 0.1m });
        host.SignInAs(host.Dana);
        var timer = Timer(host);
        var first = await timer.GetAsync();
        Assert.Equal(0.1m, first.IncrementHours);
        Assert.Null(first.DefaultWbsElementId); // nothing charged or staffed yet
        Assert.DoesNotContain(first.ChargeCodes, c => c.ChargeCode.Kind == ChargeCodeKind.Leave);

        var sheet = await host.Timesheets.GetOrCreateMyTimesheetAsync(TimeTestHost.Monday);
        await host.Timesheets.SaveEntriesAsync(sheet.Timesheet.Id, new SaveTimeEntriesRequest([new TimeEntryInput(host.Overhead.Id, TimeTestHost.Monday, 1m)]));
        var state = await timer.GetAsync();
        Assert.Equal(host.Overhead.Id, state.DefaultWbsElementId);
        Assert.Equal(host.Overhead.Id, state.ChargeCodes[0].ChargeCode.WbsElementId);
        Assert.True(state.ChargeCodes[0].RecentlyUsed);

        await timer.StartAsync(new StartTimerRequest(host.Overhead.Id));
        host.Clock.UtcNow = Now.AddMinutes(100);
        Assert.Equal(1.7m, (await timer.StopAsync(new StopTimerRequest())).Days.Single().Hours);
    }

    [Fact]
    public void Notes_are_appended_to_the_comment_once()
    {
        Assert.Equal("a", TimesheetTimerService.AppendComment(null, "a"));
        Assert.Equal("a\nb", TimesheetTimerService.AppendComment("a", "b"));
        Assert.Equal("a\nb", TimesheetTimerService.AppendComment("a\nb", "b"));
        Assert.Equal("a", TimesheetTimerService.AppendComment("a", "  "));
        Assert.Equal(1000, TimesheetTimerService.AppendComment(new string('x', 999), "yy")!.Length);
    }
}

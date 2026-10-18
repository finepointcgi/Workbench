using Microsoft.EntityFrameworkCore;
using Workbench.Application.Time;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Contracts.Time;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Tests.Time;

/// <summary>The four timesheet cadences, month ends, leap years, and changing the cadence at a period boundary.</summary>
public sealed class TimesheetPeriodMathTests
{
    private static (DateOnly, DateOnly) P(int y1, int m1, int d1, int y2, int m2, int d2) => (new DateOnly(y1, m1, d1), new DateOnly(y2, m2, d2));

    private static (DateOnly Start, DateOnly End) Natural(DateOnly date, TimesheetCadence cadence, DayOfWeek start = DayOfWeek.Monday, DateOnly? anchor = null) =>
        TimesheetPeriods.Natural(date, new TimesheetPeriodSettings(cadence, start, anchor));

    [Theory]
    [InlineData(2026, 10, 25, DayOfWeek.Monday, 2026, 10, 19)] // Sunday ends a Monday week
    [InlineData(2026, 10, 19, DayOfWeek.Monday, 2026, 10, 19)] // the start day itself
    [InlineData(2026, 10, 21, DayOfWeek.Sunday, 2026, 10, 18)]
    [InlineData(2026, 10, 24, DayOfWeek.Saturday, 2026, 10, 24)]
    [InlineData(2026, 10, 23, DayOfWeek.Saturday, 2026, 10, 17)]
    [InlineData(2026, 12, 31, DayOfWeek.Monday, 2026, 12, 28)] // spans the new year
    [InlineData(2028, 2, 29, DayOfWeek.Thursday, 2028, 2, 24)] // leap day
    public void Weekly_periods_start_on_the_configured_day(int y, int m, int d, DayOfWeek startDay, int sy, int sm, int sd)
    {
        var (start, end) = Natural(new DateOnly(y, m, d), TimesheetCadence.Weekly, startDay);
        Assert.Equal(new DateOnly(sy, sm, sd), start);
        Assert.Equal(start.AddDays(6), end);
        Assert.Equal(startDay, start.DayOfWeek);
    }

    [Fact]
    public void Biweekly_periods_repeat_every_fourteen_days_from_the_anchor_in_both_directions()
    {
        var anchor = new DateOnly(2026, 1, 5);
        Assert.Equal(P(2026, 1, 5, 2026, 1, 18), Natural(new DateOnly(2026, 1, 5), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2026, 1, 5, 2026, 1, 18), Natural(new DateOnly(2026, 1, 18), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2026, 1, 19, 2026, 2, 1), Natural(new DateOnly(2026, 1, 19), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2025, 12, 22, 2026, 1, 4), Natural(new DateOnly(2026, 1, 4), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2025, 12, 22, 2026, 1, 4), Natural(new DateOnly(2025, 12, 22), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2025, 12, 8, 2025, 12, 21), Natural(new DateOnly(2025, 12, 21), TimesheetCadence.Biweekly, anchor: anchor));
        Assert.Equal(P(2026, 10, 12, 2026, 10, 25), Natural(new DateOnly(2026, 10, 21), TimesheetCadence.Biweekly, anchor: anchor)); // Jan 5 + 20 × 14 days

        // Across a leap day the period is still exactly 14 days.
        Assert.Equal(P(2028, 2, 21, 2028, 3, 5), Natural(new DateOnly(2028, 2, 29), TimesheetCadence.Biweekly, anchor: new DateOnly(2028, 2, 21)));
        Assert.Throws<ArgumentException>(() => Natural(new DateOnly(2026, 1, 1), TimesheetCadence.Biweekly));
    }

    [Theory]
    [InlineData(2026, 1, 15, 2026, 1, 1, 2026, 1, 15)]
    [InlineData(2026, 1, 16, 2026, 1, 16, 2026, 1, 31)]
    [InlineData(2026, 4, 30, 2026, 4, 16, 2026, 4, 30)]
    [InlineData(2027, 2, 20, 2027, 2, 16, 2027, 2, 28)]
    [InlineData(2028, 2, 29, 2028, 2, 16, 2028, 2, 29)] // leap year
    [InlineData(2026, 12, 31, 2026, 12, 16, 2026, 12, 31)]
    public void Semi_monthly_periods_split_on_the_fifteenth(int y, int m, int d, int sy, int sm, int sd, int ey, int em, int ed) =>
        Assert.Equal(P(sy, sm, sd, ey, em, ed), Natural(new DateOnly(y, m, d), TimesheetCadence.SemiMonthly));

    [Theory]
    [InlineData(2028, 2, 10, 29)] // leap year
    [InlineData(2027, 2, 10, 28)]
    [InlineData(2026, 4, 1, 30)]
    [InlineData(2026, 12, 31, 31)]
    public void Monthly_periods_are_calendar_months(int y, int m, int d, int lastDay) =>
        Assert.Equal(P(y, m, 1, y, m, lastDay), Natural(new DateOnly(y, m, d), TimesheetCadence.Monthly));

    [Fact]
    public void A_change_starts_on_its_effective_date_with_a_short_first_period_when_needed()
    {
        var weekly = new TimesheetPeriodRuleEntry(new TimesheetPeriodSettings(TimesheetCadence.Weekly), DateOnly.MinValue);
        var monthly = new TimesheetPeriodRuleEntry(new TimesheetPeriodSettings(TimesheetCadence.Monthly), new DateOnly(2026, 10, 26));
        IReadOnlyList<TimesheetPeriodRuleEntry> schedule = [weekly, monthly];

        Assert.Equal(P(2026, 10, 19, 2026, 10, 25), TimesheetPeriods.For(new DateOnly(2026, 10, 21), schedule));
        Assert.Equal(P(2026, 10, 26, 2026, 10, 31), TimesheetPeriods.For(new DateOnly(2026, 10, 28), schedule)); // stub to the month end
        Assert.Equal(P(2026, 11, 1, 2026, 11, 30), TimesheetPeriods.For(new DateOnly(2026, 11, 3), schedule));
        Assert.Equal(new DateOnly(2026, 10, 26), TimesheetPeriods.NextBoundary(new DateOnly(2026, 10, 21), [weekly]));

        // Back to weekly from Sunday Nov 1: the first weekly period is the one day, then Monday weeks.
        var back = new TimesheetPeriodRuleEntry(new TimesheetPeriodSettings(TimesheetCadence.Weekly), new DateOnly(2026, 11, 1));
        IReadOnlyList<TimesheetPeriodRuleEntry> twice = [weekly, monthly, back];
        Assert.Equal(P(2026, 10, 26, 2026, 10, 31), TimesheetPeriods.For(new DateOnly(2026, 10, 31), twice));
        Assert.Equal(P(2026, 11, 1, 2026, 11, 1), TimesheetPeriods.For(new DateOnly(2026, 11, 1), twice));
        Assert.Equal(P(2026, 11, 2, 2026, 11, 8), TimesheetPeriods.For(new DateOnly(2026, 11, 5), twice));

        // A period of the old cadence never runs past the change (semi-monthly to biweekly on the 16th).
        var semi = new TimesheetPeriodRuleEntry(new TimesheetPeriodSettings(TimesheetCadence.SemiMonthly), DateOnly.MinValue);
        var biweekly = new TimesheetPeriodRuleEntry(new TimesheetPeriodSettings(TimesheetCadence.Biweekly, BiweeklyAnchor: new DateOnly(2026, 11, 16)), new DateOnly(2026, 11, 16));
        Assert.Equal(P(2026, 11, 1, 2026, 11, 15), TimesheetPeriods.For(new DateOnly(2026, 11, 15), [semi, biweekly]));
        Assert.Equal(P(2026, 11, 16, 2026, 11, 29), TimesheetPeriods.For(new DateOnly(2026, 11, 20), [semi, biweekly]));
    }
}

/// <summary>The organization setting: Admin-only, effective from the next boundary, audited; started timesheets keep their period.</summary>
public sealed class TimesheetCadenceSettingTests
{
    private const string Reason = "Payroll moves to monthly";

    [Fact]
    public async Task Defaults_to_the_configured_cadence_until_saved()
    {
        var host = await TimeTestHost.CreateAsync();
        var settings = await host.OrganizationSettings.GetAsync();

        Assert.Equal(TimesheetCadenceKind.Weekly, settings.Current.Cadence);
        Assert.Equal(DayOfWeek.Monday, settings.Current.WeekStartDay);
        Assert.Null(settings.Pending);
        Assert.Empty(settings.History);
        Assert.Equal((TimeTestHost.Monday, TimeTestHost.Monday.AddDays(6)), (settings.CurrentPeriodStart, settings.CurrentPeriodEnd));
        Assert.Equal(new DateOnly(2026, 10, 26), settings.NextBoundary);
        Assert.True(settings.CanEdit);
    }

    [Fact]
    public async Task Only_admins_change_it_and_finance_can_read_it()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.OrganizationSettings.GetAsync());

        await host.GrantAsync(host.Dana.Email, OrgRoles.Finance);
        Assert.False((await host.OrganizationSettings.GetAsync()).CanEdit);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() =>
            host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Monthly, null, null, Reason)));
    }

    [Fact]
    public async Task A_change_takes_effect_at_the_next_boundary_and_started_timesheets_keep_their_period()
    {
        var host = await TimeTestHost.CreateAsync();
        host.SignInAs(host.Dana);
        var started = (await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 20))).Timesheet;

        host.User.Email = "admin@example.com";
        var preview = await host.OrganizationSettings.PreviewTimesheetCadenceAsync(new(TimesheetCadenceKind.Monthly, null, null, Reason));
        Assert.Equal(new DateOnly(2026, 10, 26), preview.EffectiveFrom);
        Assert.Equal([new(new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 31)), new(new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30))],
            preview.FirstPeriods);

        var settings = await host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Monthly, null, null, Reason));
        Assert.Equal(TimesheetCadenceKind.Weekly, settings.Current.Cadence);
        Assert.Equal((TimesheetCadenceKind.Monthly, new DateOnly(2026, 10, 26)), (settings.Pending!.Cadence, settings.Pending.EffectiveFrom!.Value));
        Assert.Equal(["admin@example.com", "configuration"], settings.History.Select(h => h.ChangedBy));

        // This week's timesheet is unchanged, before and after the change takes effect.
        host.SignInAs(host.Dana);
        Assert.Equal(started.Id, (await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 24))).Timesheet.Id);
        host.Clock.UtcNow = new DateTime(2026, 10, 27, 15, 0, 0, DateTimeKind.Utc);
        var again = (await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 22))).Timesheet;
        Assert.Equal((started.Id, TimeTestHost.Monday, TimeTestHost.Monday.AddDays(6)), (again.Id, again.PeriodStart, again.PeriodEnd));

        // The first monthly period is the stub to the month end, then whole months.
        var stub = (await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 10, 27))).Timesheet;
        Assert.Equal((new DateOnly(2026, 10, 26), new DateOnly(2026, 10, 31)), (stub.PeriodStart, stub.PeriodEnd));
        await Assert.ThrowsAsync<ArgumentException>(() => host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 11, 2)));
        host.Clock.UtcNow = new DateTime(2026, 11, 2, 15, 0, 0, DateTimeKind.Utc);
        var november = (await host.Timesheets.GetOrCreateMyTimesheetAsync(new DateOnly(2026, 11, 2))).Timesheet;
        Assert.Equal((new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 30)), (november.PeriodStart, november.PeriodEnd));

        host.User.Email = "admin@example.com";
        Assert.Equal(TimesheetCadenceKind.Monthly, (await host.OrganizationSettings.GetAsync()).Current.Cadence);
    }

    [Fact]
    public async Task Saving_again_before_it_takes_effect_replaces_the_pending_change_and_everything_is_audited()
    {
        var host = await TimeTestHost.CreateAsync();
        await host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Monthly, null, null, Reason));
        var semi = await host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.SemiMonthly, null, null, "Semi-monthly instead"));
        Assert.Equal((TimesheetCadenceKind.SemiMonthly, new DateOnly(2026, 10, 26)), (semi.Pending!.Cadence, semi.Pending.EffectiveFrom!.Value));
        Assert.DoesNotContain(semi.History, h => h.Cadence == TimesheetCadenceKind.Monthly);

        // Saving the setting in force cancels the pending change; saving it again is refused.
        var reverted = await host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Weekly, DayOfWeek.Monday, null, "Keep weekly"));
        Assert.Null(reverted.Pending);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Weekly, DayOfWeek.Monday, null, "Again")));

        await using var db = await host.Factory.CreateAsync();
        var audit = await db.Set<AuditEvent>().Where(a => a.EntityType == OrganizationSettingsService.AuditEntity).Select(a => a.Action).ToListAsync();
        Assert.Equal(2, audit.Count(a => a == "TimesheetCadenceChanged"));
        Assert.Equal(2, audit.Count(a => a == "TimesheetCadenceCancelled"));
        Assert.Equal(3, await db.Set<SecurityEvent>().CountAsync(e => e.EventType == SecurityEventTypes.SettingChanged && e.Target == "Timekeeping:Cadence"));
        Assert.Equal(2, await db.Set<TimesheetPeriodRule>().CountAsync(r => r.CancelledAtUtc != null));
    }

    [Fact]
    public async Task Weekly_start_day_and_biweekly_anchor_are_validated_and_used()
    {
        var host = await TimeTestHost.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Biweekly, null, null, Reason)));
        await Assert.ThrowsAsync<ArgumentException>(() => host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Monthly, null, null, " ")));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Weekly, (DayOfWeek)9, null, Reason)));

        var sunday = await host.OrganizationSettings.ChangeTimesheetCadenceAsync(new(TimesheetCadenceKind.Weekly, DayOfWeek.Sunday, null, "Sunday weeks"));
        Assert.Equal(DayOfWeek.Sunday, sunday.Pending!.WeekStartDay);
        Assert.Equal(new DateOnly(2026, 10, 26), sunday.Pending.EffectiveFrom); // a stub Mon 10-26 – Sat 10-31, then Sunday weeks

        var biweekly = await host.OrganizationSettings.PreviewTimesheetCadenceAsync(new(TimesheetCadenceKind.Biweekly, null, new DateOnly(2026, 11, 2), Reason));
        Assert.Equal([new(new DateOnly(2026, 10, 26), new DateOnly(2026, 11, 1)), new(new DateOnly(2026, 11, 2), new DateOnly(2026, 11, 15))],
            biweekly.FirstPeriods);
    }
}

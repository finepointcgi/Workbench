using Microsoft.EntityFrameworkCore;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Time;

/// <summary>How periods are cut: the cadence plus what it needs (the weekly start day, the biweekly anchor).</summary>
public sealed record TimesheetPeriodSettings(TimesheetCadence Cadence, DayOfWeek WeekStartDay = DayOfWeek.Monday, DateOnly? BiweeklyAnchor = null)
{
    public static TimesheetPeriodSettings Of(TimesheetPeriodRule rule) => new(rule.Cadence, rule.WeekStartDay, rule.BiweeklyAnchor);

    /// <summary>The setting with only the fields its cadence uses, so equal settings compare equal.</summary>
    public TimesheetPeriodSettings Normalized() => Cadence switch
    {
        TimesheetCadence.Weekly => new(Cadence, WeekStartDay),
        TimesheetCadence.Biweekly => new(Cadence, BiweeklyAnchor?.DayOfWeek ?? DayOfWeek.Monday, BiweeklyAnchor),
        _ => new(Cadence),
    };
}

/// <summary>A setting and the first day it applies.</summary>
public sealed record TimesheetPeriodRuleEntry(TimesheetPeriodSettings Settings, DateOnly EffectiveFrom);

/// <summary>
/// Timesheet period math. Periods are derived, never stored as a calendar; a timesheet stores the period it was opened for.
/// When the setting changes, the new one applies from its effective date: a period of the old setting never runs past
/// it, and the first period of the new one starts on it (a short period when it isn't the new cadence's natural boundary).
/// </summary>
public static class TimesheetPeriods
{
    /// <summary>The period containing <paramref name="date"/> under one setting, ignoring any change.</summary>
    public static (DateOnly Start, DateOnly End) Natural(DateOnly date, TimesheetPeriodSettings settings)
    {
        switch (settings.Cadence)
        {
            case TimesheetCadence.SemiMonthly:
                return date.Day <= 15
                    ? (new DateOnly(date.Year, date.Month, 1), new DateOnly(date.Year, date.Month, 15))
                    : (new DateOnly(date.Year, date.Month, 16), LastDayOfMonth(date));
            case TimesheetCadence.Monthly:
                return (new DateOnly(date.Year, date.Month, 1), LastDayOfMonth(date));
            case TimesheetCadence.Biweekly:
                var anchor = settings.BiweeklyAnchor ?? throw new ArgumentException("A biweekly period needs an anchor date (the first day of any period).");
                var offset = date.DayNumber - anchor.DayNumber;
                var periods = offset >= 0 ? offset / 14 : -((-offset + 13) / 14);
                var biweeklyStart = anchor.AddDays(periods * 14);
                return (biweeklyStart, biweeklyStart.AddDays(13));
            default:
                var weeklyStart = date.AddDays(-(((int)date.DayOfWeek - (int)settings.WeekStartDay + 7) % 7));
                return (weeklyStart, weeklyStart.AddDays(6));
        }
    }

    /// <summary>
    /// The period containing <paramref name="date"/> under an effective-dated schedule. Before the first rule's effective
    /// date, the first rule applies (it is the setting the organization started with).
    /// </summary>
    public static (DateOnly Start, DateOnly End) For(DateOnly date, IReadOnlyList<TimesheetPeriodRuleEntry> schedule)
    {
        if (schedule.Count == 0)
        {
            throw new ArgumentException("The schedule needs at least one rule.", nameof(schedule));
        }

        var ordered = schedule.OrderBy(r => r.EffectiveFrom).ToList();
        var index = ordered.FindLastIndex(r => r.EffectiveFrom <= date);
        var rule = ordered[Math.Max(index, 0)];
        var (start, end) = Natural(date, rule.Settings);
        if (index >= 0 && start < rule.EffectiveFrom)
        {
            start = rule.EffectiveFrom;
        }

        if (index + 1 < ordered.Count && end >= ordered[index + 1].EffectiveFrom)
        {
            end = ordered[index + 1].EffectiveFrom.AddDays(-1);
        }

        return (start, end);
    }

    /// <summary>The first day after the period containing <paramref name="today"/>: where a change made today takes effect.</summary>
    public static DateOnly NextBoundary(DateOnly today, IReadOnlyList<TimesheetPeriodRuleEntry> schedule) => For(today, schedule).End.AddDays(1);

    /// <summary>The active (not cancelled) rules, or the configured default when the organization never saved one.</summary>
    public static async Task<IReadOnlyList<TimesheetPeriodRuleEntry>> LoadScheduleAsync(IWorkbenchDbContext db, TimesheetPeriodSettings fallback, CancellationToken cancellationToken)
    {
        var rules = await db.Set<TimesheetPeriodRule>().AsNoTracking().Where(r => r.CancelledAtUtc == null).ToListAsync(cancellationToken);
        return rules.Count == 0
            ? [new TimesheetPeriodRuleEntry(fallback, DateOnly.MinValue)]
            : rules.OrderBy(r => r.EffectiveFrom).ThenBy(r => r.ChangedAtUtc).Select(r => new TimesheetPeriodRuleEntry(TimesheetPeriodSettings.Of(r), r.EffectiveFrom)).ToList();
    }

    private static DateOnly LastDayOfMonth(DateOnly date) => new(date.Year, date.Month, DateTime.DaysInMonth(date.Year, date.Month));
}

namespace Workbench.Domain.ProjectAccounting;

public sealed class FiscalYear : Entity
{
    public int Year { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
}

public enum PeriodStatus
{
    Open,
    Closed,
}

/// <summary>An accounting period (usually a month). Closed periods reject new time, ODCs and changes.</summary>
public sealed class FiscalPeriod : Entity
{
    public Guid FiscalYearId { get; set; }

    /// <summary>For example <c>2026-10</c>.</summary>
    public required string Name { get; set; }

    public int Number { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public PeriodStatus Status { get; set; } = PeriodStatus.Open;
}

public enum TimesheetCadence
{
    /// <summary>Seven days from <see cref="TimesheetPeriodRule.WeekStartDay"/>.</summary>
    Weekly,

    /// <summary>The 1st–15th and the 16th–end of each month.</summary>
    SemiMonthly,

    /// <summary>Fourteen days, counted from <see cref="TimesheetPeriodRule.BiweeklyAnchor"/>.</summary>
    Biweekly,

    /// <summary>The calendar month.</summary>
    Monthly,
}

/// <summary>
/// The organization's timesheet period setting, effective-dated and append-only: a change is a new rule effective from
/// the next period boundary, so timesheets already started keep their period. A pending rule (not yet effective) can be
/// cancelled by a newer change; rules that took effect are never changed. No rows means the configured default.
/// </summary>
public sealed class TimesheetPeriodRule : Entity
{
    public TimesheetCadence Cadence { get; set; }

    /// <summary>Weekly: the first day of each period.</summary>
    public DayOfWeek WeekStartDay { get; set; } = DayOfWeek.Monday;

    /// <summary>Biweekly: any first day of a period; periods repeat every 14 days before and after it.</summary>
    public DateOnly? BiweeklyAnchor { get; set; }

    /// <summary>The first day the rule applies (a period boundary of the rule before it).</summary>
    public DateOnly EffectiveFrom { get; set; }

    public required string ChangedBy { get; set; }
    public DateTime ChangedAtUtc { get; set; }
    public required string Reason { get; set; }

    /// <summary>Set when a later change replaced this rule before it took effect.</summary>
    public DateTime? CancelledAtUtc { get; set; }
}

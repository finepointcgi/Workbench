namespace Workbench.Domain.Scheduling;

/// <summary>
/// A plan's own working calendar, overriding the company calendar (Monday to Friday less company holidays). A sheet has at
/// most one; without one the plan uses the company calendar.
/// </summary>
public sealed class PlanCalendar : Entity
{
    /// <summary>Monday to Friday as a <see cref="WorkDays"/> mask.</summary>
    public const int MondayToFriday = 0b0111110;

    public Guid SheetId { get; set; }

    /// <summary>The working weekdays: bit <c>(int)DayOfWeek</c> is set for each (Sunday is bit 0).</summary>
    public int WorkDays { get; set; } = MondayToFriday;

    /// <summary>When true, company holidays are non-working days on this plan too.</summary>
    public bool UseCompanyHolidays { get; set; } = true;

    public List<PlanCalendarException> Exceptions { get; } = [];
}

/// <summary>One day the plan's calendar treats differently: a project holiday or shutdown, or a working Saturday.</summary>
public sealed class PlanCalendarException : Entity
{
    public Guid CalendarId { get; set; }
    public DateOnly Date { get; set; }
    public required string Name { get; set; }

    /// <summary>True for an extra working day (a working weekend or holiday), false for a day off.</summary>
    public bool IsWorking { get; set; }
}

/// <summary>MS Project's constraint types, with its numbering (the <c>ConstraintType</c> of its XML).</summary>
public enum TaskConstraintType
{
    AsSoonAsPossible = 0,
    AsLateAsPossible = 1,
    MustStartOn = 2,
    MustFinishOn = 3,
    StartNoEarlierThan = 4,
    StartNoLaterThan = 5,
    FinishNoEarlierThan = 6,
    FinishNoLaterThan = 7,
}

/// <summary>
/// A date constraint on a plan task. Tasks without one are "as soon as possible" (their links push them later, never
/// earlier). One per task.
/// </summary>
public sealed class TaskConstraint : Entity
{
    public Guid SheetId { get; set; }
    public Guid ItemId { get; set; }
    public TaskConstraintType Type { get; set; }
    public DateOnly Date { get; set; }
}

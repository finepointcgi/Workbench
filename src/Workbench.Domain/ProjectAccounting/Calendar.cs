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
    Weekly,
    SemiMonthly,
}

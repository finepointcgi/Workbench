namespace Workbench.Contracts.TotalTime;

// Total time accounting (TTA): exempt salaried employees record every hour worked; hours beyond their standard week are
// uncompensated (not paid overtime). The organization picks how that labor is costed. Mirrors
// Workbench.Domain.ProjectAccounting.LaborCostMethod by name and value.
public enum LaborCostMethodKind { Standard, TotalTimeEffectiveRate, Hybrid }

public static class LaborCostMethods
{
    public static string Label(LaborCostMethodKind method) => method switch
    {
        LaborCostMethodKind.TotalTimeEffectiveRate => "Total time (effective rate)",
        LaborCostMethodKind.Hybrid => "Hybrid",
        _ => "Standard",
    };

    public static string Describe(LaborCostMethodKind method) => method switch
    {
        LaborCostMethodKind.TotalTimeEffectiveRate =>
            "Every hour an exempt employee works carries cost. The period's salary is divided by all hours worked, and that effective rate is used on every charge, direct and indirect.",
        LaborCostMethodKind.Hybrid =>
            "Exempt employees are costed at their hourly rate. In a period where they work more than their standard hours, the salary is spread over all hours worked instead.",
        _ => "Hours up to the standard week are costed at the hourly rate. An exempt employee's hours beyond it are recorded as uncompensated and carry no cost.",
    };

    /// <summary>The note shown on an exempt employee's timesheet.</summary>
    public static string TimesheetNote(LaborCostMethodKind method) =>
        "Record every hour you work, including hours beyond your standard week. As an exempt employee you aren't paid overtime: "
        + "extra hours are uncompensated time. "
        + method switch
        {
            LaborCostMethodKind.TotalTimeEffectiveRate => "Company policy spreads your salary over all hours you record, so every project you charge gets its share.",
            LaborCostMethodKind.Hybrid => "When you work more than your standard hours, company policy spreads your salary over all hours you record.",
            _ => "Company policy records the extra hours at no cost.",
        };
}

public sealed record LaborCostPolicyDto(Guid Id, DateOnly EffectiveFrom, LaborCostMethodKind Method, string Reason, string CreatedBy, DateTime CreatedAtUtc);

/// <param name="EarliestChange">The first period start a new method may take effect on (the next timesheet period).</param>
public sealed record LaborCostSettingsDto(LaborCostMethodKind Current, DateOnly EarliestChange, IReadOnlyList<LaborCostPolicyDto> History);

/// <param name="EffectiveFrom">Snapped to the start of the timesheet period containing it; it can't be before <see cref="LaborCostSettingsDto.EarliestChange"/>.</param>
public sealed record SetLaborCostMethodRequest(LaborCostMethodKind Method, DateOnly EffectiveFrom, string Reason);

/// <summary>Standard vs worked hours on one timesheet (no money, so anyone who can see the timesheet can see it).</summary>
public sealed record TimesheetTotalTimeDto(
    Guid TimesheetId,
    bool IsExempt,
    decimal StandardHours,
    decimal WorkedHours,
    decimal UncompensatedHours,
    LaborCostMethodKind Method,
    string PolicyNote);

/// <summary>One employee's approved timesheet period, costed by the method in effect.</summary>
/// <param name="PeriodSalary">For exempt staff, the standard hours at the hourly rate; null for non-exempt.</param>
/// <param name="EffectiveRate">Labor cost ÷ hours worked.</param>
/// <param name="EffectiveRateApplied">True when the salary was spread over all hours (TTA, or Hybrid in a period with uncompensated hours).</param>
public sealed record EffectiveRateRowDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    bool IsExempt,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    int Revision,
    LaborCostMethodKind Method,
    decimal StandardHours,
    decimal WorkedHours,
    decimal UncompensatedHours,
    decimal? HourlyRate,
    decimal? PeriodSalary,
    decimal LaborCost,
    decimal? EffectiveRate,
    bool EffectiveRateApplied);

/// <summary>Uncompensated hours of one employee on one project in one timesheet period.</summary>
public sealed record UncompensatedHoursRowDto(
    Guid EmployeeId,
    string EmployeeNumber,
    string EmployeeName,
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    decimal WorkedHours,
    decimal UncompensatedHours);

/// <summary>A project's labor cost under the standard method and under total time accounting.</summary>
public sealed record ProjectCostEffectRowDto(
    Guid ProjectId,
    string ProjectCode,
    string ProjectName,
    decimal Hours,
    decimal UncompensatedHours,
    decimal StandardCost,
    decimal TotalTimeCost,
    decimal AppliedCost)
{
    public decimal Difference => TotalTimeCost - StandardCost;
}

public sealed record TotalTimeReportDto(
    DateOnly From,
    DateOnly To,
    LaborCostMethodKind CurrentMethod,
    IReadOnlyList<EffectiveRateRowDto> EffectiveRates,
    IReadOnlyList<UncompensatedHoursRowDto> Uncompensated,
    IReadOnlyList<ProjectCostEffectRowDto> ProjectEffect,
    IReadOnlyList<string> Notes);

/// <summary>Total time accounting: the labor cost method, timesheet summaries and the uncompensated overtime reports.</summary>
public interface ITotalTimeService
{
    /// <summary>Finance/Admin.</summary>
    Task<LaborCostSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default);

    /// <summary>Finance/Admin. Takes effect from a future timesheet period boundary; earlier periods keep their method.</summary>
    Task<LaborCostSettingsDto> SetMethodAsync(SetLaborCostMethodRequest request, CancellationToken cancellationToken = default);

    /// <summary>Anyone who can see the timesheet.</summary>
    Task<TimesheetTotalTimeDto> GetTimesheetSummaryAsync(Guid timesheetId, CancellationToken cancellationToken = default);

    /// <summary>Finance/Admin. Approved timesheets whose period overlaps the range.</summary>
    Task<TotalTimeReportDto> GetReportAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken = default);
}

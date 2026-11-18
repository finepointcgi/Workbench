namespace Workbench.Domain.Payroll;

// The pay calendar and the pay profile: the records the core leave engine (eligibility, accruals per pay period) reads whether or not the payroll
// module is installed. The payroll module creates and maintains them; core only reads them. The rest of payroll is in the module.
//
// Payroll integration. Workbench never calculates payroll taxes: it sends approved hours to the payroll provider
// (Paylocity first), imports the provider's payroll register, reconciles it with the labor it accrued, and posts the
// payroll journal. Pay rates here are what employees are paid, not the cost rates projects are charged.

public enum PayFrequency
{
    Weekly,
    Biweekly,
    SemiMonthly,
    Monthly,
}

public enum PayType
{
    Hourly,
    Salaried,
}

/// <summary>How an employee is employed. Leave policies can be limited to some of these.</summary>
public enum EmploymentType
{
    FullTime,
    PartTime,
    Temporary,
    Seasonal,
    Intern,
}

/// <summary>Employees paid together on one schedule by one provider company.</summary>
public sealed class PayGroup : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public PayFrequency Frequency { get; set; } = PayFrequency.Biweekly;

    /// <summary>The payroll provider adapter, e.g. <c>paylocity</c>.</summary>
    public required string ProviderKey { get; set; }

    /// <summary>The company code (Paylocity company id) the provider files this group under.</summary>
    public required string CompanyCode { get; set; }

    /// <summary>
    /// The first day of any one pay period: weekly and biweekly calendars step from it. Ignored for semi-monthly
    /// (1st–15th, 16th–end) and monthly calendars.
    /// </summary>
    public DateOnly AnchorDate { get; set; }

    /// <summary>Days after the period end that the checks are dated.</summary>
    public int CheckDateOffsetDays { get; set; } = 5;

    public bool IsActive { get; set; } = true;
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>One pay period of a pay group. Generated per calendar year from the group's frequency.</summary>
public sealed class PayPeriod : Entity
{
    public Guid PayGroupId { get; set; }

    /// <summary>The year of <see cref="End"/>; <see cref="Number"/> counts from 1 within it.</summary>
    public int Year { get; set; }

    public int Number { get; set; }
    public DateOnly Start { get; set; }
    public DateOnly End { get; set; }
    public DateOnly CheckDate { get; set; }
}

/// <summary>
/// How an employee is paid. Sensitive: Finance and administrators only, and the rate is masked until revealed (which is
/// recorded as a security event). The rate history is <see cref="EmployeePayRate"/>; <see cref="PayType"/>,
/// <see cref="AnnualSalary"/>, <see cref="HourlyRate"/> and <see cref="EffectiveFrom"/> mirror its latest row. Anything
/// that needs the rate for a date (exports, reconciliation) reads the history (<c>Application.Payroll.PayRates</c>).
/// </summary>
public sealed class EmployeePayProfile : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid? PayGroupId { get; set; }

    /// <summary>The employee's id in the payroll provider (Paylocity employee id).</summary>
    public string? ProviderEmployeeId { get; set; }

    public PayType PayType { get; set; } = PayType.Hourly;

    public EmploymentType EmploymentType { get; set; } = EmploymentType.FullTime;

    /// <summary>Salaried employees: annual salary.</summary>
    public decimal? AnnualSalary { get; set; }

    /// <summary>Hourly employees: hourly pay rate.</summary>
    public decimal? HourlyRate { get; set; }

    public DateOnly EffectiveFrom { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    /// <summary>Hourly pay equivalent: the hourly rate, or the salary over 2,080 hours.</summary>
    public decimal? EquivalentHourlyRate => PayType == PayType.Salaried
        ? AnnualSalary is { } salary ? Math.Round(salary / 2080m, 4, MidpointRounding.AwayFromZero) : null
        : HourlyRate;
}

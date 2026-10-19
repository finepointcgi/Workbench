namespace Workbench.Domain.Hr;

// Benefits administration. Workbench keeps the plans, the elections and their costs; the payroll provider takes the
// deductions (exported per pay period through the provider adapter) and the employer cost reaches the GL through the
// payroll journal, split by each plan's expense account so it lands in the fringe pool.

public enum BenefitPlanType
{
    Medical,
    Dental,
    Vision,
    Life,
    Retirement401k,
    Fsa,
    Hsa,
    Other,
}

/// <summary>How much coverage a tiered plan gives.</summary>
public enum CoverageLevel
{
    EmployeeOnly,
    EmployeeSpouse,
    EmployeeChildren,
    Family,
}

public sealed class BenefitPlan : Entity
{
    /// <summary>Unique, upper-case (for example <c>MED-PPO</c>).</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public BenefitPlanType Type { get; set; }
    public string? Carrier { get; set; }

    /// <summary>The first and last day coverage under this plan can be in force (the plan year).</summary>
    public DateOnly StartsOn { get; set; }

    public DateOnly EndsOn { get; set; }

    /// <summary>The payroll provider's deduction code (Paylocity "Det Code").</summary>
    public required string DeductionCode { get; set; }

    /// <summary>Deducted before taxes (section 125 / 401(k) traditional).</summary>
    public bool IsPreTax { get; set; } = true;

    /// <summary>
    /// The GL expense account the employer cost is posted to (a fringe account, for example 6030). Null uses the payroll
    /// journal's default benefits account.
    /// </summary>
    public Guid? ExpenseAccountId { get; set; }

    /// <summary>Medical plans: qualifies its members for an HSA.</summary>
    public bool IsHighDeductible { get; set; }

    /// <summary>Only full-time employees may enroll.</summary>
    public bool FullTimeOnly { get; set; } = true;

    // Contribution plans (401(k), FSA, HSA): the employee elects an amount (FSA/HSA: per year) or a percent of pay (401(k)).

    /// <summary>FSA/HSA: the most an employee may elect for the year. 401(k): the most percent of pay (e.g. 75).</summary>
    public decimal? AnnualElectionLimit { get; set; }

    /// <summary>401(k): the employer matches this fraction of the employee's contribution (1 = dollar for dollar)...</summary>
    public decimal? EmployerMatchRate { get; set; }

    /// <summary>...on contributions up to this fraction of pay (0.04 = 4%).</summary>
    public decimal? EmployerMatchCapPercent { get; set; }

    /// <summary>HSA and other flat employer contributions, per month.</summary>
    public decimal? EmployerMonthlyContribution { get; set; }

    public string? Description { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }

    public bool IsContribution => Type is BenefitPlanType.Retirement401k or BenefitPlanType.Fsa or BenefitPlanType.Hsa;

    public List<BenefitTier> Tiers { get; } = [];
}

/// <summary>A coverage level of a tiered plan and its monthly cost to the employee and the employer.</summary>
public sealed class BenefitTier : Entity
{
    public Guid PlanId { get; set; }
    public CoverageLevel Coverage { get; set; }
    public decimal EmployeeMonthlyCost { get; set; }
    public decimal EmployerMonthlyCost { get; set; }
}

/// <summary>Open enrollment: when employees may change any election, for coverage starting on <see cref="CoverageStartsOn"/>.</summary>
public sealed class OpenEnrollmentWindow : Entity
{
    public required string Name { get; set; }
    public DateOnly OpensOn { get; set; }
    public DateOnly ClosesOn { get; set; }
    public DateOnly CoverageStartsOn { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

public enum DependentRelationship
{
    Spouse,
    DomesticPartner,
    Child,
    Other,
}

public sealed class BenefitDependent : Entity
{
    public Guid EmployeeId { get; set; }
    public required string FirstName { get; set; }
    public required string LastName { get; set; }
    public DependentRelationship Relationship { get; set; }
    public DateOnly BirthDate { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ElectionSource
{
    NewHire,
    OpenEnrollment,
    LifeEvent,

    /// <summary>Entered by HR (corrections, terminations).</summary>
    Administrative,

    /// <summary>401(k) contribution changes, allowed any time.</summary>
    AnyTime,
}

/// <summary>
/// One employee's election in one plan, in force from <see cref="CoverageStart"/> to <see cref="CoverageEnd"/> (open when
/// null). A new election for the same plan type ends the previous one the day before it starts. A waiver is an election
/// with <see cref="IsWaived"/>.
/// </summary>
public sealed class BenefitElection : Entity
{
    public Guid EmployeeId { get; set; }
    public Guid PlanId { get; set; }
    public BenefitPlanType PlanType { get; set; }
    public Guid? TierId { get; set; }
    public CoverageLevel? Coverage { get; set; }

    /// <summary>FSA/HSA: annual election in dollars; 401(k): percent of pay (6 = 6%).</summary>
    public decimal? ContributionAmount { get; set; }

    public bool IsWaived { get; set; }
    public DateOnly CoverageStart { get; set; }
    public DateOnly? CoverageEnd { get; set; }
    public ElectionSource Source { get; set; }
    public Guid? WindowId { get; set; }
    public Guid? LifeEventId { get; set; }
    public required string ElectedBy { get; set; }
    public DateTime ElectedAtUtc { get; set; }

    public List<BenefitElectionDependent> Dependents { get; } = [];

    public bool InForceOn(DateOnly date) => !IsWaived && CoverageStart <= date && (CoverageEnd is null || CoverageEnd >= date);
}

public sealed class BenefitElectionDependent : Entity
{
    public Guid ElectionId { get; set; }
    public Guid DependentId { get; set; }
}

public enum LifeEventKind
{
    Marriage,
    Divorce,
    BirthOrAdoption,
    DeathOfDependent,
    LossOfOtherCoverage,
    GainOfOtherCoverage,
    Other,
}

public enum LifeEventStatus
{
    /// <summary>Reported by the employee; HR verifies it.</summary>
    Pending,

    /// <summary>Verified: the employee may change elections until <see cref="LifeEvent.WindowEndsOn"/>.</summary>
    Approved,
    Rejected,
}

/// <summary>A qualifying life event: opens a 30-day window (from the event date) to change elections.</summary>
public sealed class LifeEvent : Entity
{
    public Guid EmployeeId { get; set; }
    public LifeEventKind Kind { get; set; }
    public DateOnly EventDate { get; set; }
    public DateOnly WindowEndsOn { get; set; }
    public string? Note { get; set; }
    public LifeEventStatus Status { get; set; } = LifeEventStatus.Pending;
    public required string ReportedBy { get; set; }
    public DateTime ReportedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
}

/// <summary>A deduction file sent to the payroll provider for one pay period: the evidence of what was sent.</summary>
public sealed class BenefitDeductionExport : Entity
{
    public Guid PayGroupId { get; set; }
    public Guid PayPeriodId { get; set; }
    public required string FileName { get; set; }
    public required string Content { get; set; }
    public int LineCount { get; set; }
    public decimal EmployeeTotal { get; set; }
    public decimal EmployerTotal { get; set; }
    public required string ExportedBy { get; set; }
    public DateTime ExportedAtUtc { get; set; }
}

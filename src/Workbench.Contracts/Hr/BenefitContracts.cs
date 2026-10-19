namespace Workbench.Contracts.Hr;

public enum BenefitPlanType { Medical, Dental, Vision, Life, Retirement401k, Fsa, Hsa, Other }

public enum CoverageLevel { EmployeeOnly, EmployeeSpouse, EmployeeChildren, Family }

public enum DependentRelationship { Spouse, DomesticPartner, Child, Other }

public enum ElectionSource { NewHire, OpenEnrollment, LifeEvent, Administrative, AnyTime }

public enum LifeEventKind { Marriage, Divorce, BirthOrAdoption, DeathOfDependent, LossOfOtherCoverage, GainOfOtherCoverage, Other }

public enum LifeEventStatus { Pending, Approved, Rejected }

public static class BenefitLabels
{
    public static string Label(BenefitPlanType t) => t switch
    {
        BenefitPlanType.Retirement401k => "401(k)",
        BenefitPlanType.Fsa => "FSA",
        BenefitPlanType.Hsa => "HSA",
        _ => t.ToString(),
    };

    public static string Label(CoverageLevel c) => c switch
    {
        CoverageLevel.EmployeeOnly => "Employee only",
        CoverageLevel.EmployeeSpouse => "Employee + spouse/partner",
        CoverageLevel.EmployeeChildren => "Employee + children",
        _ => "Family",
    };

    public static string Label(LifeEventKind k) => k switch
    {
        LifeEventKind.BirthOrAdoption => "Birth or adoption",
        LifeEventKind.DeathOfDependent => "Death of a dependent",
        LifeEventKind.LossOfOtherCoverage => "Loss of other coverage",
        LifeEventKind.GainOfOtherCoverage => "Gain of other coverage",
        _ => k.ToString(),
    };

    public static bool IsContribution(BenefitPlanType t) => t is BenefitPlanType.Retirement401k or BenefitPlanType.Fsa or BenefitPlanType.Hsa;
}

public sealed record BenefitTierDto(Guid Id, CoverageLevel Coverage, decimal EmployeeMonthlyCost, decimal EmployerMonthlyCost);

public sealed record BenefitPlanDto(
    Guid Id,
    string Code,
    string Name,
    BenefitPlanType Type,
    string? Carrier,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string DeductionCode,
    bool IsPreTax,
    Guid? ExpenseAccountId,
    string? ExpenseAccountNumber,
    string? ExpenseAccountName,

    /// <summary>The expense account is classified Fringe, so the employer cost lands in the fringe pool.</summary>
    bool ExpenseAccountIsFringe,
    bool IsHighDeductible,
    bool FullTimeOnly,
    decimal? AnnualElectionLimit,
    decimal? EmployerMatchRate,
    decimal? EmployerMatchCapPercent,
    decimal? EmployerMonthlyContribution,
    string? Description,
    bool IsActive,
    IReadOnlyList<BenefitTierDto> Tiers,
    int Enrolled);

public sealed record SaveBenefitTier(CoverageLevel Coverage, decimal EmployeeMonthlyCost, decimal EmployerMonthlyCost);

public sealed record SaveBenefitPlanRequest(
    string Code,
    string Name,
    BenefitPlanType Type,
    string? Carrier,
    DateOnly StartsOn,
    DateOnly EndsOn,
    string DeductionCode,
    bool IsPreTax,
    Guid? ExpenseAccountId,
    bool IsHighDeductible,
    bool FullTimeOnly,
    decimal? AnnualElectionLimit,
    decimal? EmployerMatchRate,
    decimal? EmployerMatchCapPercent,
    decimal? EmployerMonthlyContribution,
    string? Description,
    bool IsActive,
    IReadOnlyList<SaveBenefitTier> Tiers);

public sealed record SetPlanAccountRequest(Guid? ExpenseAccountId);

/// <summary>An active expense account a plan's employer cost can post to.</summary>
public sealed record BenefitAccountOptionDto(Guid Id, string Number, string Name, bool IsFringe);

public sealed record OpenEnrollmentWindowDto(Guid Id, string Name, DateOnly OpensOn, DateOnly ClosesOn, DateOnly CoverageStartsOn, bool IsOpen);

public sealed record SaveOpenEnrollmentWindowRequest(string Name, DateOnly OpensOn, DateOnly ClosesOn, DateOnly CoverageStartsOn);

public sealed record DependentDto(Guid Id, string FirstName, string LastName, DependentRelationship Relationship, DateOnly BirthDate, bool IsActive)
{
    public string Name => $"{FirstName} {LastName}";
}

public sealed record SaveDependentRequest(string FirstName, string LastName, DependentRelationship Relationship, DateOnly BirthDate, bool IsActive = true);

public sealed record ElectionDto(
    Guid Id,
    Guid PlanId,
    string PlanCode,
    string PlanName,
    BenefitPlanType PlanType,
    CoverageLevel? Coverage,
    decimal? ContributionAmount,
    bool IsWaived,
    DateOnly CoverageStart,
    DateOnly? CoverageEnd,
    ElectionSource Source,
    decimal EmployeeMonthlyCost,
    decimal EmployerMonthlyCost,
    IReadOnlyList<Guid> DependentIds,
    string ElectedBy,
    DateTime ElectedAtUtc);

public sealed record LifeEventDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    LifeEventKind Kind,
    DateOnly EventDate,
    DateOnly WindowEndsOn,
    string? Note,
    LifeEventStatus Status,
    string ReportedBy,
    DateTime ReportedAtUtc,
    string? DecidedBy);

/// <summary>Why the employee may change elections now, and for coverage from when.</summary>
public sealed record EnrollmentOpportunityDto(ElectionSource Source, string Label, DateOnly CoverageStart, DateOnly EndsOn, Guid? WindowId, Guid? LifeEventId);

/// <summary>An employee's benefits: current and future elections, dependents, life events and what they may do now.</summary>
public sealed record EmployeeBenefitsDto(
    Guid EmployeeId,
    string EmployeeName,
    IReadOnlyList<ElectionDto> Elections,
    IReadOnlyList<DependentDto> Dependents,
    IReadOnlyList<LifeEventDto> LifeEvents,
    IReadOnlyList<EnrollmentOpportunityDto> Opportunities,
    IReadOnlyList<BenefitPlanDto> AvailablePlans,
    OpenEnrollmentWindowDto? OpenWindow,

    /// <summary>Elections made for the open window (by plan type), so the page can show what's done.</summary>
    IReadOnlyList<BenefitPlanType> ElectedThisWindow);

/// <summary>
/// An election (or a waiver) in one plan. Tiered plans need <see cref="Coverage"/> and the dependents it covers;
/// FSA/HSA take an annual <see cref="ContributionAmount"/> and 401(k) a percent of pay. <see cref="Source"/> picks the
/// opportunity (open enrollment, life event, new hire); 401(k) changes are allowed any time.
/// </summary>
public sealed record ElectBenefitRequest(
    Guid PlanId,
    CoverageLevel? Coverage,
    IReadOnlyList<Guid>? DependentIds,
    decimal? ContributionAmount,
    ElectionSource Source,
    bool Waive = false,
    Guid? LifeEventId = null);

/// <summary>HR waives a plan type for someone (or ends coverage on a date).</summary>
public sealed record WaiveBenefitRequest(BenefitPlanType PlanType, ElectionSource Source, Guid? LifeEventId = null);

public sealed record ReportLifeEventRequest(LifeEventKind Kind, DateOnly EventDate, string? Note);

public sealed record DecideLifeEventRequest(bool Approve, string? Comment);

/// <summary>
/// Benefits. Plans and enrollment windows: everyone reads, HR (and administrators) change; a plan's GL expense account can
/// also be set by Finance. Employees manage their own dependents, elections and life events (self-service); HR may act for
/// anyone and verifies life events.
/// </summary>
public interface IBenefitService
{
    Task<IReadOnlyList<BenefitPlanDto>> ListPlansAsync(bool includeInactive = false, CancellationToken cancellationToken = default);

    Task<BenefitPlanDto> SavePlanAsync(Guid? planId, SaveBenefitPlanRequest request, CancellationToken cancellationToken = default);

    /// <summary>HR or Finance: active expense accounts (fringe first) for mapping plans.</summary>
    Task<IReadOnlyList<BenefitAccountOptionDto>> ListExpenseAccountsAsync(CancellationToken cancellationToken = default);

    /// <summary>HR or Finance: the GL expense account the plan's employer cost posts to.</summary>
    Task<BenefitPlanDto> SetPlanAccountAsync(Guid planId, Guid? expenseAccountId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OpenEnrollmentWindowDto>> ListWindowsAsync(CancellationToken cancellationToken = default);

    Task<OpenEnrollmentWindowDto> SaveWindowAsync(Guid? windowId, SaveOpenEnrollmentWindowRequest request, CancellationToken cancellationToken = default);

    Task<EmployeeBenefitsDto> GetMineAsync(CancellationToken cancellationToken = default);

    /// <summary>HR, administrators or the employee.</summary>
    Task<EmployeeBenefitsDto> GetForEmployeeAsync(Guid employeeId, CancellationToken cancellationToken = default);

    Task<DependentDto> SaveDependentAsync(Guid employeeId, Guid? dependentId, SaveDependentRequest request, CancellationToken cancellationToken = default);

    Task<ElectionDto> ElectAsync(Guid employeeId, ElectBenefitRequest request, CancellationToken cancellationToken = default);

    Task<LifeEventDto> ReportLifeEventAsync(Guid employeeId, ReportLifeEventRequest request, CancellationToken cancellationToken = default);

    /// <summary>HR: life events waiting to be verified.</summary>
    Task<IReadOnlyList<LifeEventDto>> ListPendingLifeEventsAsync(CancellationToken cancellationToken = default);

    Task<LifeEventDto> DecideLifeEventAsync(Guid lifeEventId, DecideLifeEventRequest request, CancellationToken cancellationToken = default);
}

public sealed record DeductionLineDto(
    Guid EmployeeId,
    string EmployeeName,
    string? ProviderEmployeeId,
    string PlanCode,
    string DeductionCode,
    decimal EmployeeAmount,
    decimal? EmployeePercent,
    decimal EmployerAmount,
    bool IsPreTax,
    string? Issue);

public sealed record DeductionPreviewDto(
    Guid PayGroupId,
    string PayGroupCode,
    Guid PayPeriodId,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string ProviderKey,
    IReadOnlyList<DeductionLineDto> Lines,
    DateTime? LastExportedAtUtc)
{
    public decimal EmployeeTotal => Lines.Where(l => l.Issue is null).Sum(l => l.EmployeeAmount);

    public decimal EmployerTotal => Lines.Where(l => l.Issue is null).Sum(l => l.EmployerAmount);
}

/// <summary>A pay period deductions can be exported for (the last few and the next ones of each active pay group).</summary>
public sealed record DeductionPeriodDto(Guid PayGroupId, string PayGroupCode, Guid PayPeriodId, DateOnly Start, DateOnly End, DateOnly CheckDate, DateTime? LastExportedAtUtc);

public sealed record DeductionFileDto(string FileName, string ContentType, string Content);

/// <summary>
/// Benefit deductions per pay period for the payroll provider (Paylocity first), from the elections in force on the
/// period's last day. HR, Finance and administrators; exports are security events (<c>export.benefit_deductions</c>).
/// </summary>
public interface IBenefitDeductionService
{
    /// <summary>Recent and upcoming pay periods of the active pay groups (four back, two ahead of today).</summary>
    Task<IReadOnlyList<DeductionPeriodDto>> ListPeriodsAsync(CancellationToken cancellationToken = default);

    Task<DeductionPreviewDto> PreviewAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default);

    Task<DeductionFileDto> ExportAsync(Guid payGroupId, Guid payPeriodId, CancellationToken cancellationToken = default);
}

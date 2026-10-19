using Workbench.Domain.Hr;
using Workbench.Domain.Payroll;

namespace Workbench.Application.Hr;

/// <summary>
/// The enrollment rules and cost arithmetic, kept pure so they can be tested on their own:
/// <list type="bullet">
/// <item>Elections change only in an open enrollment window, within 30 days of hire, or within 30 days of a verified
/// life event; 401(k) contributions change any time; HR may make administrative changes.</item>
/// <item>The coverage level must match the dependents covered: Employee only (none), + spouse/partner (exactly one),
/// + children (one or more children, no spouse), Family (a spouse/partner and one or more children). Children are
/// covered until 26.</item>
/// <item>An HSA needs a high-deductible medical plan and can't be combined with a (general purpose) FSA.</item>
/// <item>Costs are monthly; payroll deducts <c>monthly × 12 ÷ pay periods per year</c>.</item>
/// </list>
/// </summary>
public static class BenefitRules
{
    public const int EnrollmentWindowDays = 30;
    public const int ChildAgeLimit = 26;

    public static int PeriodsPerYear(PayFrequency frequency) => frequency switch
    {
        PayFrequency.Weekly => 52,
        PayFrequency.Biweekly => 26,
        PayFrequency.SemiMonthly => 24,
        _ => 12,
    };

    public static decimal PerPeriod(decimal monthly, PayFrequency frequency) =>
        Math.Round(monthly * 12m / PeriodsPerYear(frequency), 2, MidpointRounding.AwayFromZero);

    public static DateOnly FirstOfNextMonth(DateOnly date) => new DateOnly(date.Year, date.Month, 1).AddMonths(1);

    /// <summary>Coverage for a new hire starts on the hire date when it's the 1st, otherwise the 1st of the next month.</summary>
    public static DateOnly NewHireCoverageStart(DateOnly hireDate) => hireDate.Day == 1 ? hireDate : FirstOfNextMonth(hireDate);

    /// <summary>A birth or adoption is covered from the day it happened; other events from the 1st of the next month.</summary>
    public static DateOnly LifeEventCoverageStart(LifeEventKind kind, DateOnly eventDate) =>
        kind == LifeEventKind.BirthOrAdoption ? eventDate : FirstOfNextMonth(eventDate);

    public static int Age(DateOnly birth, DateOnly on)
    {
        var age = on.Year - birth.Year;
        return on < birth.AddYears(age) ? age - 1 : age;
    }

    /// <summary>Throws when the dependents don't fit the coverage level (see the class summary).</summary>
    public static void CheckCoverage(CoverageLevel coverage, IReadOnlyList<BenefitDependent> dependents, DateOnly coverageStart)
    {
        var partners = dependents.Count(d => d.Relationship is DependentRelationship.Spouse or DependentRelationship.DomesticPartner);
        var children = dependents.Where(d => d.Relationship == DependentRelationship.Child).ToList();
        if (dependents.Any(d => d.Relationship == DependentRelationship.Other))
        {
            throw new ArgumentException("Only a spouse, domestic partner or children can be covered.");
        }

        if (children.FirstOrDefault(c => Age(c.BirthDate, coverageStart) >= ChildAgeLimit) is { } adult)
        {
            throw new ArgumentException($"{adult.FirstName} {adult.LastName} is {ChildAgeLimit} or older on {coverageStart:yyyy-MM-dd}; children are covered until {ChildAgeLimit}.");
        }

        if (partners > 1)
        {
            throw new ArgumentException("Only one spouse or domestic partner can be covered.");
        }

        var ok = coverage switch
        {
            CoverageLevel.EmployeeOnly => partners == 0 && children.Count == 0,
            CoverageLevel.EmployeeSpouse => partners == 1 && children.Count == 0,
            CoverageLevel.EmployeeChildren => partners == 0 && children.Count > 0,
            _ => partners == 1 && children.Count > 0,
        };
        if (!ok)
        {
            throw new ArgumentException(coverage switch
            {
                CoverageLevel.EmployeeOnly => "Employee-only coverage can't include dependents.",
                CoverageLevel.EmployeeSpouse => "Employee + spouse/partner coverage needs exactly your spouse or partner, and no children.",
                CoverageLevel.EmployeeChildren => "Employee + children coverage needs at least one child and no spouse or partner.",
                _ => "Family coverage needs your spouse or partner and at least one child.",
            });
        }
    }

    /// <summary>
    /// Monthly cost of an election to the employee and the employer. Tiered plans: the tier's costs. FSA/HSA: the annual
    /// election ÷ 12, plus any flat employer contribution. 401(k): the percent of <paramref name="annualPay"/> ÷ 12, and the
    /// employer match on contributions up to the cap (zero when the pay isn't known).
    /// </summary>
    public static (decimal Employee, decimal Employer) MonthlyCost(BenefitPlan plan, BenefitTier? tier, BenefitElection election, decimal? annualPay)
    {
        if (election.IsWaived)
        {
            return (0m, 0m);
        }

        switch (plan.Type)
        {
            case BenefitPlanType.Fsa or BenefitPlanType.Hsa:
                return (Round((election.ContributionAmount ?? 0m) / 12m), plan.EmployerMonthlyContribution ?? 0m);
            case BenefitPlanType.Retirement401k:
                var percent = (election.ContributionAmount ?? 0m) / 100m;
                var pay = annualPay ?? 0m;
                var matched = Math.Min(percent, plan.EmployerMatchCapPercent ?? 0m) * (plan.EmployerMatchRate ?? 0m);
                return (Round(pay * percent / 12m), Round(pay * matched / 12m));
            default:
                return (tier?.EmployeeMonthlyCost ?? 0m, tier?.EmployerMonthlyCost ?? 0m);
        }
    }

    private static decimal Round(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);
}

namespace Workbench.Contracts.Billing;

/// <summary>Which indirect rates to apply: provisional billing rates during the year, actual (final) rates for true-up.</summary>
public enum RateBasis
{
    Provisional,
    Target,
    Actual,

    /// <summary>Forward pricing rates, for proposals (see proposal pricing).</summary>
    ForwardPricing,
}

/// <summary>
/// Fully burdened, allowable cost for one WBS element (and its CLIN) over a date range.
/// Unallowable costs are excluded from every column; <see cref="UnallowableExcluded"/> reports how much was left out.
/// </summary>
public sealed record BurdenedCostLine(
    Guid ProjectId,
    Guid WbsElementId,
    string WbsCode,
    Guid? ClinId,
    decimal DirectLaborHours,
    decimal DirectLabor,
    decimal Fringe,
    decimal Overhead,
    decimal GeneralAndAdministrative,
    decimal DirectOdc,
    decimal UnallowableExcluded)
{
    public decimal TotalCost => DirectLabor + Fringe + Overhead + GeneralAndAdministrative + DirectOdc;
}

/// <summary>The rate applied to each pool, for display on vouchers and audit.</summary>
public sealed record AppliedRates(RateBasis Basis, int FiscalYear, decimal Fringe, decimal Overhead, decimal GeneralAndAdministrative);

public sealed record BurdenedCostResult(IReadOnlyList<BurdenedCostLine> Lines, IReadOnlyList<AppliedRates> Rates);

/// <summary>
/// Shared seam between indirect rates (which implements it) and billing / financials (which consume it).
/// Applies the pool rates for the fiscal year of each cost's date, in cascade order:
/// fringe on direct labor; overhead on direct labor + fringe; G&amp;A on total cost input.
/// </summary>
public interface IBurdenedCostService
{
    /// <summary>Approved, allowable cost for a project from <paramref name="from"/> through <paramref name="to"/>, one line per WBS element with cost.</summary>
    Task<BurdenedCostResult> GetProjectCostAsync(Guid projectId, DateOnly from, DateOnly to, RateBasis basis, CancellationToken cancellationToken = default);
}

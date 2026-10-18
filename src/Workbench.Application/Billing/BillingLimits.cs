using Workbench.Contracts.Billing;

namespace Workbench.Application.Billing;

/// <summary>The fee rate billing may apply on a CLIN and the most fee this invoice may add (null = no limit).</summary>
public sealed record FeeLimit(decimal FeeRate, decimal? MaxFee, string? Note);

/// <summary>
/// Fee already on the contract's live invoices (and on earlier CLINs and years of this one): by CLIN, and by CLIN and the
/// fiscal year of the cost it was billed on (fee lines without a year count in their invoice period's year).
/// </summary>
public sealed record FeeToDate(IReadOnlyDictionary<Guid, decimal> ByClin, IReadOnlyDictionary<(Guid ClinId, int FiscalYear), decimal> ByClinAndYear)
{
    public decimal Total => ByClin.Values.Sum();

    public decimal Of(Guid clinId) => ByClin.GetValueOrDefault(clinId);

    public decimal OfYear(int fiscalYear) => ByClinAndYear.Where(kv => kv.Key.FiscalYear == fiscalYear).Sum(kv => kv.Value);

    public decimal OfYear(Guid clinId, int fiscalYear) => ByClinAndYear.GetValueOrDefault((clinId, fiscalYear));
}

/// <summary>
/// Contract limits billing must respect, registered by other modules (ceilings registers the indirect rate, fee rate and
/// fee amount caps). Without a registration billing bills what it computes.
/// </summary>
public interface IBillingLimits
{
    /// <summary>The billable burdened cost of a project (for example with indirect rates capped at the contract's ceilings).</summary>
    Task<BurdenedCostResult> AdjustCostAsync(IWorkbenchDbContext db, Guid projectId, DateOnly from, DateOnly to, RateBasis basis, BurdenedCostResult cost, CancellationToken ct);

    /// <summary>
    /// The fiscal years with fee caps of their own on the CLIN (or the whole contract). When there are any, billing splits the
    /// CLIN's fee by the fiscal year of the cost and asks <see cref="FeeLimitAsync"/> once per year.
    /// </summary>
    Task<IReadOnlySet<int>> FeeCapYearsAsync(IWorkbenchDbContext db, Guid contractId, Guid clinId, CancellationToken ct);

    /// <summary>
    /// The fee rate and the most fee a new invoice may add on a CLIN, on cost of <paramref name="fiscalYear"/> (null: not split
    /// by year, or cost outside the fiscal calendar; only caps for every year apply then).
    /// </summary>
    Task<FeeLimit> FeeLimitAsync(IWorkbenchDbContext db, Guid contractId, Guid clinId, int? fiscalYear, decimal feeRate, FeeToDate prior, CancellationToken ct);
}

using Workbench.Contracts.Billing;

namespace Workbench.Application.Billing;

/// <summary>The fee rate billing may apply on a CLIN and the most fee this invoice may add (null = no limit).</summary>
public sealed record FeeLimit(decimal FeeRate, decimal? MaxFee, string? Note);

/// <summary>
/// Contract limits billing must respect, registered by other modules (ceilings registers the indirect rate, fee rate and
/// fee amount caps). Without a registration billing bills what it computes.
/// </summary>
public interface IBillingLimits
{
    /// <summary>The billable burdened cost of a project (for example with indirect rates capped at the contract's ceilings).</summary>
    Task<BurdenedCostResult> AdjustCostAsync(IWorkbenchDbContext db, Guid projectId, DateOnly from, DateOnly to, RateBasis basis, BurdenedCostResult cost, CancellationToken ct);

    /// <summary>
    /// The fee rate and the most fee a new invoice may add on a CLIN. <paramref name="priorFeeByClin"/> is the fee already on
    /// the contract's live invoices (and on earlier CLINs of this one), by CLIN.
    /// </summary>
    Task<FeeLimit> FeeLimitAsync(IWorkbenchDbContext db, Guid contractId, Guid clinId, decimal feeRate, IReadOnlyDictionary<Guid, decimal> priorFeeByClin, CancellationToken ct);
}

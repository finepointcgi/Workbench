namespace Workbench.Application.ProjectAccounting;

/// <summary>
/// The latest approved estimate at completion (cost) of projects, for the rules that need an estimate to complete (progress payment requests on
/// SF 1443). The estimates are the billing module's (revenue recognition), so the module that needs them reads them through this interface instead
/// of through the billing module's tables. There is no registration in the product itself: a host without the billing module has no estimates,
/// and a caller that finds no source treats that as "no approved estimate", as it does for a project nobody has estimated.
/// </summary>
public interface IEstimateAtCompletionSource
{
    /// <summary>For each project that has one, its latest approved project-level estimate at completion (cost) dated on or before <paramref name="through"/>.</summary>
    Task<IReadOnlyDictionary<Guid, decimal>> LatestApprovedCostAsync(IReadOnlyCollection<Guid> projectIds, DateOnly through, CancellationToken cancellationToken = default);
}

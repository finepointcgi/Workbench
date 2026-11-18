namespace Workbench.Contracts.Compliance;

// The seam between proposal pricing (estimating, the capture module) and the CAS consistency check (the government contracting module):
// the check asks each source what the open proposals were priced with and compares it with the books. They stay in the shipped
// contracts so neither module references the other; without the capture module there is no source and the check has no estimating side.

/// <summary>One indirect rate cascade step a locked proposal price was built with, as words (for example pool "Overhead", base "Direct labor").</summary>
public sealed record PriceCascadeFact(string Pool, string Base);

/// <summary>A locked (submitted) version of a proposal's price and the cascade it kept.</summary>
public sealed record LockedPriceFact(int Version, IReadOnlyList<PriceCascadeFact> Cascade);

/// <summary>
/// What one open proposal (draft or submitted) was priced with. <see cref="RateOverrides"/> lists the working price's indirect rate overrides ("Overhead 25%");
/// <see cref="WorkingRateBasis"/> is the rate basis it uses when that is not forward pricing, else null.
/// </summary>
public sealed record ProposalPricingFact(string Number, string Link, IReadOnlyList<string> RateOverrides, string? WorkingRateBasis, IReadOnlyList<LockedPriceFact> LockedVersions);

/// <summary>Estimating facts for the CAS consistency check. The capture module registers an implementation; nothing else does.</summary>
public interface IPricingConsistencySource
{
    Task<IReadOnlyList<ProposalPricingFact>> OpenProposalsAsync(CancellationToken cancellationToken = default);
}

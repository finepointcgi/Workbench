namespace Workbench.Contracts.Compliance;

// The one seam between purchasing (RFQs) and the contractor purchasing system review (CPSR), which belongs to the government
// contracting module. Purchasing implements the source; the module reads it. They stay in the shipped contracts so purchasing
// never references the module.

/// <summary>Competition evidence from a request for quotes (supplied by the RFQ module through <see cref="IProcurementCompetitionSource"/>).</summary>
public sealed record CompetitionEvidenceDto(string Reference, int QuotesSolicited, int QuotesReceived, string? AwardBasis = null, string? Link = null);

/// <summary>
/// Competition evidence from requests for quotes, keyed by purchase order. The procurement (RFQ) module registers an
/// implementation; without the government contracting module nothing reads it.
/// </summary>
public interface IProcurementCompetitionSource
{
    Task<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>> GetForPurchaseOrdersAsync(IReadOnlyCollection<Guid> purchaseOrderIds, CancellationToken cancellationToken = default);
}

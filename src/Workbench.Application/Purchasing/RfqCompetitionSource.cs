using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Compliance;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// CPSR competition evidence from awarded RFQs: for each purchase order an award created, the vendors invited, the
/// responsive quotes and the award basis (sole-source justification or price reasonableness). Replaces the compliance
/// module's no-op default.
/// </summary>
public sealed class RfqCompetitionSource(IWorkbenchDbContextFactory dbFactory) : IProcurementCompetitionSource
{
    public async Task<IReadOnlyDictionary<Guid, CompetitionEvidenceDto>> GetForPurchaseOrdersAsync(IReadOnlyCollection<Guid> purchaseOrderIds, CancellationToken cancellationToken = default)
    {
        if (purchaseOrderIds.Count == 0)
        {
            return new Dictionary<Guid, CompetitionEvidenceDto>();
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = purchaseOrderIds.ToList();
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => new { p.Id, p.Number }).ToListAsync(cancellationToken);
        var awards = await db.Set<RfqAward>().AsNoTracking().ToListAsync(cancellationToken);
        var rfqIds = awards.Select(a => a.RfqId).Distinct().ToList();
        var rfqNumbers = await db.Set<Rfq>().AsNoTracking().Where(r => rfqIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Number, cancellationToken);

        var byPo = new Dictionary<string, RfqAward>(StringComparer.OrdinalIgnoreCase);
        foreach (var award in awards.OrderBy(a => a.AwardedAtUtc))
        {
            foreach (var number in award.PurchaseOrderNumbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                byPo[number] = award;
            }
        }

        var result = new Dictionary<Guid, CompetitionEvidenceDto>();
        foreach (var order in orders)
        {
            if (byPo.TryGetValue(order.Number, out var award))
            {
                var basis = award.SoleSource ? $"Sole source: {award.SoleSourceJustification}" : award.PriceReasonableness;
                result[order.Id] = new CompetitionEvidenceDto(rfqNumbers.GetValueOrDefault(award.RfqId) ?? "RFQ", award.VendorsInvited, award.QuotesReceived,
                    basis, $"purchasing/rfqs/{award.RfqId}");
            }
        }

        return result;
    }
}

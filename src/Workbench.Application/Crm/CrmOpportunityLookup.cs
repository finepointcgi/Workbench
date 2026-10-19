using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Pricing;
using D = Workbench.Domain.Crm;

namespace Workbench.Application.Crm;

/// <summary>
/// The proposal dialog's opportunity picker over the CRM. It reads names and agencies directly rather than through
/// <see cref="Contracts.Crm.IOpportunityService"/>, so a Pricing-only user (who can't open the pipeline) can still link a
/// proposal; the pricing permission decorator requires CanPrice. Lists open opportunities plus won ones (a late price).
/// </summary>
public sealed class CrmOpportunityLookup(IWorkbenchDbContextFactory dbFactory) : IPricingOpportunityLookup
{
    private static readonly D.OpportunityStage[] Closed = [D.OpportunityStage.Lost, D.OpportunityStage.NoBid];

    public async Task<IReadOnlyList<OpportunityRefDto>> SearchAsync(string? search, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var query = db.Set<D.Opportunity>().AsNoTracking().Where(o => !Closed.Contains(o.Stage));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLower();
            query = query.Where(o => o.Name.ToLower().Contains(term) || o.Number.ToLower().Contains(term)
                || (o.SolicitationNumber != null && o.SolicitationNumber.ToLower().Contains(term)));
        }

        var rows = await query.OrderBy(o => o.Number).Take(50).ToListAsync(cancellationToken);
        return await ToRefsAsync(db, rows, cancellationToken);
    }

    public async Task<OpportunityRefDto?> GetAsync(Guid opportunityId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<D.Opportunity>().AsNoTracking().FirstOrDefaultAsync(o => o.Id == opportunityId, cancellationToken);
        return row is null ? null : (await ToRefsAsync(db, [row], cancellationToken))[0];
    }

    private static async Task<IReadOnlyList<OpportunityRefDto>> ToRefsAsync(IWorkbenchDbContext db, IReadOnlyList<D.Opportunity> rows, CancellationToken ct)
    {
        var ids = rows.Where(o => o.AgencyAccountId != null).Select(o => o.AgencyAccountId!.Value).Distinct().ToList();
        var agencies = await db.Set<D.CrmAccount>().AsNoTracking().Where(a => ids.Contains(a.Id)).ToDictionaryAsync(a => a.Id, a => a.Name, ct);
        return rows.Select(o => new OpportunityRefDto(o.Id, $"{o.Number} {o.Name}",
            o.AgencyAccountId is { } a && agencies.TryGetValue(a, out var name) ? name : o.AgencyName)).ToList();
    }
}

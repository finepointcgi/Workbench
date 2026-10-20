using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>MRP demand from sales orders: the open quantity of released and partly shipped orders' lines, on their need dates.</summary>
internal static class SalesOrderDemand
{
    public static async Task<List<MrpDemand>> LoadAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var orders = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines)
            .Where(o => o.Status == SalesOrderStatus.Released || o.Status == SalesOrderStatus.PartlyShipped)
            .ToListAsync(ct);
        return orders.SelectMany(o => o.Lines.Where(l => l.QuantityOpen > 0)
                .Select(l => new MrpDemand(l.PartId, l.NeedDate, l.QuantityOpen, $"{o.Number} line {l.LineNumber}", o.ProjectId)))
            .ToList();
    }
}

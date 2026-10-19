using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

internal sealed record MrpPart(Guid Id, string Number, PartSource Source, int LeadTimeDays, decimal SafetyStock, decimal MinimumOrder, decimal Multiple, decimal OnHand, bool HasBom = true);

internal sealed record MrpComponent(Guid ParentId, Guid ComponentId, decimal QuantityPer, decimal ScrapPercent = 0, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null)
{
    public bool IsEffectiveOn(DateOnly d) => (EffectiveFrom is null || EffectiveFrom <= d) && (EffectiveTo is null || d <= EffectiveTo);

    public decimal RequiredFor(decimal parentQuantity) => Math.Round(parentQuantity * QuantityPer * (1 + ScrapPercent / 100m), 4, MidpointRounding.AwayFromZero);
}

internal sealed record MrpDemand(Guid PartId, DateOnly Date, decimal Quantity, string Source, Guid? ProjectId = null);

internal sealed record MrpSupply(Guid PartId, DateOnly Date, decimal Quantity, string Source);

internal sealed record MrpPlan(Guid PartId, PlannedOrderKind Kind, decimal Quantity, DateOnly NeedDate, DateOnly ReleaseDate, bool IsLate, string Pegging, Guid? ProjectId);

internal sealed record MrpResult(IReadOnlyList<MrpPlan> Plans, IReadOnlyList<string> Messages);

/// <summary>
/// Material requirements planning, pure: parts are planned in low-level-code order (a part after every assembly that
/// uses it), each part's dated demand is netted against on hand and scheduled receipts, and whenever projected stock
/// would fall below safety stock a planned order covers it (lot for lot, raised to the minimum and rounded to the
/// multiple), released lead-time days before it's needed. Planned make orders explode their BOM into dependent demand
/// for the components on the release date. Orders released before the as-of date are late (expedite).
/// </summary>
internal static class MrpEngine
{
    public static MrpResult Plan(
        IReadOnlyList<MrpPart> parts, IReadOnlyList<MrpComponent> components, IReadOnlyList<MrpDemand> demands, IReadOnlyList<MrpSupply> supplies,
        DateOnly asOf, int horizonDays)
    {
        var messages = new List<string>();
        var horizon = asOf.AddDays(horizonDays);
        var byId = parts.ToDictionary(p => p.Id);
        var children = components.ToLookup(c => c.ParentId);
        var levels = LowLevelCodes(parts, children, messages);

        var demand = demands.Where(d => d.Quantity > 0 && d.Date <= horizon && byId.ContainsKey(d.PartId))
            .GroupBy(d => d.PartId).ToDictionary(g => g.Key, g => g.ToList());
        var supply = supplies.Where(s => s.Quantity > 0).ToLookup(s => s.PartId);
        var plans = new List<MrpPlan>();

        foreach (var part in parts.OrderBy(p => levels.GetValueOrDefault(p.Id)).ThenBy(p => p.Number, StringComparer.Ordinal))
        {
            var partDemand = demand.GetValueOrDefault(part.Id) ?? [];
            if (partDemand.Count == 0 && part.SafetyStock <= part.OnHand + supply[part.Id].Sum(s => s.Quantity))
            {
                continue;
            }

            if (part.Source == PartSource.Make && !part.HasBom)
            {
                messages.Add($"{part.Number} is a make part without an active BOM: its planned orders have no component demand.");
            }

            var projected = part.OnHand;
            var receipts = supply[part.Id].OrderBy(s => s.Date).ToList();
            var r = 0;
            var pegged = new List<MrpDemand>();
            var firstPlan = true;
            var dates = partDemand.Select(d => d.Date < asOf ? asOf : d.Date).Append(asOf).Distinct().Order().ToList();
            foreach (var date in dates)
            {
                while (r < receipts.Count && receipts[r].Date <= date)
                {
                    projected += receipts[r++].Quantity;
                }

                foreach (var d in partDemand.Where(d => (d.Date < asOf ? asOf : d.Date) == date))
                {
                    projected -= d.Quantity;
                    pegged.Add(d);
                }

                if (projected >= part.SafetyStock)
                {
                    continue;
                }

                // Receipts scheduled later than this need: the planned order covers the need now (the receipt shows as excess later).
                var need = part.SafetyStock - projected;
                var quantity = LotSize(need, part.MinimumOrder, part.Multiple);
                var release = date.AddDays(-part.LeadTimeDays);
                var late = release < asOf;
                var projects = pegged.Select(p => p.ProjectId).Distinct().ToList();
                var pegging = Peg(pegged, firstPlan ? part.SafetyStock : 0);
                firstPlan = false;
                var plan = new MrpPlan(part.Id, part.Source == PartSource.Make ? PlannedOrderKind.Make : PlannedOrderKind.Buy, quantity, date, release, late, pegging,
                    projects.Count == 1 ? projects[0] : null);
                plans.Add(plan);
                if (late)
                {
                    messages.Add($"{part.Number}: release {quantity:0.####} by {release:yyyy-MM-dd} to have it by {date:yyyy-MM-dd} ({part.LeadTimeDays} days lead time) — expedite.");
                }

                projected += quantity;
                pegged.Clear();

                if (plan.Kind == PlannedOrderKind.Make)
                {
                    var on = release < asOf ? asOf : release;
                    foreach (var c in children[part.Id].Where(c => c.IsEffectiveOn(on)))
                    {
                        if (!byId.ContainsKey(c.ComponentId))
                        {
                            continue;
                        }

                        var list = demand.TryGetValue(c.ComponentId, out var l) ? l : demand[c.ComponentId] = [];
                        list.Add(new MrpDemand(c.ComponentId, on, c.RequiredFor(quantity), $"planned {part.Number} {quantity:0.####}", plan.ProjectId));
                    }
                }
            }

        }

        return new MrpResult(plans, messages);
    }

    public static decimal LotSize(decimal need, decimal minimum, decimal multiple)
    {
        var q = Math.Max(need, minimum);
        if (multiple > 0)
        {
            q = Math.Ceiling(q / multiple) * multiple;
        }

        return Math.Round(q, 4, MidpointRounding.AwayFromZero);
    }

    private static string Peg(IReadOnlyList<MrpDemand> pegged, decimal safety)
    {
        var parts = pegged.OrderBy(p => p.Date).Take(8).Select(p => $"{p.Source} {p.Quantity:0.####} ({p.Date:MMM d})").ToList();
        if (pegged.Count > 8)
        {
            parts.Add($"+{pegged.Count - 8} more");
        }

        if (safety > 0)
        {
            parts.Add($"safety stock {safety:0.####}");
        }

        var text = string.Join("; ", parts);
        return text.Length > 2000 ? text[..2000] : text;
    }

    /// <summary>A part's level: 0 for top assemblies, otherwise one more than the deepest parent that uses it.</summary>
    internal static Dictionary<Guid, int> LowLevelCodes(IReadOnlyList<MrpPart> parts, ILookup<Guid, MrpComponent> children, ICollection<string> messages)
    {
        var levels = parts.ToDictionary(p => p.Id, _ => 0);
        var reported = false;
        void Walk(Guid id, int level, HashSet<Guid> path)
        {
            foreach (var c in children[id])
            {
                if (!path.Add(c.ComponentId))
                {
                    if (!reported)
                    {
                        messages.Add("A BOM uses itself (directly or through a subassembly); that loop isn't planned.");
                        reported = true;
                    }

                    continue;
                }

                if (levels.GetValueOrDefault(c.ComponentId, -1) < level + 1)
                {
                    levels[c.ComponentId] = level + 1;
                }

                Walk(c.ComponentId, level + 1, path);
                path.Remove(c.ComponentId);
            }
        }

        foreach (var p in parts)
        {
            Walk(p.Id, levels[p.Id], [p.Id]);
        }

        return levels;
    }
}

/// <summary>Database reads MRP and the part page share.</summary>
internal static class MrpData
{
    internal sealed record PurchaseSupply(Guid PartId, decimal Quantity, DateOnly Date, string Document);

    /// <summary>
    /// The part a purchase order line is for: its own link, or the link of the requisition line it was ordered from.
    /// </summary>
    public static async Task<Dictionary<Guid, MaterialPurchaseLink>> LinksByPoLineAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid>? poLineIds, CancellationToken ct)
    {
        var links = await db.Set<MaterialPurchaseLink>().AsNoTracking().ToListAsync(ct);
        var byReqLine = links.Where(l => l.RequisitionLineId != null).GroupBy(l => l.RequisitionLineId!.Value).ToDictionary(g => g.Key, g => g.First());
        var result = links.Where(l => l.PurchaseOrderLineId != null).GroupBy(l => l.PurchaseOrderLineId!.Value).ToDictionary(g => g.Key, g => g.First());
        var lines = db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => l.RequisitionLineId != null);
        if (poLineIds is not null)
        {
            var ids = poLineIds.ToList();
            lines = lines.Where(l => ids.Contains(l.Id));
        }

        foreach (var l in await lines.Select(l => new { l.Id, l.RequisitionLineId }).ToListAsync(ct))
        {
            if (!result.ContainsKey(l.Id) && byReqLine.TryGetValue(l.RequisitionLineId!.Value, out var link))
            {
                result[l.Id] = link;
            }
        }

        return poLineIds is null ? result : result.Where(kv => poLineIds.Contains(kv.Key)).ToDictionary();
    }

    /// <summary>Open quantities on part-linked purchase order lines (issued orders) and not-yet-ordered requisition lines.</summary>
    public static async Task<List<PurchaseSupply>> OpenPurchaseSupplyAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var result = new List<PurchaseSupply>();
        if (!await db.Set<MaterialPurchaseLink>().AnyAsync(ct))
        {
            return result;
        }

        var links = await LinksByPoLineAsync(db, null, ct);
        var poLines = await db.Set<PurchaseOrderLine>().AsNoTracking()
            .Join(db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived || o.Status == PurchaseOrderStatus.Draft),
                l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, l.Quantity, l.QuantityReceived, l.NeededBy, o.OrderDate, o.Number })
            .ToListAsync(ct);
        foreach (var l in poLines.Where(l => links.ContainsKey(l.Id) && l.Quantity > l.QuantityReceived))
        {
            result.Add(new PurchaseSupply(links[l.Id].PartId, l.Quantity - l.QuantityReceived, l.NeededBy ?? l.OrderDate, l.Number));
        }

        var reqLinks = await db.Set<MaterialPurchaseLink>().AsNoTracking().Where(k => k.RequisitionLineId != null).ToListAsync(ct);
        var reqLineIds = reqLinks.Select(k => k.RequisitionLineId!.Value).ToList();
        var reqLines = await db.Set<PurchaseRequisitionLine>().AsNoTracking().Where(l => reqLineIds.Contains(l.Id) && l.PurchaseOrderLineId == null)
            .Join(db.Set<PurchaseRequisition>().AsNoTracking().Where(r => r.Status == RequisitionStatus.Draft || r.Status == RequisitionStatus.PendingApproval || r.Status == RequisitionStatus.Approved),
                l => l.RequisitionId, r => r.Id, (l, r) => new { l.Id, l.Quantity, l.NeededBy, r.Number, r.CreatedAtUtc })
            .ToListAsync(ct);
        foreach (var l in reqLines)
        {
            var link = reqLinks.First(k => k.RequisitionLineId == l.Id);
            result.Add(new PurchaseSupply(link.PartId, l.Quantity, l.NeededBy ?? DateOnly.FromDateTime(l.CreatedAtUtc), l.Number));
        }

        return result;
    }
}

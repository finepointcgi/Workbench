using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

internal static class MaterialMapping
{
    public static PartSourceKind ToContract(this PartSource v) => (PartSourceKind)(int)v;
    public static PartSource ToDomain(this PartSourceKind v) => Enum.IsDefined(v) ? (PartSource)(int)v : throw new ArgumentException("Unknown part source.");
    public static PartCostMethod ToContract(this CostMethod v) => (PartCostMethod)(int)v;
    public static CostMethod ToDomain(this PartCostMethod v) => Enum.IsDefined(v) ? (CostMethod)(int)v : throw new ArgumentException("Unknown cost method.");
    public static PartTracking ToContract(this TrackingMode v) => (PartTracking)(int)v;
    public static TrackingMode ToDomain(this PartTracking v) => Enum.IsDefined(v) ? (TrackingMode)(int)v : throw new ArgumentException("Unknown tracking mode.");
    public static StockOwnership ToContract(this InventoryOwnership v) => (StockOwnership)(int)v;
    public static InventoryOwnership ToDomain(this StockOwnership v) => Enum.IsDefined(v) ? (InventoryOwnership)(int)v : throw new ArgumentException("Unknown ownership.");
    public static InventoryMovement ToContract(this InventoryTransactionType v) => (InventoryMovement)(int)v;
    public static InventoryTransactionType ToDomain(this InventoryMovement v) => (InventoryTransactionType)(int)v;
    public static WorkOrderState ToContract(this WorkOrderStatus v) => (WorkOrderState)(int)v;
    public static WorkOrderStatus ToDomain(this WorkOrderState v) => (WorkOrderStatus)(int)v;
    public static PlannedOrderType ToContract(this PlannedOrderKind v) => (PlannedOrderType)(int)v;
    public static PlannedOrderState ToContract(this PlannedOrderStatus v) => (PlannedOrderState)(int)v;
}

/// <summary>Reads and DTO mapping shared by the materials services.</summary>
internal static class MaterialQueries
{
    public static async Task<IReadOnlyList<PartSummaryDto>> PartSummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Part> parts, CancellationToken ct)
    {
        var ids = parts.Select(p => p.Id).ToList();
        var onHand = await db.Set<InventoryBalance>().AsNoTracking().Where(b => ids.Contains(b.PartId))
            .GroupBy(b => new { b.PartId, Gfm = b.Ownership == InventoryOwnership.GovernmentFurnished })
            .Select(g => new { g.Key.PartId, g.Key.Gfm, Qty = g.Sum(b => b.Quantity) }).ToListAsync(ct);
        var vendorIds = parts.Where(p => p.PreferredVendorId != null).Select(p => p.PreferredVendorId!.Value).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, ct);
        var boms = (await db.Set<BillOfMaterial>().AsNoTracking().Where(b => b.IsActive && ids.Contains(b.PartId)).Select(b => b.PartId).ToListAsync(ct)).ToHashSet();
        return parts.Select(p => new PartSummaryDto(
            p.Id, p.Number, p.Revision, p.Description, p.UnitOfMeasure, p.Source.ToContract(), p.Tracking.ToContract(), p.CostMethod.ToContract(),
            p.StandardCost, p.AverageCost,
            onHand.Where(o => o.PartId == p.Id && !o.Gfm).Sum(o => o.Qty),
            onHand.Where(o => o.PartId == p.Id && o.Gfm).Sum(o => o.Qty),
            p.InventoryValue,
            p.PreferredVendorId is { } v ? vendors.GetValueOrDefault(v) : null,
            p.LeadTimeDays, boms.Contains(p.Id), p.IsActive)).ToList();
    }

    public static async Task<IReadOnlyList<InventoryBalanceDto>> BalancesAsync(IWorkbenchDbContext db, IReadOnlyList<InventoryBalance> balances, CancellationToken ct)
    {
        var partIds = balances.Select(b => b.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = balances.Select(b => b.BinId).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var projectIds = balances.Where(b => b.OwnerProjectId != null).Select(b => b.OwnerProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        return balances
            .Select(b =>
            {
                var p = parts[b.PartId];
                var unit = b.Ownership == InventoryOwnership.GovernmentFurnished ? 0 : p.CurrentUnitCost;
                return new InventoryBalanceDto(b.Id, p.Id, p.Number, p.Revision, p.Description, p.UnitOfMeasure, b.WarehouseId, warehouses.GetValueOrDefault(b.WarehouseId) ?? "",
                    b.BinId, bins.GetValueOrDefault(b.BinId) ?? "", b.Lot, b.Serial, b.Ownership.ToContract(), b.OwnerProjectId,
                    b.OwnerProjectId is { } op ? projects.GetValueOrDefault(op) : null, b.Quantity, unit, Money.Round(b.Quantity * unit), b.ReceivedOn, b.GovernmentReference,
                    (StockStatus)(int)b.Status);
            })
            .OrderBy(b => b.PartNumber, StringComparer.Ordinal).ThenBy(b => b.Warehouse, StringComparer.Ordinal).ThenBy(b => b.Bin, StringComparer.Ordinal)
            .ThenBy(b => b.Lot, StringComparer.Ordinal).ThenBy(b => b.Serial, StringComparer.Ordinal)
            .ToList();
    }

    public static async Task<IReadOnlyList<InventoryTransactionDto>> TransactionsAsync(IWorkbenchDbContext db, IReadOnlyList<InventoryTransaction> rows, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var partIds = rows.Select(r => r.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = rows.Where(r => r.BinId != null).Select(r => r.BinId!.Value).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var projectIds = rows.SelectMany(r => new[] { r.ProjectId, r.OwnerProjectId }).OfType<Guid>().Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var wbsIds = rows.Where(r => r.WbsElementId != null).Select(r => r.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var woIds = rows.Where(r => r.WorkOrderId != null).Select(r => r.WorkOrderId!.Value).Distinct().ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Number, ct);
        var lineIds = rows.Where(r => r.PurchaseOrderLineId != null).Select(r => r.PurchaseOrderLineId!.Value).Distinct().ToList();
        var poNumbers = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>().AsNoTracking(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, o.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var jeIds = rows.Where(r => r.JournalEntryId != null).Select(r => r.JournalEntryId!.Value).Distinct().ToList();
        var jes = await db.Set<JournalEntry>().AsNoTracking().Where(e => jeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.Number, ct);
        return rows.Select(t =>
        {
            var p = parts[t.PartId];
            var location = t.WarehouseId is { } w ? $"{warehouses.GetValueOrDefault(w)} / {(t.BinId is { } b ? bins.GetValueOrDefault(b) : "")}" : null;
            return new InventoryTransactionDto(t.Id, t.Number, t.Type.ToContract(), t.Date, p.Id, p.Display, p.Description, location, t.Lot, t.Serial,
                t.Ownership.ToContract(), t.OwnerProjectId is { } op ? projects.GetValueOrDefault(op) : null, t.Quantity, t.UnitCost, t.Amount, t.Variance,
                t.ProjectId is { } pid ? projects.GetValueOrDefault(pid) : null, t.WbsElementId is { } wid ? wbs.GetValueOrDefault(wid) : null,
                t.WorkOrderId, t.WorkOrderId is { } woid ? wos.GetValueOrDefault(woid) : null,
                t.PurchaseOrderLineId is { } lid ? poNumbers.GetValueOrDefault(lid) : null, t.Reason, t.Reference,
                t.JournalEntryId is { } je ? jes.GetValueOrDefault(je) : null, t.CreatedBy, t.CreatedAtUtc);
        }).ToList();
    }

    /// <summary>Multi-level where-used: every active or inactive BOM revision that uses the part, then their parents.</summary>
    public static async Task<IReadOnlyList<WhereUsedDto>> WhereUsedAsync(IWorkbenchDbContext db, Guid partId, CancellationToken ct)
    {
        var components = await db.Set<BomComponent>().AsNoTracking().ToListAsync(ct);
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var result = new List<WhereUsedDto>();
        var seen = new HashSet<Guid>();
        void Walk(Guid id, int level)
        {
            foreach (var c in components.Where(c => c.ComponentPartId == id).OrderBy(c => parts[boms[c.BomId].PartId].Number, StringComparer.Ordinal))
            {
                var bom = boms[c.BomId];
                var parent = parts[bom.PartId];
                result.Add(new WhereUsedDto(level, parent.Id, parent.Number, parent.Revision, parent.Description, bom.Revision, bom.IsActive, c.QuantityPer));
                if (bom.IsActive && level < 20 && seen.Add(parent.Id))
                {
                    Walk(parent.Id, level + 1);
                }
            }
        }

        Walk(partId, 1);
        return result;
    }
}

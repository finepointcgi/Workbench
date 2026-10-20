using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

/// <summary>Bins stock can't be picked from: staged kits (reserved) and physical inventories being counted (frozen).</summary>
internal static class ShopFloorLocks
{
    public static async Task LoadAsync(StockEngine engine, CancellationToken ct)
    {
        var db = engine.Db;
        foreach (var bin in await db.Set<Kit>().AsNoTracking().Where(k => k.Status == KitStatus.Open || k.Status == KitStatus.Staged).Select(k => k.StagingBinId).Distinct().ToListAsync(ct))
        {
            engine.ReservedBins.Add(bin);
        }

        var counting = await db.Set<PhysicalInventory>().AsNoTracking().Where(p => p.Status == PhysicalInventoryStatus.Counting).ToListAsync(ct);
        foreach (var pi in counting)
        {
            foreach (var bin in await FrozenBinsAsync(db, pi, ct))
            {
                engine.FrozenBins.TryAdd(bin, pi.Number);
            }
        }
    }

    /// <summary>Bins nothing may be picked from now: open kits' staging bins and frozen bins.</summary>
    public static async Task<HashSet<Guid>> LockedBinsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var locked = (await db.Set<Kit>().AsNoTracking().Where(k => k.Status == KitStatus.Open || k.Status == KitStatus.Staged).Select(k => k.StagingBinId).ToListAsync(ct)).ToHashSet();
        foreach (var pi in await db.Set<PhysicalInventory>().AsNoTracking().Where(p => p.Status == PhysicalInventoryStatus.Counting).ToListAsync(ct))
        {
            locked.UnionWith(await FrozenBinsAsync(db, pi, ct));
        }

        return locked;
    }

    /// <summary>Stock of each part that could be picked for <paramref name="project"/> (general, plus the project's own), outside locked bins.</summary>
    public static async Task<Dictionary<Guid, decimal>> AvailableAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> partIds, Guid? project, CancellationToken ct)
    {
        var locked = await LockedBinsAsync(db, ct);
        var ids = partIds.ToList();
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => ids.Contains(b.PartId) && b.Quantity > 0).ToListAsync(ct);
        return ids.Distinct().ToDictionary(id => id, id => balances.Where(b => b.PartId == id && !locked.Contains(b.BinId) && StockEngine.UsableFor(b, project)).Sum(b => b.Quantity));
    }

    /// <summary>The bins a physical inventory froze.</summary>
    public static async Task<List<Guid>> FrozenBinsAsync(IWorkbenchDbContext db, PhysicalInventory pi, CancellationToken ct) =>
        pi.WholeWarehouse
            ? await db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == pi.WarehouseId).Select(b => b.Id).ToListAsync(ct)
            : await db.Set<PhysicalInventoryBin>().AsNoTracking().Where(b => b.PhysicalInventoryId == pi.Id).Select(b => b.BinId).ToListAsync(ct);
}

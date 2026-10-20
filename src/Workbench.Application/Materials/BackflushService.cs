using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;

namespace Workbench.Application.Materials;

internal static class ShopFloorMapping
{
    public static KitState ToContract(this KitStatus v) => (KitState)(int)v;
    public static BackflushWhen ToContract(this BackflushTrigger v) => (BackflushWhen)(int)v;
    public static LandedChargeType ToContract(this LandedChargeKind v) => (LandedChargeType)(int)v;
    public static LandedChargeKind ToDomain(this LandedChargeType v) => Enum.IsDefined(v) ? (LandedChargeKind)(int)v : throw new ArgumentException("Unknown charge type.");
    public static LandedCostBasis ToContract(this LandedCostMethod v) => (LandedCostBasis)(int)v;
    public static LandedCostMethod ToDomain(this LandedCostBasis v) => Enum.IsDefined(v) ? (LandedCostMethod)(int)v : throw new ArgumentException("Unknown allocation basis.");
    public static LandedChargeState ToContract(this LandedChargeStatus v) => (LandedChargeState)(int)v;
    public static PhysicalInventoryState ToContract(this PhysicalInventoryStatus v) => (PhysicalInventoryState)(int)v;
    public static CountTagState ToContract(this CountTagStatus v) => (CountTagState)(int)v;
    public static ScheduleMode ToContract(this ScheduleDirection v) => (ScheduleMode)(int)v;
    public static ScheduleDirection ToDomain(this ScheduleMode v) => Enum.IsDefined(v) ? (ScheduleDirection)(int)v : throw new ArgumentException("Unknown scheduling direction.");

    /// <summary>"MAIN / A-01" for a bin, keyed by bin id.</summary>
    public static async Task<Dictionary<Guid, string>> BinLabelsAsync(IWorkbenchDbContext db, CancellationToken ct) =>
        await db.Set<Bin>().AsNoTracking().Join(db.Set<Warehouse>().AsNoTracking(), b => b.WarehouseId, w => w.Id, (b, w) => new { b.Id, Label = w.Code + " / " + b.Code })
            .ToDictionaryAsync(x => x.Id, x => x.Label, ct);
}

/// <summary>Backflush settings per part or BOM revision, the resolved view of each BOM line, and shortages.</summary>
public sealed class BackflushService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, TimeProvider clock) : IBackflushService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<BackflushSettingDto>> ListSettingsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SettingsAsync(db, await db.Set<BackflushSetting>().AsNoTracking().ToListAsync(cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<BackflushSettingDto>> SettingsAsync(IWorkbenchDbContext db, IReadOnlyList<BackflushSetting> settings, CancellationToken ct)
    {
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Id, ct);
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().ToDictionaryAsync(b => b.Id, ct);
        var bins = await ShopFloorMapping.BinLabelsAsync(db, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        return settings
            .Select(s =>
            {
                var p = parts[s.PartId];
                var bom = s.BomId is { } b ? boms.GetValueOrDefault(b) : null;
                return new BackflushSettingDto(s.Id, p.Id, p.Display, p.Description, s.BomId, bom is null ? null : parts[bom.PartId].Display, bom?.Revision, s.IsBackflushed,
                    s.WarehouseId, s.BinId, Location(s, bins, warehouses));
            })
            .OrderBy(s => s.PartNumber, StringComparer.Ordinal).ThenBy(s => s.ParentPartNumber ?? "", StringComparer.Ordinal).ToList();
    }

    private static string? Location(BackflushSetting s, IReadOnlyDictionary<Guid, string> bins, IReadOnlyDictionary<Guid, string> warehouses) =>
        s.BinId is { } bin ? bins.GetValueOrDefault(bin) : s.WarehouseId is { } wh ? $"{warehouses.GetValueOrDefault(wh)} (any bin)" : null;

    public async Task<IReadOnlyList<BackflushComponentDto>> ListComponentsAsync(Guid? bomId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var boms = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).Where(b => bomId == null ? b.IsActive : b.Id == bomId).ToListAsync(cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().ToDictionaryAsync(p => p.Id, cancellationToken);
        var settings = await db.Set<BackflushSetting>().AsNoTracking().ToListAsync(cancellationToken);
        var bins = await ShopFloorMapping.BinLabelsAsync(db, cancellationToken);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, cancellationToken);
        return boms.OrderBy(b => parts[b.PartId].Number, StringComparer.Ordinal).ThenBy(b => b.Revision, StringComparer.Ordinal)
            .SelectMany(b => b.Components.OrderBy(c => c.Sequence).Select(c =>
            {
                var own = settings.FirstOrDefault(s => s.PartId == c.ComponentPartId && s.BomId == b.Id);
                var setting = own ?? settings.FirstOrDefault(s => s.PartId == c.ComponentPartId && s.BomId == null);
                var p = parts[c.ComponentPartId];
                var source = own is not null ? "This BOM" : setting is not null ? "Part default" : "Not set (kitted)";
                return new BackflushComponentDto(b.Id, b.PartId, parts[b.PartId].Display, b.Revision, b.IsActive, p.Id, p.Display, p.Description, c.OperationSequence,
                    setting?.IsBackflushed == true, source, setting is null ? null : Location(setting, bins, warehouses));
            }))
            .ToList();
    }

    public async Task<BackflushSettingDto> SaveSettingAsync(SaveBackflushSettingRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        if (request.BomId is { } bomId)
        {
            var bom = await db.Set<BillOfMaterial>().AsNoTracking().Include(b => b.Components).FirstOrDefaultAsync(b => b.Id == bomId, cancellationToken)
                ?? throw WorkbenchNotFoundException.For("BOM", bomId);
            if (bom.Components.All(c => c.ComponentPartId != part.Id))
            {
                throw new ArgumentException($"{part.Display} isn't a component of that BOM.");
            }
        }

        Guid? warehouseId = request.WarehouseId;
        if (request.BinId is { } binId)
        {
            var bin = await db.Set<Bin>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Bin", binId);
            if (warehouseId is { } w && w != bin.WarehouseId)
            {
                throw new ArgumentException($"Bin {bin.Code} isn't in that warehouse.");
            }

            warehouseId = bin.WarehouseId;
        }
        else if (warehouseId is { } w && !await db.Set<Warehouse>().AnyAsync(x => x.Id == w, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Warehouse", w);
        }

        var setting = await db.Set<BackflushSetting>().FirstOrDefaultAsync(s => s.PartId == part.Id && s.BomId == request.BomId, cancellationToken);
        var before = setting is null ? null : new { setting.IsBackflushed, setting.WarehouseId, setting.BinId };
        if (setting is null)
        {
            setting = new BackflushSetting { PartId = part.Id, BomId = request.BomId, UpdatedBy = audit.Actor };
            db.Set<BackflushSetting>().Add(setting);
        }

        setting.IsBackflushed = request.IsBackflushed;
        setting.WarehouseId = warehouseId;
        setting.BinId = request.BinId;
        setting.UpdatedBy = audit.Actor;
        setting.UpdatedAtUtc = UtcNow;
        audit.Record(db, MaterialRules.PartEntity, part.Id, "BackflushChanged", before, new { setting.IsBackflushed, setting.BomId, setting.WarehouseId, setting.BinId });
        await db.SaveChangesAsync(cancellationToken);
        return (await SettingsAsync(db, [setting], cancellationToken))[0];
    }

    public async Task DeleteSettingAsync(Guid settingId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var setting = await db.Set<BackflushSetting>().FirstOrDefaultAsync(s => s.Id == settingId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Backflush setting", settingId);
        audit.Record(db, MaterialRules.PartEntity, setting.PartId, "BackflushRemoved", new { setting.IsBackflushed, setting.BomId }, null);
        db.Set<BackflushSetting>().Remove(setting);
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<BackflushShortageDto>> ListShortagesAsync(Guid? workOrderId = null, bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<BackflushShortage>().AsNoTracking();
        if (workOrderId is { } wo)
        {
            q = q.Where(s => s.WorkOrderId == wo);
        }

        if (openOnly)
        {
            q = q.Where(s => s.ResolvedAtUtc == null);
        }

        return await ShortagesAsync(db, await q.ToListAsync(cancellationToken), cancellationToken);
    }

    internal static async Task<IReadOnlyList<BackflushShortageDto>> ShortagesAsync(IWorkbenchDbContext db, IReadOnlyList<BackflushShortage> rows, CancellationToken ct)
    {
        var woIds = rows.Select(r => r.WorkOrderId).Distinct().ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Number, ct);
        var partIds = rows.Select(r => r.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        return rows.OrderByDescending(r => r.UpdatedAtUtc)
            .Select(r => new BackflushShortageDto(r.Id, r.WorkOrderId, wos.GetValueOrDefault(r.WorkOrderId) ?? "", r.PartId, parts[r.PartId].Display, parts[r.PartId].Description,
                r.QuantityShort, r.Trigger.ToContract(), r.Date, r.Message, r.ResolvedAtUtc is null))
            .ToList();
    }

    public async Task<BackflushResultDto> RetryAsync(Guid workOrderId, DateOnly date, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await db.Set<WorkOrder>().Include(w => w.Components).Include(w => w.Operations).FirstOrDefaultAsync(w => w.Id == workOrderId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Work order", workOrderId);
        if (wo.Status is not (WorkOrderStatus.Released or WorkOrderStatus.InProgress or WorkOrderStatus.Completed))
        {
            throw new InvalidOperationException($"{wo.Number} is {wo.Status}; only open or completed (not closed) work orders can be backflushed.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var result = await Backflush.ApplyAsync(engine, wo, wo.QuantityCompleted, date, cancellationToken);
        if (result.Issued.Count > 0)
        {
            await engine.FlushAsync(JournalSource.Manufacturing, result.Issued[0].Id, date, $"Backflush on {wo.Number} ({result.Issued[0].Number})", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return new BackflushResultDto(await MaterialQueries.TransactionsAsync(db, result.Issued, cancellationToken), await ShortagesAsync(db, result.Shortages, cancellationToken));
    }
}

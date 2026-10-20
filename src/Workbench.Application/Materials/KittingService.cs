using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>
/// Kits: a work order's open components (or a project's open material requirements) staged in a kit bin by transfers,
/// then issued in one step. Staged stock is reserved (normal picks skip open kits' bins); cancelling puts it back.
/// </summary>
public sealed class KittingService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, TimeProvider clock) : IKittingService
{
    private const string KitEntity = nameof(Kit);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<KitSummaryDto>> ListAsync(KitQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Kit>().AsNoTracking().Include(k => k.Lines).AsQueryable();
        if (query.OpenOnly)
        {
            q = q.Where(k => k.Status == KitStatus.Open || k.Status == KitStatus.Staged);
        }

        if (query.WorkOrderId is { } wo)
        {
            q = q.Where(k => k.WorkOrderId == wo);
        }

        if (query.ProjectId is { } p)
        {
            q = q.Where(k => k.ProjectId == p);
        }

        return await SummariesAsync(db, await q.ToListAsync(cancellationToken), cancellationToken);
    }

    private static async Task<IReadOnlyList<KitSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Kit> kits, CancellationToken ct)
    {
        var woIds = kits.Where(k => k.WorkOrderId != null).Select(k => k.WorkOrderId!.Value).Distinct().ToList();
        var wos = await db.Set<WorkOrder>().AsNoTracking().Where(w => woIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, ct);
        var projectIds = kits.Select(k => k.ProjectId).Concat(wos.Values.Select(w => w.ProjectId)).OfType<Guid>().Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var wbsIds = kits.Where(k => k.WbsElementId != null).Select(k => k.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var bins = await ShopFloorMapping.BinLabelsAsync(db, ct);
        return kits.OrderByDescending(k => k.Number, StringComparer.Ordinal)
            .Select(k =>
            {
                var wo = k.WorkOrderId is { } id ? wos.GetValueOrDefault(id) : null;
                var project = k.ProjectId ?? wo?.ProjectId;
                var open = k.IsOpen;
                return new KitSummaryDto(k.Id, k.Number, k.Status.ToContract(), k.WorkOrderId, wo?.Number, project, project is { } p ? projects.GetValueOrDefault(p) : null,
                    k.WbsElementId is { } w ? wbs.GetValueOrDefault(w) : null, bins.GetValueOrDefault(k.StagingBinId) ?? "", k.NeededBy, k.Lines.Count,
                    open ? k.Lines.Sum(l => l.QuantityShort) : 0, open ? k.Lines.Count(l => l.QuantityShort > 0) : 0, k.CreatedAtUtc);
            }).ToList();
    }

    public async Task<KitDto> GetAsync(Guid kitId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, kitId, cancellationToken);
    }

    private static async Task<KitDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var kit = await db.Set<Kit>().AsNoTracking().Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Kit", id);
        var summary = (await SummariesAsync(db, [kit], ct))[0];
        var partIds = kit.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var available = kit.IsOpen ? await ShopFloorLocks.AvailableAsync(db, partIds, summary.ProjectId, ct) : [];
        var wbsIds = kit.Lines.Where(l => l.WbsElementId != null).Select(l => l.WbsElementId!.Value).Distinct().ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => wbsIds.Contains(w.Id)).ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var picks = await db.Set<KitPick>().AsNoTracking().Where(p => p.KitId == id).ToListAsync(ct);
        var bins = await ShopFloorMapping.BinLabelsAsync(db, ct);
        var lineParts = kit.Lines.ToDictionary(l => l.Id, l => parts[l.PartId]);
        return new KitDto(summary, kit.StagingWarehouseId, kit.StagingBinId, kit.Notes,
            kit.Lines.OrderBy(l => l.Sequence).Select(l =>
            {
                var p = parts[l.PartId];
                return new KitLineDto(l.Id, l.Sequence, p.Id, p.Display, p.Description, p.UnitOfMeasure, p.Tracking.ToContract(), l.WbsElementId is { } w ? wbs.GetValueOrDefault(w) : null,
                    l.QuantityRequired, l.QuantityStaged, l.QuantityIssued, kit.IsOpen ? l.QuantityShort : 0, available.GetValueOrDefault(p.Id));
            }).ToList(),
            picks.OrderBy(p => lineParts[p.KitLineId].Number, StringComparer.Ordinal).ThenBy(p => p.Lot, StringComparer.Ordinal).ThenBy(p => p.Serial, StringComparer.Ordinal)
                .Select(p => new KitPickDto(p.Id, p.KitLineId, lineParts[p.KitLineId].Display, bins.GetValueOrDefault(p.FromBinId) ?? "", p.Lot, p.Serial, p.Ownership.ToContract(),
                    p.Quantity, p.IssueTransactionId is not null)).ToList());
    }

    public async Task<KitDto> CreateForWorkOrderAsync(CreateWorkOrderKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wo = await db.Set<WorkOrder>().AsNoTracking().Include(w => w.Components).FirstOrDefaultAsync(w => w.Id == request.WorkOrderId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Work order", request.WorkOrderId);
        if (!wo.IsOpen)
        {
            throw new InvalidOperationException(wo.Status == WorkOrderStatus.Planned ? $"Release {wo.Number} before kitting it." : $"{wo.Number} is {wo.Status}.");
        }

        var existing = await db.Set<Kit>().AsNoTracking().FirstOrDefaultAsync(k => k.WorkOrderId == wo.Id && (k.Status == KitStatus.Open || k.Status == KitStatus.Staged), cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException($"{wo.Number} already has open kit {existing.Number}; issue or cancel it first.");
        }

        var backflushed = request.IncludeBackflushed ? [] : await BackflushedPartsAsync(db, wo, cancellationToken);
        var components = wo.Components.Where(c => c.QuantityOpen > 0 && !backflushed.Contains(c.PartId)).OrderBy(c => c.Sequence).ToList();
        if (components.Count == 0)
        {
            throw new ArgumentException(wo.Components.Any(c => c.QuantityOpen > 0)
                ? $"Every open component of {wo.Number} is backflushed; it's issued when work is reported."
                : $"Every component of {wo.Number} is already issued.");
        }

        var (warehouseId, binId) = await StagingAsync(db, request.StagingBinId, cancellationToken);
        var kit = await NewKitAsync(db, warehouseId, binId, request.NeededBy ?? wo.StartDate, request.Notes, cancellationToken);
        kit.WorkOrderId = wo.Id;
        var seq = 0;
        foreach (var c in components)
        {
            kit.Lines.Add(new KitLine { KitId = kit.Id, Sequence = seq += 10, PartId = c.PartId, WorkOrderComponentId = c.Id, QuantityRequired = c.QuantityOpen });
        }

        audit.Record(db, KitEntity, kit.Id, "Created", after: new { kit.Number, WorkOrder = wo.Number, Lines = kit.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, kit.Id, cancellationToken);
    }

    /// <summary>Components of the work order that are backflushed (BOM setting first, then the part's).</summary>
    private static async Task<HashSet<Guid>> BackflushedPartsAsync(IWorkbenchDbContext db, WorkOrder wo, CancellationToken ct)
    {
        var partIds = wo.Components.Select(c => c.PartId).Distinct().ToList();
        var settings = await db.Set<BackflushSetting>().AsNoTracking().Where(s => partIds.Contains(s.PartId) && (s.BomId == null || s.BomId == wo.BomId)).ToListAsync(ct);
        return partIds.Where(id => (settings.FirstOrDefault(s => s.PartId == id && s.BomId != null) ?? settings.FirstOrDefault(s => s.PartId == id && s.BomId == null))?.IsBackflushed == true)
            .ToHashSet();
    }

    public async Task<KitDto> CreateForProjectAsync(CreateProjectKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.ProjectId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Project", request.ProjectId);
        if (request.WbsElementId is { } wid && !await db.Set<WbsElement>().AnyAsync(w => w.Id == wid && w.ProjectId == project.Id, cancellationToken))
        {
            throw new ArgumentException("That WBS element isn't on the project.");
        }

        var kitted = await db.Set<KitLine>().AsNoTracking().Where(l => l.RequirementId != null)
            .Join(db.Set<Kit>().AsNoTracking().Where(k => k.Status == KitStatus.Open || k.Status == KitStatus.Staged), l => l.KitId, k => k.Id, (l, k) => l.RequirementId!.Value)
            .ToListAsync(cancellationToken);
        var requirements = (await db.Set<ProjectMaterialRequirement>().AsNoTracking()
                .Where(r => r.ProjectId == project.Id && !r.IsClosed && (request.WbsElementId == null || r.WbsElementId == request.WbsElementId)).ToListAsync(cancellationToken))
            .Where(r => r.OpenQuantity > 0 && !kitted.Contains(r.Id)).OrderBy(r => r.NeededBy).ToList();
        if (requirements.Count == 0)
        {
            throw new ArgumentException($"Project {project.Code} has no open material requirements that aren't already on a kit.");
        }

        var (warehouseId, binId) = await StagingAsync(db, request.StagingBinId, cancellationToken);
        var kit = await NewKitAsync(db, warehouseId, binId, request.NeededBy ?? requirements.Min(r => r.NeededBy), request.Notes, cancellationToken);
        kit.ProjectId = project.Id;
        kit.WbsElementId = request.WbsElementId;
        var seq = 0;
        foreach (var r in requirements)
        {
            kit.Lines.Add(new KitLine
            {
                KitId = kit.Id, Sequence = seq += 10, PartId = r.PartId, RequirementId = r.Id, WbsElementId = r.WbsElementId, QuantityRequired = r.OpenQuantity,
            });
        }

        audit.Record(db, KitEntity, kit.Id, "Created", after: new { kit.Number, Project = project.Code, Lines = kit.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, kit.Id, cancellationToken);
    }

    private static async Task<(Guid WarehouseId, Guid BinId)> StagingAsync(IWorkbenchDbContext db, Guid binId, CancellationToken ct)
    {
        var bin = await db.Set<Bin>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == binId, ct) ?? throw WorkbenchNotFoundException.For("Bin", binId);
        var warehouse = await db.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Id == bin.WarehouseId, ct);
        if (!bin.IsActive || !warehouse.IsActive)
        {
            throw new ArgumentException($"{warehouse.Code} / {bin.Code} is inactive.");
        }

        return (warehouse.Id, bin.Id);
    }

    private async Task<Kit> NewKitAsync(IWorkbenchDbContext db, Guid warehouseId, Guid binId, DateOnly neededBy, string? notes, CancellationToken ct)
    {
        var last = await db.Set<Kit>().OrderByDescending(k => k.Number).Select(k => k.Number).FirstOrDefaultAsync(ct);
        var next = last is not null && last.StartsWith("KIT-", StringComparison.Ordinal) && int.TryParse(last[4..], out var n) ? n + 1 : 1;
        var kit = new Kit
        {
            Number = $"KIT-{next:D6}",
            StagingWarehouseId = warehouseId,
            StagingBinId = binId,
            NeededBy = neededBy,
            Notes = PurchasingSupport.Optional(notes, "Notes", 1000),
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        db.Set<Kit>().Add(kit);
        return kit;
    }

    private static async Task<Kit> LoadOpenAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var kit = await db.Set<Kit>().Include(k => k.Lines).FirstOrDefaultAsync(k => k.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Kit", id);
        if (!kit.IsOpen)
        {
            throw new InvalidOperationException($"{kit.Number} is {kit.Status}.");
        }

        return kit;
    }

    private static async Task<Guid?> DestinationProjectAsync(IWorkbenchDbContext db, Kit kit, CancellationToken ct) =>
        kit.ProjectId ?? (kit.WorkOrderId is { } wo ? await db.Set<WorkOrder>().AsNoTracking().Where(w => w.Id == wo).Select(w => w.ProjectId).FirstAsync(ct) : null);

    public async Task<KitDto> StageAsync(Guid kitId, StageKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var kit = await LoadOpenAsync(db, kitId, cancellationToken);
        var project = await DestinationProjectAsync(db, kit, cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var explicitLines = request.Lines is { Count: > 0 };
        var work = explicitLines
            ? request.Lines!.Where(l => l.Quantity != 0).Select(l => (Line: kit.Lines.FirstOrDefault(x => x.Id == l.KitLineId) ?? throw new ArgumentException("That line isn't on this kit."),
                Quantity: (decimal?)MaterialRules.Quantity(l.Quantity, "Quantity"), l.BalanceId)).ToList()
            : kit.Lines.Where(l => l.QuantityShort > 0).OrderBy(l => l.Sequence).Select(l => (Line: l, Quantity: (decimal?)null, BalanceId: (Guid?)null)).ToList();
        var transfers = new List<InventoryTransaction>();
        foreach (var (line, quantity, balanceId) in work)
        {
            var part = await engine.LoadPartAsync(line.PartId, cancellationToken);
            var wanted = quantity ?? line.QuantityShort;
            if (wanted > line.QuantityShort)
            {
                throw new ArgumentException($"Only {line.QuantityShort:0.####} {part.UnitOfMeasure} of {part.Display} is still to stage.");
            }

            if (balanceId is { } bid && (engine.ReservedBins.Contains(engine.Db.Set<InventoryBalance>().Local.FirstOrDefault(b => b.Id == bid)?.BinId ?? Guid.Empty)))
            {
                throw new ArgumentException("That stock is already staged in a kit.");
            }

            var take = balanceId is null ? Math.Min(wanted, engine.Available(part.Id, project)) : wanted;
            if (part.Tracking == TrackingMode.Serial)
            {
                take = Math.Floor(take);
            }

            if (take <= 0)
            {
                if (explicitLines)
                {
                    throw new ArgumentException($"No {part.Display} is available to stage.");
                }

                continue; // Stays short.
            }

            foreach (var (balance, qty) in engine.Pick(part, take, project, balanceId))
            {
                var from = (balance.WarehouseId, balance.BinId, balance.Lot, balance.Serial, balance.Ownership, balance.OwnerProjectId);
                var (_, i) = engine.Transfer(part, balance, qty, kit.StagingWarehouseId, kit.StagingBinId, request.Date, $"Staged for {kit.Number}");
                transfers.Add(i);
                db.Set<KitPick>().Add(new KitPick
                {
                    KitId = kit.Id, KitLineId = line.Id, FromWarehouseId = from.WarehouseId, FromBinId = from.BinId, Lot = from.Lot, Serial = from.Serial,
                    Ownership = from.Ownership, OwnerProjectId = from.OwnerProjectId, Quantity = qty, TransferInTransactionId = i.Id,
                });
            }

            line.QuantityStaged += take;
        }

        if (transfers.Count == 0)
        {
            throw new ArgumentException("Nothing could be staged: the parts still short aren't in stock outside other kits and frozen bins.");
        }

        kit.Status = kit.Lines.All(l => l.QuantityShort == 0) ? KitStatus.Staged : KitStatus.Open;
        await engine.FlushAsync(JournalSource.Inventory, transfers[0].Id, request.Date, "", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, kit.Id, cancellationToken);
    }

    public async Task<KitDto> IssueAsync(Guid kitId, IssueKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var kit = await LoadOpenAsync(db, kitId, cancellationToken);
        var picks = await db.Set<KitPick>().Where(p => p.KitId == kit.Id && p.IssueTransactionId == null).ToListAsync(cancellationToken);
        if (picks.Count == 0)
        {
            throw new ArgumentException($"Nothing is staged on {kit.Number}; stage it first.");
        }

        WorkOrder? wo = null;
        if (kit.WorkOrderId is { } woId)
        {
            wo = await db.Set<WorkOrder>().Include(w => w.Components).FirstAsync(w => w.Id == woId, cancellationToken);
            if (!wo.IsOpen)
            {
                throw new InvalidOperationException($"{wo.Number} is {wo.Status}; cancel the kit to put the stock back.");
            }
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var issued = new List<InventoryTransaction>();
        var lines = kit.Lines.ToDictionary(l => l.Id);
        var wbsCache = new Dictionary<Guid, WbsElement>();
        foreach (var pick in picks)
        {
            var line = lines[pick.KitLineId];
            var part = await engine.LoadPartAsync(line.PartId, cancellationToken);
            var balance = engine.Balances(part.Id).FirstOrDefault(b => b.BinId == kit.StagingBinId && b.Lot == pick.Lot && b.Serial == pick.Serial
                && b.Ownership == pick.Ownership && b.OwnerProjectId == pick.OwnerProjectId && b.Status == InventoryStockStatus.Available);
            if (balance is null || balance.Quantity < pick.Quantity)
            {
                throw new ArgumentException($"{part.Display}{(pick.Lot.Length > 0 ? $" lot {pick.Lot}" : "")}{(pick.Serial.Length > 0 ? $" serial {pick.Serial}" : "")} isn't all in the kit bin any more; re-stage the kit.");
            }

            InventoryTransaction t;
            if (wo is not null)
            {
                var debit = wo.IsForProject ? engine.Accounts?.DirectMaterial : engine.Accounts?.Wip;
                t = engine.Issue(part, balance, pick.Quantity, InventoryTransactionType.IssueToWorkOrder, request.Date, debit, $"kit {kit.Number} issued to {wo.Number}", wo.ProjectId, wo.WbsElementId);
                t.WorkOrderId = wo.Id;
                wo.MaterialCost += -t.Amount;
                if (wo.Components.FirstOrDefault(c => c.Id == line.WorkOrderComponentId) is { } component)
                {
                    component.QuantityIssued += pick.Quantity;
                }
            }
            else
            {
                var wbsId = line.WbsElementId ?? throw new InvalidOperationException("A project kit line has no WBS element.");
                if (!wbsCache.TryGetValue(wbsId, out var wbs))
                {
                    wbsCache[wbsId] = wbs = await StockEngine.ChargeableWbsAsync(db, wbsId, direct: true, cancellationToken);
                }

                StockEngine.EnsureUsableFor(balance, wbs.ProjectId);
                t = engine.Issue(part, balance, pick.Quantity, InventoryTransactionType.IssueToProject, request.Date, engine.Accounts?.DirectMaterial, $"kit {kit.Number} issued to {wbs.Code}", wbs.ProjectId, wbs.Id);
                await InventoryService.ConsumeRequirementsAsync(db, wbs.Id, part.Id, pick.Quantity, cancellationToken);
            }

            t.Reason = $"Kit {kit.Number}";
            pick.IssueTransactionId = t.Id;
            line.QuantityStaged -= pick.Quantity;
            line.QuantityIssued += pick.Quantity;
            issued.Add(t);
        }

        if (wo is not null)
        {
            wo.Status = WorkOrderStatus.InProgress;
        }

        var shortLines = kit.Lines.Count(l => l.QuantityShort > 0);
        kit.Status = KitStatus.Issued;
        kit.IssuedAtUtc = UtcNow;
        audit.Record(db, KitEntity, kit.Id, "Issued", after: new { Issued = issued.Count, ShortLines = shortLines });
        await engine.FlushAsync(wo is null ? JournalSource.Inventory : JournalSource.Manufacturing, issued[0].Id, request.Date,
            $"Kit {kit.Number} issued to {wo?.Number ?? "the project"} ({issued[0].Number})", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, kit.Id, cancellationToken);
    }

    public async Task<KitDto> CancelAsync(Guid kitId, CancelKitRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = PurchasingSupport.Required(request.Reason, "A reason", 500);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var kit = await LoadOpenAsync(db, kitId, cancellationToken);
        var picks = await db.Set<KitPick>().Where(p => p.KitId == kit.Id && p.IssueTransactionId == null).ToListAsync(cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var lines = kit.Lines.ToDictionary(l => l.Id);
        InventoryTransaction? first = null;
        foreach (var pick in picks)
        {
            var part = await engine.LoadPartAsync(lines[pick.KitLineId].PartId, cancellationToken);
            var balance = engine.Balances(part.Id).FirstOrDefault(b => b.BinId == kit.StagingBinId && b.Lot == pick.Lot && b.Serial == pick.Serial
                && b.Ownership == pick.Ownership && b.OwnerProjectId == pick.OwnerProjectId && b.Status == InventoryStockStatus.Available);
            var qty = Math.Min(pick.Quantity, balance?.Quantity ?? 0);
            if (balance is not null && qty > 0)
            {
                var (o, _) = engine.Transfer(part, balance, qty, pick.FromWarehouseId, pick.FromBinId, request.Date, $"Returned from cancelled {kit.Number}");
                first ??= o;
                pick.IssueTransactionId = o.Id;
            }

            lines[pick.KitLineId].QuantityStaged -= pick.Quantity;
        }

        kit.Status = KitStatus.Cancelled;
        audit.Record(db, KitEntity, kit.Id, "Cancelled", after: new { Returned = picks.Count }, reason: reason);
        if (first is not null)
        {
            await engine.FlushAsync(JournalSource.Inventory, first.Id, request.Date, "", cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, kit.Id, cancellationToken);
    }
}

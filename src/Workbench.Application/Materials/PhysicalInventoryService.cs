using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>
/// Physical inventory: freeze a warehouse or some bins (count tags snapshot every balance; the stock engine refuses
/// movements in or out of frozen bins), count by tag, recount what's off by more than the thresholds, then post every
/// difference through the cycle count posting (<see cref="InventoryService.CountDifference"/>) in one journal entry.
/// </summary>
public sealed class PhysicalInventoryService(IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, AuditRecorder audit, TimeProvider clock) : IPhysicalInventoryService
{
    private const string Entity = nameof(PhysicalInventory);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<PhysicalInventorySummaryDto>> ListAsync(bool openOnly = false, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var list = await db.Set<PhysicalInventory>().AsNoTracking().Where(p => !openOnly || p.Status == PhysicalInventoryStatus.Counting).ToListAsync(cancellationToken);
        var ids = list.Select(p => p.Id).ToList();
        var tags = await db.Set<CountTag>().AsNoTracking().Where(t => ids.Contains(t.PhysicalInventoryId)).ToListAsync(cancellationToken);
        var result = new List<PhysicalInventorySummaryDto>();
        foreach (var pi in list.OrderByDescending(p => p.Number, StringComparer.Ordinal))
        {
            result.Add(await SummaryAsync(db, pi, tags.Where(t => t.PhysicalInventoryId == pi.Id).ToList(), cancellationToken));
        }

        return result;
    }

    private static async Task<PhysicalInventorySummaryDto> SummaryAsync(IWorkbenchDbContext db, PhysicalInventory pi, IReadOnlyList<CountTag> tags, CancellationToken ct)
    {
        var warehouse = await db.Set<Warehouse>().AsNoTracking().Where(w => w.Id == pi.WarehouseId).Select(w => w.Code).FirstOrDefaultAsync(ct) ?? "";
        var binCount = pi.WholeWarehouse ? 0 : await db.Set<PhysicalInventoryBin>().CountAsync(b => b.PhysicalInventoryId == pi.Id, ct);
        var scope = pi.WholeWarehouse ? "Whole warehouse" : binCount == 1 ? "1 bin" : $"{binCount} bins";
        var live = tags.Where(t => t.Status != CountTagStatus.Void).ToList();
        return new PhysicalInventorySummaryDto(pi.Id, pi.Number, pi.WarehouseId, warehouse, scope, pi.Status.ToContract(), pi.CountDate, live.Count,
            live.Count(t => t.FinalQuantity is not null), live.Count(t => t.Status == CountTagStatus.RecountNeeded),
            live.Where(t => t.FinalQuantity is not null).Sum(t => Money.Round((t.FinalQuantity!.Value - t.FrozenQuantity) * t.UnitCost)), pi.FrozenAtUtc, pi.CreatedBy);
    }

    public async Task<PhysicalInventoryDto> GetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, physicalInventoryId, cancellationToken);
    }

    private static async Task<PhysicalInventoryDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var pi = await db.Set<PhysicalInventory>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Physical inventory", id);
        var tags = await db.Set<CountTag>().AsNoTracking().Where(t => t.PhysicalInventoryId == id).OrderBy(t => t.TagNumber).ToListAsync(ct);
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == pi.WarehouseId).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var frozen = await ShopFloorLocks.FrozenBinsAsync(db, pi, ct);
        var partIds = tags.Select(t => t.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var projectIds = tags.Where(t => t.OwnerProjectId != null).Select(t => t.OwnerProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        var je = pi.JournalEntryId is { } j ? await db.Set<JournalEntry>().AsNoTracking().Where(e => e.Id == j).Select(e => e.Number).FirstOrDefaultAsync(ct) : null;
        return new PhysicalInventoryDto(await SummaryAsync(db, pi, tags, ct),
            frozen.Select(b => bins.GetValueOrDefault(b) ?? "").OrderBy(b => b, StringComparer.Ordinal).ToList(), pi.RecountPercent, pi.RecountValue, pi.BlindCount, pi.Notes,
            tags.Select(t => ToDto(t, parts[t.PartId], bins.GetValueOrDefault(t.BinId) ?? "", t.OwnerProjectId is { } op ? projects.GetValueOrDefault(op) : null)).ToList(), je);
    }

    private static CountTagDto ToDto(CountTag t, Part p, string bin, string? ownerProject)
    {
        var final = t.FinalQuantity;
        var variance = final is { } f ? f - t.FrozenQuantity : (decimal?)null;
        return new CountTagDto(t.Id, t.TagNumber, t.BinId, bin, p.Id, p.Display, p.Description, p.UnitOfMeasure, p.Tracking.ToContract(), t.Lot, t.Serial, t.Ownership.ToContract(),
            ownerProject, t.FrozenQuantity, t.UnitCost, t.CountedQuantity, t.RecountQuantity, final, variance, variance is { } v ? Money.Round(v * t.UnitCost) : null,
            t.Status.ToContract(), t.IsBlankTag, t.RecountedBy ?? t.CountedBy, t.Note);
    }

    public async Task<PhysicalInventoryDto> CreateAsync(CreatePhysicalInventoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RecountPercent < 0 || request.RecountValue < 0)
        {
            throw new ArgumentException("Recount thresholds can't be negative.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var warehouse = await db.Set<Warehouse>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == request.WarehouseId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Warehouse", request.WarehouseId);
        if (!warehouse.IsActive)
        {
            throw new ArgumentException($"{warehouse.Code} is inactive.");
        }

        var all = await db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == warehouse.Id).ToListAsync(cancellationToken);
        var wanted = (request.BinIds ?? []).Distinct().ToList();
        if (wanted.Any(id => all.All(b => b.Id != id)))
        {
            throw new ArgumentException($"Every bin must be in {warehouse.Code}.");
        }

        var whole = wanted.Count == 0 || wanted.Count == all.Count;
        var bins = whole ? all.Select(b => b.Id).ToList() : wanted;
        var locked = (await db.Set<PhysicalInventory>().AsNoTracking().Where(p => p.Status == PhysicalInventoryStatus.Counting).ToListAsync(cancellationToken));
        foreach (var other in locked)
        {
            if ((await ShopFloorLocks.FrozenBinsAsync(db, other, cancellationToken)).Intersect(bins).Any())
            {
                throw new ArgumentException($"Some of those bins are already frozen for {other.Number}.");
            }
        }

        var last = await db.Set<PhysicalInventory>().OrderByDescending(p => p.Number).Select(p => p.Number).FirstOrDefaultAsync(cancellationToken);
        var next = last is not null && last.StartsWith("PI-", StringComparison.Ordinal) && int.TryParse(last[3..], out var n) ? n + 1 : 1;
        var pi = new PhysicalInventory
        {
            Number = $"PI-{next:D6}",
            WarehouseId = warehouse.Id,
            WholeWarehouse = whole,
            CountDate = request.CountDate,
            RecountPercent = request.RecountPercent,
            RecountValue = request.RecountValue,
            BlindCount = request.BlindCount,
            Notes = PurchasingSupport.Optional(request.Notes, "Notes", 1000),
            CreatedBy = audit.Actor,
            FrozenAtUtc = UtcNow,
        };
        db.Set<PhysicalInventory>().Add(pi);
        if (!whole)
        {
            db.Set<PhysicalInventoryBin>().AddRange(bins.Select(b => new PhysicalInventoryBin { PhysicalInventoryId = pi.Id, BinId = b }));
        }

        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => bins.Contains(b.BinId) && b.Quantity > 0).ToListAsync(cancellationToken);
        var partIds = balances.Select(b => b.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var binCodes = all.ToDictionary(b => b.Id, b => b.Code);
        var tag = 0;
        foreach (var b in balances.OrderBy(b => binCodes[b.BinId], StringComparer.Ordinal).ThenBy(b => parts[b.PartId].Number, StringComparer.Ordinal)
                     .ThenBy(b => b.Lot, StringComparer.Ordinal).ThenBy(b => b.Serial, StringComparer.Ordinal).ThenBy(b => b.Ownership))
        {
            db.Set<CountTag>().Add(new CountTag
            {
                PhysicalInventoryId = pi.Id, TagNumber = ++tag, BinId = b.BinId, PartId = b.PartId, Lot = b.Lot, Serial = b.Serial, Ownership = b.Ownership,
                OwnerProjectId = b.OwnerProjectId, FrozenQuantity = b.Quantity,
                UnitCost = b.Ownership == InventoryOwnership.GovernmentFurnished ? 0 : parts[b.PartId].CurrentUnitCost,
            });
        }

        audit.Record(db, Entity, pi.Id, "Frozen", after: new { pi.Number, Warehouse = warehouse.Code, Bins = bins.Count, Tags = tag });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, pi.Id, cancellationToken);
    }

    private static async Task<PhysicalInventory> LoadCountingAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var pi = await db.Set<PhysicalInventory>().FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Physical inventory", id);
        if (!pi.IsFrozen)
        {
            throw new InvalidOperationException($"{pi.Number} is {pi.Status}.");
        }

        return pi;
    }

    public async Task<CountResultDto> EnterCountAsync(Guid physicalInventoryId, EnterCountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await LoadCountingAsync(db, physicalInventoryId, cancellationToken);
        var tag = await db.Set<CountTag>().FirstOrDefaultAsync(t => t.PhysicalInventoryId == pi.Id && t.TagNumber == request.TagNumber, cancellationToken)
            ?? throw new ArgumentException($"There's no tag {request.TagNumber} on {pi.Number}.");
        if (tag.Status == CountTagStatus.Void)
        {
            throw new ArgumentException($"Tag {tag.TagNumber} is void.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == tag.PartId, cancellationToken);
        var result = Count(pi, tag, part, request.Quantity, PurchasingSupport.Optional(request.Note, "The note", 500));
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    /// <summary>Records a count or recount and decides whether it needs a recount (off by more than either threshold).</summary>
    private CountResultDto Count(PhysicalInventory pi, CountTag tag, Part part, decimal quantity, string? note)
    {
        MaterialRules.Quantity(quantity, "The count", allowZero: true);
        if (part.Tracking == TrackingMode.Serial && quantity is not (0 or 1))
        {
            throw new ArgumentException("A serial number counts 0 or 1.");
        }

        if (note is not null)
        {
            tag.Note = note;
        }

        if (tag.Status is CountTagStatus.RecountNeeded or CountTagStatus.Recounted)
        {
            tag.RecountQuantity = quantity;
            tag.RecountedBy = audit.Actor;
            tag.RecountedAtUtc = UtcNow;
            tag.Status = CountTagStatus.Recounted;
            return new CountResultDto(tag.TagNumber, tag.Status.ToContract(), $"Tag {tag.TagNumber} recounted.");
        }

        tag.CountedQuantity = quantity;
        tag.CountedBy = audit.Actor;
        tag.CountedAtUtc = UtcNow;
        var variance = Math.Abs(quantity - tag.FrozenQuantity);
        var recount = variance != 0
            && ((pi.RecountPercent > 0 && (tag.FrozenQuantity == 0 || variance / tag.FrozenQuantity * 100m > pi.RecountPercent))
                || (pi.RecountValue > 0 && variance * tag.UnitCost > pi.RecountValue));
        tag.Status = recount ? CountTagStatus.RecountNeeded : CountTagStatus.Counted;
        return new CountResultDto(tag.TagNumber, tag.Status.ToContract(),
            recount ? $"Tag {tag.TagNumber} needs a recount. Count it again and enter the new count." : $"Tag {tag.TagNumber} counted.");
    }

    public async Task<CountResultDto> AddTagAsync(Guid physicalInventoryId, AddCountTagRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await LoadCountingAsync(db, physicalInventoryId, cancellationToken);
        if (!(await ShopFloorLocks.FrozenBinsAsync(db, pi, cancellationToken)).Contains(request.BinId))
        {
            throw new ArgumentException($"That bin isn't part of {pi.Number}.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        var (lot, serial) = StockEngine.Tracking(part, request.Lot, request.Serial);
        var ownership = request.Ownership.ToDomain();
        if (ownership != InventoryOwnership.General && (request.OwnerProjectId is not { } op || !await db.Set<Project>().AnyAsync(p => p.Id == op, cancellationToken)))
        {
            throw new ArgumentException("Contract and government-furnished stock belong to a project; choose it.");
        }

        var owner = ownership == InventoryOwnership.General ? null : request.OwnerProjectId;
        var tags = await db.Set<CountTag>().Where(t => t.PhysicalInventoryId == pi.Id).ToListAsync(cancellationToken);
        if (tags.FirstOrDefault(t => t.Status != CountTagStatus.Void && t.BinId == request.BinId && t.PartId == part.Id && t.Lot == lot && t.Serial == serial
                && t.Ownership == ownership && t.OwnerProjectId == owner) is { } existing)
        {
            throw new ArgumentException($"Tag {existing.TagNumber} already covers that stock; enter the count on it.");
        }

        if (serial.Length > 0 && await db.Set<InventoryBalance>().AnyAsync(b => b.PartId == part.Id && b.Serial == serial && b.Quantity > 0 && b.BinId != request.BinId, cancellationToken))
        {
            throw new ArgumentException($"Serial {serial} of {part.Display} is on record in another bin; count it there (enter 0 on its tag if it isn't there).");
        }

        var tag = new CountTag
        {
            PhysicalInventoryId = pi.Id, TagNumber = tags.Select(t => t.TagNumber).DefaultIfEmpty(0).Max() + 1, BinId = request.BinId, PartId = part.Id, Lot = lot, Serial = serial,
            Ownership = ownership, OwnerProjectId = owner, IsBlankTag = true,
            UnitCost = ownership == InventoryOwnership.GovernmentFurnished ? 0 : part.CurrentUnitCost,
        };
        db.Set<CountTag>().Add(tag);
        var result = Count(pi, tag, part, request.Quantity, PurchasingSupport.Optional(request.Note, "The note", 500));
        await db.SaveChangesAsync(cancellationToken);
        return result;
    }

    public async Task<PhysicalInventoryDto> VoidTagAsync(Guid physicalInventoryId, int tagNumber, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await LoadCountingAsync(db, physicalInventoryId, cancellationToken);
        var tag = await db.Set<CountTag>().FirstOrDefaultAsync(t => t.PhysicalInventoryId == pi.Id && t.TagNumber == tagNumber, cancellationToken)
            ?? throw new ArgumentException($"There's no tag {tagNumber} on {pi.Number}.");
        if (!tag.IsBlankTag)
        {
            throw new ArgumentException($"Tag {tagNumber} is stock on record; enter 0 if it isn't there.");
        }

        tag.Status = CountTagStatus.Void;
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, pi.Id, cancellationToken);
    }

    public async Task<CountSheetFileDto> ExportCountSheetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await DetailAsync(db, physicalInventoryId, cancellationToken);
        var header = new List<object?> { "Tag", "Warehouse", "Bin", "Part", "Description", "Unit", "Lot", "Serial", "Owner" };
        if (!pi.BlindCount)
        {
            header.Add("On record");
        }

        header.AddRange(["Count", "Counted by"]);
        var rows = new List<IEnumerable<object?>> { header };
        foreach (var t in pi.Tags.Where(t => t.Status != CountTagState.Void))
        {
            var row = new List<object?>
            {
                t.TagNumber.ToString(CultureInfo.InvariantCulture), pi.Summary.Warehouse, t.Bin, t.PartNumber, t.PartDescription, t.UnitOfMeasure, t.Lot, t.Serial,
                t.Ownership switch { StockOwnership.GovernmentFurnished => $"GFM {t.OwnerProjectCode}", StockOwnership.Contract => $"Contract {t.OwnerProjectCode}", _ => "General" },
            };
            if (!pi.BlindCount)
            {
                row.Add(t.FrozenQuantity.ToString("0.####", CultureInfo.InvariantCulture));
            }

            row.AddRange([t.FinalQuantity?.ToString("0.####", CultureInfo.InvariantCulture) ?? "", ""]);
            rows.Add(row);
        }

        return new CountSheetFileDto($"{pi.Summary.Number}-count-sheet.csv", "text/csv", PurchasingSupport.Csv(rows));
    }

    public async Task<PhysicalInventoryDto> PostAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await LoadCountingAsync(db, physicalInventoryId, cancellationToken);
        var tags = await db.Set<CountTag>().Where(t => t.PhysicalInventoryId == pi.Id && t.Status != CountTagStatus.Void).OrderBy(t => t.TagNumber).ToListAsync(cancellationToken);
        var uncounted = tags.Count(t => t.FinalQuantity is null);
        var recounts = tags.Count(t => t.Status == CountTagStatus.RecountNeeded);
        if (uncounted > 0 || recounts > 0)
        {
            throw new ArgumentException(string.Join(" ", new[]
            {
                uncounted > 0 ? $"{uncounted} tag{(uncounted == 1 ? " isn't" : "s aren't")} counted yet." : null,
                recounts > 0 ? $"{recounts} tag{(recounts == 1 ? " needs" : "s need")} a recount." : null,
            }.OfType<string>()) + " Enter 0 for stock that isn't there.");
        }

        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        engine.PostingPhysicalInventory = pi.Number;
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => b.WarehouseId == pi.WarehouseId).ToDictionaryAsync(b => b.Id, cancellationToken);
        var adjusted = 0;
        foreach (var tag in tags)
        {
            var part = await engine.LoadPartAsync(tag.PartId, cancellationToken);
            var balance = engine.Balances(part.Id).FirstOrDefault(b => b.BinId == tag.BinId && b.Lot == tag.Lot && b.Serial == tag.Serial && b.Ownership == tag.Ownership
                && b.OwnerProjectId == tag.OwnerProjectId);
            var diff = tag.FinalQuantity!.Value - (balance?.Quantity ?? 0);
            if (diff == 0)
            {
                continue;
            }

            var key = new StockKey(part.Id, pi.WarehouseId, tag.BinId, tag.Lot, tag.Serial, tag.Ownership, tag.OwnerProjectId);
            var t = InventoryService.CountDifference(engine, part, key, balance, diff, InventoryTransactionType.PhysicalCount, pi.CountDate,
                $"Physical inventory {pi.Number} tag {tag.TagNumber} ({bins.GetValueOrDefault(tag.BinId)?.Code})");
            t.Reference = pi.Number;
            tag.AdjustmentTransactionId = t.Id;
            adjusted++;
        }

        pi.JournalEntryId = (await engine.FlushAsync(JournalSource.Inventory, pi.Id, pi.CountDate, $"Physical inventory {pi.Number} adjustments", cancellationToken))?.Id;
        pi.Status = PhysicalInventoryStatus.Posted;
        pi.PostedBy = audit.Actor;
        pi.PostedAtUtc = UtcNow;
        audit.Record(db, Entity, pi.Id, "Posted", new { Status = nameof(PhysicalInventoryStatus.Counting) }, new { Status = pi.Status.ToString(), Tags = tags.Count, Adjusted = adjusted });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, pi.Id, cancellationToken);
    }

    public async Task<PhysicalInventoryDto> CancelAsync(Guid physicalInventoryId, string reason, CancellationToken cancellationToken = default)
    {
        var why = PurchasingSupport.Required(reason, "A reason", 500);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pi = await LoadCountingAsync(db, physicalInventoryId, cancellationToken);
        pi.Status = PhysicalInventoryStatus.Cancelled;
        audit.Record(db, Entity, pi.Id, "Cancelled", new { Status = nameof(PhysicalInventoryStatus.Counting) }, new { Status = pi.Status.ToString() }, why);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, pi.Id, cancellationToken);
    }
}

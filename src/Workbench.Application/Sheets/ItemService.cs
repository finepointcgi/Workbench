using Microsoft.EntityFrameworkCore;
using Workbench.Application.Formulas;
using Workbench.Application.Scheduling;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

namespace Workbench.Application.Sheets;

public sealed class ItemService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IItemService
{
    public async Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (!await db.Sheets.AnyAsync(s => s.Id == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Sheet", sheetId);
        }

        var items = await db.Items
            .AsNoTracking()
            .Where(i => i.SheetId == sheetId)
            .OrderBy(i => i.ParentId)
            .ThenBy(i => i.Order)
            .ToListAsync(cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        var computed = FormulaCalculator.Calculate(columns, items, Today());
        return items.Select(i => WithComputed(i.ToDto(), computed)).ToList();
    }

    public async Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ValidateDates(request.Start, request.Finish);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);
        await EnsureParentAsync(db, sheetId, request.ParentId, cancellationToken);

        var siblings = await db.Items
            .Where(i => i.SheetId == sheetId && i.ParentId == request.ParentId)
            .OrderBy(i => i.Order)
            .ToListAsync(cancellationToken);
        var order = Math.Clamp(request.Order ?? siblings.Count, 0, siblings.Count);
        foreach (var sibling in siblings.Where(s => s.Order >= order))
        {
            sibling.Order++;
        }

        var item = new Item
        {
            SheetId = sheetId,
            ParentId = request.ParentId,
            Order = order,
            Title = request.Title.Trim(),
            Start = request.Start,
            Finish = request.Finish,
            Values = await StoredValuesAsync(db, sheetId, request.Values, cancellationToken),
            IsMilestone = request.IsMilestone,
        };
        PinMilestoneDate(item);
        db.Items.Add(item);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken);
        return await WithComputedAsync(db, sheetId, item, cancellationToken);
    }

    public async Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        ValidateDates(request.Start, request.Finish);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var item = await FindItemAsync(db, sheetId, itemId, cancellationToken);

        item.Title = request.Title.Trim();
        item.Start = request.Start;
        item.Finish = request.Finish;
        item.IsMilestone = request.IsMilestone ?? item.IsMilestone;
        PinMilestoneDate(item);
        item.CostpointWbsId = string.IsNullOrWhiteSpace(request.CostpointWbsId) ? null : request.CostpointWbsId.Trim();
        item.Values = await StoredValuesAsync(db, sheetId, request.Values, cancellationToken);
        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken);
        return await WithComputedAsync(db, sheetId, item, cancellationToken);
    }

    public async Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var items = await db.Items.Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var item = items.FirstOrDefault(i => i.Id == itemId) ?? throw WorkbenchNotFoundException.For("Item", itemId);

        if (request.ParentId is { } parentId)
        {
            if (items.All(i => i.Id != parentId))
            {
                throw WorkbenchNotFoundException.For("Item", parentId);
            }

            // Walk up from the new parent; reaching the item means the move would create a cycle.
            for (Guid? cursor = parentId; cursor is not null; cursor = items.First(i => i.Id == cursor).ParentId)
            {
                if (cursor == itemId)
                {
                    throw new ArgumentException("An item can't be moved under itself or one of its descendants.", nameof(request));
                }
            }
        }

        Renumber(items.Where(i => i.ParentId == item.ParentId && i.Id != itemId));

        var siblings = items.Where(i => i.ParentId == request.ParentId && i.Id != itemId).OrderBy(i => i.Order).ToList();
        var order = Math.Clamp(request.Order, 0, siblings.Count);
        siblings.Insert(order, item);
        item.ParentId = request.ParentId;

        // Number by list position: the moved item still carries its old order, so re-sorting would misplace it.
        for (var i = 0; i < siblings.Count; i++)
        {
            siblings[i].Order = i;
        }

        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken);
        return await WithComputedAsync(db, sheetId, item, cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var items = await db.Items.Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        var item = items.FirstOrDefault(i => i.Id == itemId) ?? throw WorkbenchNotFoundException.For("Item", itemId);

        var doomed = new HashSet<Guid> { itemId };
        bool added;
        do
        {
            added = false;
            foreach (var child in items.Where(i => i.ParentId is { } p && doomed.Contains(p) && !doomed.Contains(i.Id)))
            {
                doomed.Add(child.Id);
                added = true;
            }
        }
        while (added);

        db.Items.RemoveRange(items.Where(i => doomed.Contains(i.Id)));
        Renumber(items.Where(i => i.ParentId == item.ParentId && !doomed.Contains(i.Id)));
        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await PlanReflow.ReflowAndSaveAsync(db, sheetId, cancellationToken);
    }

    /// <summary>Copies request values, dropping formula columns: their values are computed on read, never stored.</summary>
    private static async Task<Dictionary<string, string?>> StoredValuesAsync(
        IWorkbenchDbContext db, Guid sheetId, IReadOnlyDictionary<string, string?>? values, CancellationToken cancellationToken)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var formulaColumnIds = await db.Columns
            .Where(c => c.SheetId == sheetId && c.Formula != null)
            .Select(c => c.Id)
            .ToListAsync(cancellationToken);
        return new Dictionary<string, string?>(
            values.Where(kv => !(Guid.TryParse(kv.Key, out var id) && formulaColumnIds.Contains(id))));
    }

    /// <summary>The item's DTO with formula columns calculated against the rest of the sheet.</summary>
    private async Task<ItemDto> WithComputedAsync(IWorkbenchDbContext db, Guid sheetId, Item item, CancellationToken cancellationToken)
    {
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        if (columns.All(c => c.Formula is null))
        {
            return item.ToDto();
        }

        var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        return WithComputed(item.ToDto(), FormulaCalculator.Calculate(columns, items, Today()));
    }

    private static ItemDto WithComputed(ItemDto dto, IReadOnlyDictionary<Guid, IReadOnlyDictionary<Guid, string?>> computed)
    {
        if (!computed.TryGetValue(dto.Id, out var row) || row.Count == 0)
        {
            return dto;
        }

        var values = new Dictionary<string, string?>(dto.Values);
        foreach (var (columnId, value) in row)
        {
            values[columnId.ToString()] = value;
        }

        return dto with { Values = values };
    }

    private DateOnly Today() => DateOnly.FromDateTime(clock.GetLocalNow().DateTime);

    private static void Renumber(IEnumerable<Item> siblings)
    {
        var order = 0;
        foreach (var sibling in siblings.OrderBy(s => s.Order).ToList())
        {
            sibling.Order = order++;
        }
    }

    /// <summary>A milestone is one day: its finish (or its start when it has no finish) is both dates.</summary>
    private static void PinMilestoneDate(Item item)
    {
        if (item.IsMilestone && (item.Finish ?? item.Start) is { } day)
        {
            item.Start = day;
            item.Finish = day;
        }
    }

    private static void ValidateDates(DateOnly? start, DateOnly? finish)
    {
        if (start is { } s && finish is { } f && f < s)
        {
            throw new ArgumentException("Finish can't be before start.");
        }
    }

    private static async Task EnsureParentAsync(IWorkbenchDbContext db, Guid sheetId, Guid? parentId, CancellationToken cancellationToken)
    {
        if (parentId is { } id && !await db.Items.AnyAsync(i => i.Id == id && i.SheetId == sheetId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Item", id);
        }
    }

    private static async Task<Item> FindItemAsync(IWorkbenchDbContext db, Guid sheetId, Guid itemId, CancellationToken cancellationToken) =>
        await db.Items.FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken)
        ?? throw WorkbenchNotFoundException.For("Item", itemId);

    private async Task TouchSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, cancellationToken);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

/// <summary>Inventory movements, inquiry and lot/serial traceability.</summary>
public sealed class InventoryService(
    IWorkbenchDbContextFactory dbFactory,
    MaterialsLedger ledger,
    AuditRecorder audit,
    IPurchaseOrderService purchaseOrders,
    InventoryReceiptContext receiptContext) : IInventoryService
{
    public async Task<IReadOnlyList<InventoryBalanceDto>> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<InventoryBalance>().AsNoTracking().Where(b => b.Quantity > 0);
        if (query.PartId is { } pid)
        {
            q = q.Where(b => b.PartId == pid);
        }

        if (query.WarehouseId is { } wid)
        {
            q = q.Where(b => b.WarehouseId == wid);
        }

        if (query.Ownership is { } o)
        {
            var own = o.ToDomain();
            q = q.Where(b => b.Ownership == own);
        }

        if (query.ProjectId is { } project)
        {
            q = q.Where(b => b.OwnerProjectId == project);
        }

        var rows = await MaterialQueries.BalancesAsync(db, await q.ToListAsync(cancellationToken), cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var t = query.Search.Trim();
            rows = rows.Where(r => r.PartNumber.Contains(t, StringComparison.OrdinalIgnoreCase) || r.PartDescription.Contains(t, StringComparison.OrdinalIgnoreCase)
                || r.Lot.Contains(t, StringComparison.OrdinalIgnoreCase) || r.Serial.Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows;
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> ListTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<InventoryTransaction>().AsNoTracking();
        if (query.PartId is { } pid)
        {
            q = q.Where(t => t.PartId == pid);
        }

        if (query.WorkOrderId is { } wo)
        {
            q = q.Where(t => t.WorkOrderId == wo);
        }

        if (query.ProjectId is { } project)
        {
            q = q.Where(t => t.ProjectId == project || t.OwnerProjectId == project);
        }

        if (query.Type is { } type)
        {
            var tt = type.ToDomain();
            q = q.Where(t => t.Type == tt);
        }

        if (query.From is { } from)
        {
            q = q.Where(t => t.Date >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(t => t.Date <= to);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var s = query.Search.Trim().ToUpperInvariant();
            q = q.Where(t => t.Number.Contains(s) || t.Lot.Contains(s) || t.Serial.Contains(s));
        }

        var rows = await q.OrderByDescending(t => t.Number).Take(Math.Clamp(query.Take, 1, 2000)).ToListAsync(cancellationToken);
        return await MaterialQueries.TransactionsAsync(db, rows, cancellationToken);
    }

    public async Task<IReadOnlyList<ReceivablePartLineDto>> ListReceivableLinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var links = await MrpData.LinksByPoLineAsync(db, null, cancellationToken);
        if (links.Count == 0)
        {
            return [];
        }

        var lineIds = links.Keys.ToList();
        var rows = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => lineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived),
                l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l, o })
            .ToListAsync(cancellationToken);
        var vendorIds = rows.Select(r => r.o.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
        var partIds = links.Values.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var projectIds = links.Values.Where(l => l.OwnerProjectId != null).Select(l => l.OwnerProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, cancellationToken);
        return rows.Where(r => r.l.Quantity > r.l.QuantityReceived)
            .OrderBy(r => r.l.NeededBy ?? r.o.OrderDate).ThenBy(r => r.o.Number, StringComparer.Ordinal).ThenBy(r => r.l.LineNumber)
            .Select(r =>
            {
                var link = links[r.l.Id];
                var part = parts[link.PartId];
                return new ReceivablePartLineDto(r.o.Id, r.o.Number, vendors.GetValueOrDefault(r.o.VendorId) ?? "", r.l.Id, r.l.LineNumber, r.l.Description,
                    part.Id, part.Display, part.Tracking.ToContract(), r.l.Quantity, r.l.QuantityReceived, r.l.Quantity - r.l.QuantityReceived, r.l.UnitPrice,
                    link.Ownership.ToContract(), link.OwnerProjectId is { } op ? projects.GetValueOrDefault(op) : null,
                    link.WarehouseId ?? part.DefaultWarehouseId, link.BinId ?? part.DefaultBinId, r.l.NeededBy);
            }).ToList();
    }

    public async Task<IReadOnlyList<LinkablePurchaseLineDto>> ListLinkableLinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var accounts = await ledger.AccountsAsync(db, cancellationToken);
        if (accounts is null)
        {
            return [];
        }

        var linked = (await MrpData.LinksByPoLineAsync(db, null, cancellationToken)).Keys.ToHashSet();
        var rows = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => l.AccountId == accounts.ReceivedNotInvoiced && !l.IsService)
            .Join(db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.Status == PurchaseOrderStatus.Draft || o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived),
                l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l, o.Number })
            .ToListAsync(cancellationToken);
        return rows.Where(r => !linked.Contains(r.l.Id) && r.l.Quantity > r.l.QuantityReceived)
            .OrderBy(r => r.Number, StringComparer.Ordinal).ThenBy(r => r.l.LineNumber)
            .Select(r => new LinkablePurchaseLineDto(r.l.Id, r.Number, r.l.LineNumber, r.l.Description, r.l.Quantity, r.l.UnitPrice)).ToList();
    }

    public async Task LinkPurchaseOrderLineAsync(LinkPurchaseLineRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var line = await db.Set<PurchaseOrderLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.PurchaseOrderLineId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Purchase order line", request.PurchaseOrderLineId);
        var accounts = await ledger.AccountsAsync(db, cancellationToken);
        if (accounts is not null && line.AccountId != accounts.ReceivedNotInvoiced)
        {
            throw new ArgumentException($"Line {line.LineNumber} isn't charged to the inventory clearing account (received not invoiced), so receiving it into stock would count the cost twice.");
        }

        if ((await MrpData.LinksByPoLineAsync(db, [line.Id], cancellationToken)).Count > 0)
        {
            throw new ArgumentException($"Line {line.LineNumber} is already linked to a part.");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == request.PartId && p.IsActive, cancellationToken) ?? throw WorkbenchNotFoundException.For("Part", request.PartId);
        var ownership = request.Ownership.ToDomain();
        if (ownership == InventoryOwnership.GovernmentFurnished)
        {
            throw new ArgumentException("Government-furnished material isn't bought; receive it as government-furnished.");
        }

        if (ownership == InventoryOwnership.Contract && (request.OwnerProjectId is not { } op || !await db.Set<Project>().AnyAsync(p => p.Id == op, cancellationToken)))
        {
            throw new ArgumentException("Contract inventory needs the project it's bought for.");
        }

        db.Set<MaterialPurchaseLink>().Add(new MaterialPurchaseLink
        {
            PartId = part.Id,
            PurchaseOrderLineId = line.Id,
            Ownership = ownership,
            OwnerProjectId = ownership == InventoryOwnership.Contract ? request.OwnerProjectId : null,
            WarehouseId = request.WarehouseId,
            BinId = request.BinId,
            CreatedBy = audit.Actor,
            CreatedAtUtc = ledger.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceivePurchaseOrderAsync(ReceiveIntoInventoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count(l => l.Quantity != 0) == 0)
        {
            throw new ArgumentException("Enter a quantity received on at least one line.");
        }

        receiptContext.Lines = request.Lines.Where(l => l.Quantity != 0).GroupBy(l => l.PurchaseOrderLineId).ToDictionary(g => g.Key, g => g.First());
        receiptContext.Created.Clear();
        try
        {
            await purchaseOrders.ReceiveAsync(request.PurchaseOrderId,
                new ReceiveRequest(request.ReceivedOn, request.Lines.Where(l => l.Quantity != 0).Select(l => new ReceiveLineRequest(l.PurchaseOrderLineId, l.Quantity)).ToList(), request.Note),
                cancellationToken);
            await using var db = await dbFactory.CreateAsync(cancellationToken);
            var ids = receiptContext.Created.ToList();
            var rows = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.Number).ToListAsync(cancellationToken);
            return await MaterialQueries.TransactionsAsync(db, rows, cancellationToken);
        }
        finally
        {
            receiptContext.Lines = null;
            receiptContext.Created.Clear();
        }
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> ReceiveGovernmentFurnishedAsync(GovernmentReceiptRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reference = PurchasingSupport.Required(request.Reference, "The shipping document (for example the DD Form 1149 number)", 100);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(request.PartId, cancellationToken);
        if (!await db.Set<Project>().AnyAsync(p => p.Id == request.ProjectId, cancellationToken))
        {
            throw WorkbenchNotFoundException.For("Project", request.ProjectId);
        }

        var (warehouse, bin) = await engine.LocationAsync(request.WarehouseId, request.BinId, part, cancellationToken);
        var created = new List<InventoryTransaction>();
        foreach (var serial in await engine.SerialsAsync(part, quantity, request.Serials, cancellationToken))
        {
            var (lot, s) = StockEngine.Tracking(part, request.Lot, serial.Length > 0 ? serial : null);
            var key = new StockKey(part.Id, warehouse.Id, bin.Id, lot, s, InventoryOwnership.GovernmentFurnished, request.ProjectId);
            var t = engine.Receive(part, key, s.Length > 0 ? 1 : quantity, 0, InventoryTransactionType.GovernmentReceipt, request.ReceivedOn, null, null, "government-furnished", reference);
            created.Add(t);
        }

        await engine.FlushAsync(JournalSource.Inventory, created[0].Id, request.ReceivedOn, "", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MaterialQueries.TransactionsAsync(db, created, cancellationToken);
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> IssueToProjectAsync(IssueToProjectRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var wbs = await StockEngine.ChargeableWbsAsync(db, request.WbsElementId, direct: true, cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(request.PartId, cancellationToken);
        var note = PurchasingSupport.Optional(request.Note, "The note", 500);
        var created = new List<InventoryTransaction>();
        foreach (var (balance, qty) in engine.Pick(part, quantity, wbs.ProjectId, request.BalanceId))
        {
            var t = engine.Issue(part, balance, qty, InventoryTransactionType.IssueToProject, request.Date, engine.Accounts?.DirectMaterial, $"issued to {wbs.Code}", wbs.ProjectId, wbs.Id);
            t.Reason = note;
            created.Add(t);
        }

        await ConsumeRequirementsAsync(db, wbs.Id, part.Id, quantity, cancellationToken);
        await engine.FlushAsync(JournalSource.Inventory, created[0].Id, request.Date, $"Material issue {created[0].Number}: {part.Display} to {wbs.Code}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MaterialQueries.TransactionsAsync(db, created, cancellationToken);
    }

    /// <summary>Issues to a WBS element count against its open requirements for the part, oldest first.</summary>
    internal static async Task ConsumeRequirementsAsync(IWorkbenchDbContext db, Guid wbsId, Guid partId, decimal quantity, CancellationToken ct)
    {
        var open = (await db.Set<ProjectMaterialRequirement>().Where(r => r.WbsElementId == wbsId && r.PartId == partId && !r.IsClosed).ToListAsync(ct))
            .OrderBy(r => r.NeededBy).ToList();
        foreach (var r in open)
        {
            var take = Math.Min(quantity, r.OpenQuantity);
            r.QuantityIssued += take;
            quantity -= take;
            if (r.OpenQuantity == 0)
            {
                r.IsClosed = true;
            }

            if (quantity <= 0)
            {
                break;
            }
        }
    }

    public async Task<IReadOnlyList<InventoryTransactionDto>> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var quantity = MaterialRules.Quantity(request.Quantity, "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var source = await db.Set<InventoryBalance>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BalanceId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Stock", request.BalanceId);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(source.PartId, cancellationToken);
        var balance = db.Set<InventoryBalance>().Local.First(b => b.Id == request.BalanceId);
        var (warehouse, bin) = await engine.LocationAsync(request.ToWarehouseId, request.ToBinId, part, cancellationToken);
        var (o, i) = engine.Transfer(part, balance, quantity, warehouse.Id, bin.Id, request.Date, PurchasingSupport.Optional(request.Note, "The note", 500));
        await engine.FlushAsync(JournalSource.Inventory, o.Id, request.Date, "", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await MaterialQueries.TransactionsAsync(db, [o, i], cancellationToken);
    }

    public async Task<InventoryTransactionDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = PurchasingSupport.Required(request.Reason, "A reason", 500);
        if (request.QuantityChange == 0)
        {
            throw new ArgumentException("Enter the quantity to add (positive) or remove (negative).");
        }

        MaterialRules.Quantity(Math.Abs(request.QuantityChange), "Quantity");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(request.PartId, cancellationToken);
        var (warehouse, bin) = await engine.LocationAsync(request.WarehouseId, request.BinId, part, cancellationToken);
        var (lot, serial) = StockEngine.Tracking(part, request.Lot, request.Serial);
        var ownership = request.Ownership.ToDomain();
        if (ownership != InventoryOwnership.General && request.OwnerProjectId is null)
        {
            throw new ArgumentException("Contract and government-furnished stock belong to a project; choose it.");
        }

        var key = new StockKey(part.Id, warehouse.Id, bin.Id, lot, serial, ownership, ownership == InventoryOwnership.General ? null : request.OwnerProjectId);
        var t = await AdjustAsync(engine, part, key, request.QuantityChange, InventoryTransactionType.Adjustment, request.Date, reason, request.UnitCost, cancellationToken);
        await engine.FlushAsync(JournalSource.Inventory, t.Id, request.Date, $"Inventory adjustment {t.Number}: {part.Display} ({reason})", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await MaterialQueries.TransactionsAsync(db, [t], cancellationToken))[0];
    }

    private static async Task<InventoryTransaction> AdjustAsync(
        StockEngine engine, Part part, StockKey key, decimal change, InventoryTransactionType type, DateOnly date, string reason, decimal? unitCost, CancellationToken ct)
    {
        InventoryTransaction t;
        if (change > 0)
        {
            if (key.Serial.Length > 0)
            {
                if (change != 1)
                {
                    throw new ArgumentException("A serial number is one unit.");
                }

                await engine.SerialsAsync(part, 1, [key.Serial], ct);
            }

            var unit = part.CostMethod == CostMethod.Average && unitCost is { } u ? MaterialRules.Cost(u, "Unit cost") : part.CurrentUnitCost;
            t = engine.Receive(part, key, change, Money.Round(change * unit), type, date, engine.Accounts?.Adjustment, engine.Accounts?.Adjustment, reason);
        }
        else
        {
            var balance = engine.Balances(part.Id).FirstOrDefault(b => b.BinId == key.BinId && b.Lot == key.Lot && b.Serial == key.Serial && b.Ownership == key.Ownership && b.OwnerProjectId == key.OwnerProjectId && b.Status == key.Status)
                ?? throw new ArgumentException($"There's no {part.Display} in that location to remove.");
            t = engine.Issue(part, balance, -change, type, date, engine.Accounts?.Adjustment, reason);
        }

        t.Reason = reason;
        return t;
    }

    public async Task<InventoryTransactionDto?> CycleCountAsync(CycleCountRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        MaterialRules.Quantity(request.CountedQuantity, "Counted quantity", allowZero: true);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var found = await db.Set<InventoryBalance>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == request.BalanceId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Stock", request.BalanceId);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(found.PartId, cancellationToken);
        var balance = db.Set<InventoryBalance>().Local.First(b => b.Id == request.BalanceId);
        var diff = request.CountedQuantity - balance.Quantity;
        if (diff == 0)
        {
            return null;
        }

        if (part.Tracking == TrackingMode.Serial && request.CountedQuantity > 1)
        {
            throw new ArgumentException("A serial number counts 0 or 1.");
        }

        var reason = "Cycle count" + (string.IsNullOrWhiteSpace(request.Note) ? "" : $": {PurchasingSupport.Optional(request.Note, "The note", 400)}");
        var key = StockKey.Of(balance);
        var t = CountDifference(engine, part, key, balance, diff, InventoryTransactionType.CycleCount, request.Date, reason);
        await engine.FlushAsync(JournalSource.Inventory, t.Id, request.Date, $"Cycle count {t.Number}: {part.Display}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await MaterialQueries.TransactionsAsync(db, [t], cancellationToken))[0];
    }

    /// <summary>
    /// Posts a counted difference (cycle counts and physical inventory): more found comes in at the current unit cost, less
    /// goes out at cost, both against the inventory adjustment account. Government-furnished stock moves by quantity only.
    /// </summary>
    internal static InventoryTransaction CountDifference(
        StockEngine engine, Part part, StockKey key, InventoryBalance? balance, decimal diff, InventoryTransactionType type, DateOnly date, string reason)
    {
        InventoryTransaction t;
        if (diff > 0)
        {
            t = engine.Receive(part, key, diff, Money.Round(diff * part.CurrentUnitCost), type, date, engine.Accounts?.Adjustment, engine.Accounts?.Adjustment, reason);
        }
        else
        {
            t = engine.Issue(part, balance ?? throw new ArgumentException($"There's no {part.Display} on record there to count out."), -diff, type, date, engine.Accounts?.Adjustment, reason);
        }

        t.Reason = reason;
        return t;
    }

    public async Task<InventoryTransactionDto> ChangeStandardCostAsync(Guid partId, ChangeStandardCostRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = PurchasingSupport.Required(request.Reason, "A reason", 500);
        var cost = MaterialRules.Cost(request.NewStandardCost, "Standard cost");
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var part = await engine.LoadPartAsync(partId, cancellationToken);
        if (part.CostMethod != CostMethod.Standard)
        {
            throw new ArgumentException($"{part.Display} is average cost; its standard cost is only for planning and changes on the part.");
        }

        if (cost == part.StandardCost)
        {
            throw new ArgumentException("That's already the standard cost.");
        }

        var before = part.StandardCost;
        var t = engine.Revalue(part, cost, request.Date, reason);
        audit.Record(db, MaterialRules.PartEntity, part.Id, "StandardCostChanged", new { StandardCost = before }, new { StandardCost = cost, Revaluation = t.Amount }, reason);
        await engine.FlushAsync(JournalSource.Inventory, t.Id, request.Date, $"Standard cost revaluation {t.Number}: {part.Display}", cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return (await MaterialQueries.TransactionsAsync(db, [t], cancellationToken))[0];
    }

    public async Task<TraceDto> TraceAsync(string? lot, string? serial, Guid? partId = null, CancellationToken cancellationToken = default)
    {
        lot = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim().ToUpperInvariant();
        serial = string.IsNullOrWhiteSpace(serial) ? null : serial.Trim().ToUpperInvariant();
        if (lot is null && serial is null)
        {
            throw new ArgumentException("Enter a lot or a serial number to trace.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<InventoryTransaction>().AsNoTracking();
        if (lot is not null)
        {
            q = q.Where(t => t.Lot == lot);
        }

        if (serial is not null)
        {
            q = q.Where(t => t.Serial == serial);
        }

        if (partId is { } pid)
        {
            q = q.Where(t => t.PartId == pid);
        }

        var history = await q.OrderBy(t => t.Number).ToListAsync(cancellationToken);
        var ids = history.Select(t => t.Id).ToList();
        var madeFromIds = await db.Set<WorkOrderGenealogy>().AsNoTracking().Where(g => ids.Contains(g.CompletionTransactionId)).Select(g => g.ComponentTransactionId).ToListAsync(cancellationToken);
        var usedInIds = await db.Set<WorkOrderGenealogy>().AsNoTracking().Where(g => ids.Contains(g.ComponentTransactionId)).Select(g => g.CompletionTransactionId).ToListAsync(cancellationToken);
        return new TraceDto(lot, serial, await MaterialQueries.TransactionsAsync(db, history, cancellationToken),
            await NodesAsync(db, madeFromIds, cancellationToken), await NodesAsync(db, usedInIds, cancellationToken));
    }

    private static async Task<IReadOnlyList<TraceNodeDto>> NodesAsync(IWorkbenchDbContext db, IReadOnlyList<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0)
        {
            return [];
        }

        var rows = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => ids.Contains(t.Id)).OrderBy(t => t.Number).ToListAsync(ct);
        var dtos = await MaterialQueries.TransactionsAsync(db, rows, ct);
        return dtos.Select(d => new TraceNodeDto(d.Id, d.Number, d.Type, d.Date, d.PartNumber, d.PartDescription, d.Lot, d.Serial, Math.Abs(d.Quantity),
            d.WorkOrderNumber ?? d.PurchaseOrderNumber ?? d.Reference)).ToList();
    }
}

/// <summary>Per-request details for a receipt into inventory (locations, lots, serials), handed to the purchasing decorator.</summary>
public sealed class InventoryReceiptContext
{
    public Dictionary<Guid, ReceiveIntoInventoryLine>? Lines { get; set; }

    /// <summary>The inventory transactions the last receipt created.</summary>
    public List<Guid> Created { get; } = [];
}

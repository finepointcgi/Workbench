using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>Where stock sits, whose it is and whether it can be used: a balance is unique by these.</summary>
internal readonly record struct StockKey(
    Guid PartId, Guid WarehouseId, Guid BinId, string Lot, string Serial, InventoryOwnership Ownership, Guid? OwnerProjectId,
    InventoryStockStatus Status = InventoryStockStatus.Available)
{
    public static StockKey Of(InventoryBalance b) => new(b.PartId, b.WarehouseId, b.BinId, b.Lot, b.Serial, b.Ownership, b.OwnerProjectId, b.Status);
}

/// <summary>
/// Stock movements and their costing for one unit of work: balances, moving-average and standard costing, the part's
/// book value, transaction numbers, and the GL amounts each movement implies (posted together by <see cref="FlushAsync"/>).
/// Government-furnished stock is never costed: it moves at 0 and posts nothing.
/// </summary>
internal sealed class StockEngine
{
    private readonly MaterialsLedger _ledger;
    private readonly List<LedgerAmount> _pending = [];
    private readonly List<InventoryTransaction> _pendingTransactions = [];
    private int _next;

    private StockEngine(IWorkbenchDbContext db, MaterialsLedger ledger, MaterialAccounts? accounts, int next)
    {
        Db = db;
        _ledger = ledger;
        Accounts = accounts;
        _next = next;
    }

    public IWorkbenchDbContext Db { get; }

    /// <summary>Null without a chart of accounts: movements are recorded but nothing posts.</summary>
    public MaterialAccounts? Accounts { get; }

    public string Actor => _ledger.Actor;

    public DateTime UtcNow => _ledger.UtcNow;

    public static async Task<StockEngine> CreateAsync(IWorkbenchDbContext db, MaterialsLedger ledger, CancellationToken ct)
    {
        var accounts = await ledger.AccountsAsync(db, ct);
        var last = await db.Set<InventoryTransaction>().OrderByDescending(t => t.Number).Select(t => t.Number).FirstOrDefaultAsync(ct);
        var next = last is not null && last.StartsWith("IT-", StringComparison.Ordinal) && int.TryParse(last[3..], out var n) ? n : 0;
        return new StockEngine(db, ledger, accounts, next);
    }

    public Guid? InventoryAccount(Part part) => Accounts is null ? null : part.Source == PartSource.Make ? Accounts.FinishedGoods : Accounts.Inventory;

    /// <summary>Loads the part tracked, with all its balances tracked (so on-hand sums see this unit of work's changes).</summary>
    public async Task<Part> LoadPartAsync(Guid partId, CancellationToken ct)
    {
        var part = await Db.Set<Part>().FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw WorkbenchNotFoundException.For("Part", partId);
        if (!Db.Set<InventoryBalance>().Local.Any(b => b.PartId == partId))
        {
            await Db.Set<InventoryBalance>().Where(b => b.PartId == partId).LoadAsync(ct);
        }

        return part;
    }

    public IEnumerable<InventoryBalance> Balances(Guid partId) => Db.Set<InventoryBalance>().Local.Where(b => b.PartId == partId && b.Quantity > 0);

    /// <summary>Costed stock (general and contract) of a part loaded with <see cref="LoadPartAsync"/>.</summary>
    public decimal CostedOnHand(Guid partId) => Balances(partId).Where(b => b.Ownership != InventoryOwnership.GovernmentFurnished).Sum(b => b.Quantity);

    public InventoryTransaction NewTransaction(InventoryTransactionType type, DateOnly date, Part part)
    {
        var t = new InventoryTransaction
        {
            Number = $"IT-{++_next:D6}",
            Type = type,
            Date = date,
            PartId = part.Id,
            CreatedBy = Actor,
            CreatedAtUtc = UtcNow,
        };
        Db.Set<InventoryTransaction>().Add(t);
        _pendingTransactions.Add(t);
        return t;
    }

    /// <summary>Checks and normalizes lot and serial for the part's tracking (untracked parts ignore them).</summary>
    public static (string Lot, string Serial) Tracking(Part part, string? lot, string? serial)
    {
        lot = (lot ?? "").Trim().ToUpperInvariant();
        serial = (serial ?? "").Trim().ToUpperInvariant();
        if (lot.Length > 64 || serial.Length > 64)
        {
            throw new ArgumentException("Lot and serial numbers are at most 64 characters.");
        }

        return part.Tracking switch
        {
            TrackingMode.Lot when lot.Length == 0 => throw new ArgumentException($"{part.Display} is lot tracked; enter the lot."),
            TrackingMode.Serial when serial.Length == 0 => throw new ArgumentException($"{part.Display} is serial tracked; enter a serial number for each unit."),
            TrackingMode.Lot => (lot, ""),
            TrackingMode.Serial => (lot, serial),
            _ => ("", ""),
        };
    }

    /// <summary>The serials for <paramref name="quantity"/> units of a serial-tracked part (one each, distinct, not already in stock).</summary>
    public async Task<IReadOnlyList<string>> SerialsAsync(Part part, decimal quantity, IReadOnlyList<string>? serials, CancellationToken ct)
    {
        if (part.Tracking != TrackingMode.Serial)
        {
            return [""];
        }

        var list = (serials ?? []).Select(s => (s ?? "").Trim().ToUpperInvariant()).Where(s => s.Length > 0).ToList();
        if (quantity != decimal.Truncate(quantity) || list.Count != quantity)
        {
            throw new ArgumentException($"{part.Display} is serial tracked: enter one serial number per unit ({quantity:0.####} units, {list.Count} serials).");
        }

        if (list.Distinct().Count() != list.Count)
        {
            throw new ArgumentException("The same serial number is entered twice.");
        }

        var inStock = await Db.Set<InventoryBalance>().AsNoTracking().Where(b => b.PartId == part.Id && list.Contains(b.Serial) && b.Quantity > 0).Select(b => b.Serial).ToListAsync(ct);
        inStock.AddRange(Db.Set<InventoryBalance>().Local.Where(b => b.PartId == part.Id && list.Contains(b.Serial) && b.Quantity > 0).Select(b => b.Serial));
        if (inStock.Count > 0)
        {
            throw new ArgumentException($"Serial {inStock[0]} of {part.Display} is already in stock.");
        }

        return list;
    }

    public async Task<(Warehouse Warehouse, Bin Bin)> LocationAsync(Guid? warehouseId, Guid? binId, Part part, CancellationToken ct)
    {
        var bid = binId ?? (warehouseId is null || warehouseId == part.DefaultWarehouseId ? part.DefaultBinId : null);
        if (bid is null)
        {
            throw new ArgumentException($"Choose the bin for {part.Display} (it has no default location).");
        }

        var bin = await Db.Set<Bin>().AsNoTracking().FirstOrDefaultAsync(b => b.Id == bid, ct) ?? throw WorkbenchNotFoundException.For("Bin", bid.Value);
        if (warehouseId is { } wid && wid != bin.WarehouseId)
        {
            throw new ArgumentException($"Bin {bin.Code} isn't in that warehouse.");
        }

        var warehouse = await Db.Set<Warehouse>().AsNoTracking().FirstAsync(w => w.Id == bin.WarehouseId, ct);
        if (!warehouse.IsActive || !bin.IsActive)
        {
            throw new ArgumentException($"{warehouse.Code} / {bin.Code} is inactive.");
        }

        return (warehouse, bin);
    }

    private InventoryBalance AddStock(StockKey key, decimal quantity, DateOnly date, string? reference)
    {
        var balance = Db.Set<InventoryBalance>().Local.FirstOrDefault(b => b.PartId == key.PartId && b.BinId == key.BinId && b.Lot == key.Lot && b.Serial == key.Serial
            && b.Ownership == key.Ownership && b.OwnerProjectId == key.OwnerProjectId && b.Status == key.Status);
        if (balance is null)
        {
            balance = new InventoryBalance
            {
                PartId = key.PartId,
                WarehouseId = key.WarehouseId,
                BinId = key.BinId,
                Lot = key.Lot,
                Serial = key.Serial,
                Ownership = key.Ownership,
                OwnerProjectId = key.OwnerProjectId,
                ReceivedOn = date,
                GovernmentReference = reference,
                Status = key.Status,
            };
            Db.Set<InventoryBalance>().Add(balance);
        }
        else if (balance.Quantity <= 0 || date < balance.ReceivedOn)
        {
            balance.ReceivedOn = date;
        }

        balance.Quantity += quantity;
        balance.UpdatedAtUtc = UtcNow;
        return balance;
    }

    private void RemoveStock(InventoryBalance balance, Part part, decimal quantity)
    {
        if (quantity > balance.Quantity)
        {
            throw new ArgumentException($"Only {balance.Quantity:0.####} {part.UnitOfMeasure} of {part.Display} is in that location{Describe(balance)}.");
        }

        balance.Quantity -= quantity;
        balance.UpdatedAtUtc = UtcNow;
        if (balance.Quantity == 0)
        {
            Db.Set<InventoryBalance>().Remove(balance);
        }
    }

    private static string Describe(InventoryBalance b) =>
        (b.Lot.Length > 0 ? $", lot {b.Lot}" : "") + (b.Serial.Length > 0 ? $", serial {b.Serial}" : "");

    private static void Stamp(InventoryTransaction t, StockKey key)
    {
        t.WarehouseId = key.WarehouseId;
        t.BinId = key.BinId;
        t.Lot = key.Lot;
        t.Serial = key.Serial;
        t.Ownership = key.Ownership;
        t.OwnerProjectId = key.OwnerProjectId;
        t.Status = key.Status;
    }

    /// <summary>
    /// Puts stock in. <paramref name="incomingValue"/> is what it cost (the credit side). Standard-cost parts enter at
    /// standard with the difference posted to <paramref name="varianceAccount"/>; average-cost parts at the incoming value,
    /// re-averaging. Government-furnished stock enters at 0 and posts nothing.
    /// </summary>
    public InventoryTransaction Receive(
        Part part, StockKey key, decimal quantity, decimal incomingValue, InventoryTransactionType type, DateOnly date,
        Guid? creditAccount, Guid? varianceAccount, string memo, string? reference = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("The quantity must be more than zero.");
        }

        var t = NewTransaction(type, date, part);
        Stamp(t, key);
        t.Quantity = quantity;
        t.Reference = reference;
        if (key.Ownership == InventoryOwnership.GovernmentFurnished)
        {
            AddStock(key, quantity, date, reference);
            return t;
        }

        var costedBefore = CostedOnHand(part.Id);
        incomingValue = Money.Round(incomingValue);
        decimal amount;
        if (part.CostMethod == CostMethod.Standard)
        {
            amount = Money.Round(quantity * part.StandardCost);
            t.UnitCost = part.StandardCost;
            t.Variance = incomingValue - amount;
        }
        else
        {
            amount = incomingValue;
            t.UnitCost = Math.Round(incomingValue / quantity, 4, MidpointRounding.AwayFromZero);
            var value = part.InventoryValue + amount;
            var qty = costedBefore + quantity;
            part.AverageCost = qty > 0 ? Math.Round(value / qty, 4, MidpointRounding.AwayFromZero) : t.UnitCost;
        }

        t.Amount = amount;
        part.InventoryValue += amount;
        part.UpdatedAtUtc = UtcNow;
        AddStock(key, quantity, date, reference);

        if (InventoryAccount(part) is { } inventory && creditAccount is { } credit)
        {
            _pending.Add(new(inventory, amount, $"{part.Display} {memo}"));
            if (t.Variance != 0)
            {
                _pending.Add(new(varianceAccount ?? Accounts!.PriceVariance, t.Variance, $"{part.Display} variance: {memo}"));
            }

            _pending.Add(new(credit, -incomingValue, $"{part.Display} {memo}"));
        }

        return t;
    }

    /// <summary>
    /// Takes stock out of a balance at cost: the current unit cost, or the part's remaining book value when the last
    /// costed unit leaves (so the GL is relieved exactly). Debits <paramref name="debitAccount"/> with the given segments.
    /// Returns the transaction (negative quantity and amount).
    /// </summary>
    public InventoryTransaction Issue(
        Part part, InventoryBalance balance, decimal quantity, InventoryTransactionType type, DateOnly date,
        Guid? debitAccount, string memo, Guid? projectId = null, Guid? wbsId = null)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("The quantity must be more than zero.");
        }

        var key = StockKey.Of(balance);
        var t = NewTransaction(type, date, part);
        Stamp(t, key);
        t.Quantity = -quantity;
        t.ProjectId = projectId;
        t.WbsElementId = wbsId;
        if (balance.Ownership == InventoryOwnership.GovernmentFurnished)
        {
            RemoveStock(balance, part, quantity);
            return t;
        }

        var costed = CostedOnHand(part.Id);
        var amount = quantity >= costed ? part.InventoryValue : Money.Round(quantity * part.CurrentUnitCost);
        RemoveStock(balance, part, quantity);
        t.UnitCost = part.CurrentUnitCost;
        t.Amount = -amount;
        part.InventoryValue -= amount;
        part.UpdatedAtUtc = UtcNow;
        if (InventoryAccount(part) is { } inventory && debitAccount is { } debit)
        {
            _pending.Add(new(debit, amount, $"{part.Display} {memo}", projectId, wbsId));
            _pending.Add(new(inventory, -amount, $"{part.Display} {memo}"));
        }

        return t;
    }

    /// <summary>Moves stock between locations (same owner, lot and serial; no GL: same account).</summary>
    public (InventoryTransaction Out, InventoryTransaction In) Transfer(Part part, InventoryBalance balance, decimal quantity, Guid toWarehouseId, Guid toBinId, DateOnly date, string? note)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("The quantity must be more than zero.");
        }

        if (toBinId == balance.BinId)
        {
            throw new ArgumentException("The stock is already in that bin.");
        }

        var fromKey = StockKey.Of(balance);
        var toKey = fromKey with { WarehouseId = toWarehouseId, BinId = toBinId };
        var receivedOn = balance.ReceivedOn;
        var value = balance.Ownership == InventoryOwnership.GovernmentFurnished ? 0 : Money.Round(quantity * part.CurrentUnitCost);
        var group = Guid.CreateVersion7();
        RemoveStock(balance, part, quantity);
        var target = AddStock(toKey, quantity, receivedOn, balance.GovernmentReference);
        if (target.ReceivedOn > receivedOn)
        {
            target.ReceivedOn = receivedOn;
        }

        var o = NewTransaction(InventoryTransactionType.TransferOut, date, part);
        Stamp(o, fromKey);
        o.Quantity = -quantity;
        o.UnitCost = value == 0 ? 0 : part.CurrentUnitCost;
        o.Amount = -value;
        o.TransferGroupId = group;
        o.Reason = note;
        var i = NewTransaction(InventoryTransactionType.TransferIn, date, part);
        Stamp(i, toKey);
        i.Quantity = quantity;
        i.UnitCost = o.UnitCost;
        i.Amount = value;
        i.TransferGroupId = group;
        i.Reason = note;
        return (o, i);
    }

    /// <summary>
    /// Moves stock between available, inspection hold and nonconforming in the same location (a pair of status change
    /// movements at cost, like a transfer; no GL: the stock stays in the same account).
    /// </summary>
    public (InventoryTransaction Out, InventoryTransaction In) ChangeStatus(Part part, InventoryBalance balance, decimal quantity, InventoryStockStatus status, DateOnly date, string reason)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("The quantity must be more than zero.");
        }

        if (balance.Status == status)
        {
            throw new ArgumentException("The stock already has that status.");
        }

        var fromKey = StockKey.Of(balance);
        var toKey = fromKey with { Status = status };
        var receivedOn = balance.ReceivedOn;
        var value = balance.Ownership == InventoryOwnership.GovernmentFurnished ? 0 : Money.Round(quantity * part.CurrentUnitCost);
        var group = Guid.CreateVersion7();
        RemoveStock(balance, part, quantity);
        var target = AddStock(toKey, quantity, receivedOn, balance.GovernmentReference);
        if (target.ReceivedOn > receivedOn)
        {
            target.ReceivedOn = receivedOn;
        }

        var o = NewTransaction(InventoryTransactionType.StatusChange, date, part);
        Stamp(o, fromKey);
        o.Quantity = -quantity;
        o.UnitCost = value == 0 ? 0 : part.CurrentUnitCost;
        o.Amount = -value;
        o.TransferGroupId = group;
        o.Reason = reason;
        var i = NewTransaction(InventoryTransactionType.StatusChange, date, part);
        Stamp(i, toKey);
        i.Quantity = quantity;
        i.UnitCost = o.UnitCost;
        i.Amount = value;
        i.TransferGroupId = group;
        i.Reason = reason;
        return (o, i);
    }

    /// <summary>A standard cost change on stock on hand: the book value moves to quantity × new standard, against adjustments.</summary>
    public InventoryTransaction Revalue(Part part, decimal newStandard, DateOnly date, string reason)
    {
        var qty = CostedOnHand(part.Id);
        var newValue = Money.Round(qty * newStandard);
        var delta = newValue - part.InventoryValue;
        var t = NewTransaction(InventoryTransactionType.Revaluation, date, part);
        t.UnitCost = newStandard;
        t.Amount = delta;
        t.Reason = reason;
        part.StandardCost = newStandard;
        part.InventoryValue = newValue;
        part.UpdatedAtUtc = UtcNow;
        if (InventoryAccount(part) is { } inventory)
        {
            _pending.Add(new(inventory, delta, $"{part.Display} standard cost to {newStandard:N4}"));
            _pending.Add(new(Accounts!.Adjustment, -delta, $"{part.Display} revaluation: {reason}"));
        }

        return t;
    }

    /// <summary>Adds GL amounts that aren't a stock movement (labor applied, WIP relief) to the next flush.</summary>
    public void AddLedger(params LedgerAmount[] amounts)
    {
        if (Accounts is not null)
        {
            _pending.AddRange(amounts);
        }
    }

    /// <summary>Posts the pending amounts as one entry and links it to this flush's transactions. Clears the pending lists.</summary>
    public async Task<JournalEntry?> FlushAsync(JournalSource source, Guid sourceId, DateOnly date, string description, CancellationToken ct)
    {
        JournalEntry? entry = null;
        if (Accounts is not null && _pending.Count > 0)
        {
            entry = await _ledger.PostAsync(Db, source, sourceId, date, description, _pending, ct);
        }

        foreach (var t in _pendingTransactions)
        {
            t.JournalEntryId = t.Amount != 0 || t.Variance != 0 ? entry?.Id : null; // Government-furnished movements never post.
        }

        _pending.Clear();
        _pendingTransactions.Clear();
        return entry;
    }

    /// <summary>
    /// Picks stock for <paramref name="quantity"/>: one balance when given (checked against the destination), otherwise
    /// the destination project's government-furnished stock, then its contract stock, then general stock, oldest first.
    /// </summary>
    public IReadOnlyList<(InventoryBalance Balance, decimal Quantity)> Pick(Part part, decimal quantity, Guid? destinationProjectId, Guid? balanceId)
    {
        if (quantity <= 0)
        {
            throw new ArgumentException("The quantity must be more than zero.");
        }

        if (balanceId is { } bid)
        {
            var balance = Db.Set<InventoryBalance>().Local.FirstOrDefault(b => b.Id == bid && b.PartId == part.Id)
                ?? throw new ArgumentException($"That stock isn't {part.Display}, or it's gone.");
            EnsureUsableFor(balance, destinationProjectId);
            if (balance.Quantity < quantity)
            {
                throw new ArgumentException($"Only {balance.Quantity:0.####} {part.UnitOfMeasure} of {part.Display} is in that location{Describe(balance)}.");
            }

            return [(balance, quantity)];
        }

        var eligible = Balances(part.Id).Where(b => Usable(b, destinationProjectId))
            .OrderBy(b => b.Ownership switch { InventoryOwnership.GovernmentFurnished => 0, InventoryOwnership.Contract => 1, _ => 2 })
            .ThenBy(b => b.ReceivedOn).ThenBy(b => b.Lot, StringComparer.Ordinal).ThenBy(b => b.Serial, StringComparer.Ordinal)
            .ToList();
        var available = eligible.Sum(b => b.Quantity);
        if (available < quantity)
        {
            throw new ArgumentException($"Short {quantity - available:0.####} {part.UnitOfMeasure} of {part.Display}: {available:0.####} available for this use.");
        }

        var picks = new List<(InventoryBalance, decimal)>();
        var left = quantity;
        foreach (var b in eligible)
        {
            var take = Math.Min(left, b.Quantity);
            picks.Add((b, take));
            left -= take;
            if (left == 0)
            {
                break;
            }
        }

        return picks;
    }

    public decimal Available(Guid partId, Guid? destinationProjectId) => Balances(partId).Where(b => Usable(b, destinationProjectId)).Sum(b => b.Quantity);

    private static bool Usable(InventoryBalance b, Guid? project) =>
        b.Status == InventoryStockStatus.Available && (b.Ownership == InventoryOwnership.General || (project is { } p && b.OwnerProjectId == p));

    public static void EnsureUsableFor(InventoryBalance b, Guid? project)
    {
        if (b.Status != InventoryStockStatus.Available)
        {
            throw new ArgumentException(b.Status == InventoryStockStatus.InspectionHold
                ? "That stock is on inspection hold; it can be used once it passes inspection."
                : "That stock is nonconforming; it can be used only if the material review board releases it.");
        }

        if (!Usable(b, project))
        {
            throw new ArgumentException(b.Ownership == InventoryOwnership.GovernmentFurnished
                ? "Government-furnished material can only be used on the project it was furnished for (FAR 52.245-1)."
                : "Contract inventory can only be used on the project it was bought for.");
        }
    }

    /// <summary>An open, chargeable, lowest-level direct WBS element of an active project.</summary>
    public static async Task<WbsElement> ChargeableWbsAsync(IWorkbenchDbContext db, Guid wbsId, bool direct, CancellationToken ct)
    {
        var wbs = await db.Set<WbsElement>().AsNoTracking().FirstOrDefaultAsync(w => w.Id == wbsId, ct) ?? throw WorkbenchNotFoundException.For("WBS element", wbsId);
        var project = await db.Set<Project>().AsNoTracking().FirstAsync(p => p.Id == wbs.ProjectId, ct);
        if (project.Status != ProjectStatus.Active || !wbs.IsOpen || !wbs.IsChargeable || await db.Set<WbsElement>().AnyAsync(w => w.ParentId == wbs.Id, ct))
        {
            throw new ArgumentException($"{wbs.Code} isn't an open, chargeable lowest-level WBS element of an active project.");
        }

        if (direct && wbs.Kind != ChargeKind.Direct)
        {
            throw new ArgumentException($"{wbs.Code} isn't a direct WBS element; material is issued to direct cost.");
        }

        return wbs;
    }
}

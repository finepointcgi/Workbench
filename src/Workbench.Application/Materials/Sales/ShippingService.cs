using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>
/// Fulfilment (Costpoint Shipping): pick lists reserve stock for released order lines, by bin, lot, serial and owner;
/// picked stock is packed into a shipment with packages, weights and tracking; confirming the shipment issues the
/// inventory (cost of goods sold, or the order's WBS element for cost-type work) and numbers the DD Form 250. The DD250,
/// the WAWF receiving report and the delivery invoice come from the confirmed shipment.
/// </summary>
public sealed class ShippingService(
    IWorkbenchDbContextFactory dbFactory,
    MaterialsLedger ledger,
    AuditRecorder audit,
    IOptions<SalesShippingOptions> options,
    IWawfSubmitter submitter,
    TimeProvider clock,
    ShipmentInvoicer? invoicer = null) : IShippingService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    // ---- Picking ----

    public async Task<IReadOnlyList<PickableStockDto>> ListPickableStockAsync(Guid salesOrderLineId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var line = await db.Set<SalesOrderLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == salesOrderLineId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sales order line", salesOrderLineId);
        var order = await db.Set<SalesOrder>().AsNoTracking().FirstAsync(o => o.Id == line.SalesOrderId, cancellationToken);
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => b.PartId == line.PartId && b.Quantity > 0).ToListAsync(cancellationToken);
        var reserved = await SalesReservations.LoadAsync(db, [line.PartId], null, cancellationToken);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, cancellationToken);
        var binIds = balances.Select(b => b.BinId).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, cancellationToken);
        var projects = await ProjectCodesAsync(db, balances.Select(b => b.OwnerProjectId), cancellationToken);
        return balances.Where(b => SalesRules.Usable(b.Ownership, b.OwnerProjectId, order.ProjectId))
            .OrderBy(b => Rank(b.Ownership)).ThenBy(b => b.ReceivedOn).ThenBy(b => b.Lot, StringComparer.Ordinal).ThenBy(b => b.Serial, StringComparer.Ordinal)
            .Select(b =>
            {
                var held = reserved.GetValueOrDefault(SalesReservations.Of(b));
                return new PickableStockDto(b.Id, b.WarehouseId, warehouses.GetValueOrDefault(b.WarehouseId) ?? "", b.BinId, bins.GetValueOrDefault(b.BinId) ?? "", b.Lot, b.Serial,
                    b.Ownership.ToContract(), b.OwnerProjectId is { } p ? projects.GetValueOrDefault(p) : null, b.Quantity, held, Math.Max(0, b.Quantity - held), b.ReceivedOn);
            }).ToList();
    }

    /// <summary>The project's contract stock first, then general stock. Government-furnished material is only picked on purpose.</summary>
    private static int Rank(InventoryOwnership o) => o switch { InventoryOwnership.Contract => 0, InventoryOwnership.General => 1, _ => 2 };

    public async Task<PickListDto> CreatePickListAsync(Guid salesOrderId, CreatePickListRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await db.Set<SalesOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == salesOrderId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sales order", salesOrderId);
        EnsureShippable(order);
        var dto = await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
        var toPick = dto.Lines.ToDictionary(l => l.Id, l => Math.Max(0, l.QuantityOpen - l.QuantityOnPickLists));
        var requests = request.Lines ?? dto.Lines.Where(l => toPick[l.Id] > 0).Select(l => new PickLineRequest(l.Id, toPick[l.Id])).ToList();
        if (requests.Count == 0)
        {
            throw new ArgumentException("Nothing is left to pick on this order.");
        }

        var partIds = order.Lines.Select(l => l.PartId).Distinct().ToList();
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => partIds.Contains(b.PartId) && b.Quantity > 0).ToListAsync(cancellationToken);
        var reserved = await SalesReservations.LoadAsync(db, partIds, null, cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var pick = new PickList
        {
            Number = await SalesRules.NextNumberAsync(db.Set<PickList>(), q => q.Select(p => p.Number), "PK-", cancellationToken),
            SalesOrderId = order.Id,
            Status = PickListStatus.Open,
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        var shortages = new List<string>();
        foreach (var r in requests)
        {
            var line = order.Lines.FirstOrDefault(l => l.Id == r.SalesOrderLineId) ?? throw new ArgumentException("A line isn't on this order.");
            var part = parts[line.PartId];
            var quantity = MaterialRules.Quantity(r.Quantity, $"Line {line.LineNumber} quantity");
            if (quantity > toPick[line.Id])
            {
                throw new ArgumentException($"Line {line.LineNumber}: only {toPick[line.Id]:0.####} {part.UnitOfMeasure} is left to pick.");
            }

            decimal Free(InventoryBalance b) => Math.Max(0, b.Quantity - reserved.GetValueOrDefault(SalesReservations.Of(b)));
            IEnumerable<InventoryBalance> sources;
            if (r.BalanceId is { } bid)
            {
                var chosen = balances.FirstOrDefault(b => b.Id == bid && b.PartId == part.Id) ?? throw new ArgumentException($"That stock isn't {part.Display}, or it's gone.");
                if (!SalesRules.Usable(chosen.Ownership, chosen.OwnerProjectId, order.ProjectId))
                {
                    StockEngine.EnsureUsableFor(chosen, order.ProjectId);
                }

                if (Free(chosen) < quantity)
                {
                    throw new ArgumentException($"Only {Free(chosen):0.####} {part.UnitOfMeasure} of {part.Display} is free in that location.");
                }

                sources = [chosen];
            }
            else
            {
                sources = balances.Where(b => b.PartId == part.Id && b.Ownership != InventoryOwnership.GovernmentFurnished && SalesRules.Usable(b.Ownership, b.OwnerProjectId, order.ProjectId))
                    .OrderBy(b => Rank(b.Ownership)).ThenBy(b => b.ReceivedOn).ThenBy(b => b.Lot, StringComparer.Ordinal).ThenBy(b => b.Serial, StringComparer.Ordinal);
            }

            var left = quantity;
            foreach (var b in sources)
            {
                var take = Math.Min(left, Free(b));
                if (take <= 0)
                {
                    continue;
                }

                pick.Lines.Add(new PickListLine
                {
                    PickListId = pick.Id,
                    SalesOrderLineId = line.Id,
                    PartId = part.Id,
                    WarehouseId = b.WarehouseId,
                    BinId = b.BinId,
                    Lot = b.Lot,
                    Serial = b.Serial,
                    Ownership = b.Ownership,
                    OwnerProjectId = b.OwnerProjectId,
                    Quantity = take,
                });
                var key = SalesReservations.Of(b);
                reserved[key] = reserved.GetValueOrDefault(key) + take;
                left -= take;
                if (left == 0)
                {
                    break;
                }
            }

            if (left > 0)
            {
                shortages.Add($"line {line.LineNumber} {part.Display} short {left:0.####}");
            }
        }

        if (pick.Lines.Count == 0)
        {
            throw new ArgumentException($"No stock is available to pick ({string.Join("; ", shortages)}).");
        }

        db.Set<PickList>().Add(pick);
        audit.Record(db, SalesRules.OrderEntity, order.Id, "PickListCreated", after: new { pick.Number, Lines = pick.Lines.Count, Shortages = shortages });
        await db.SaveChangesAsync(cancellationToken);
        return await PickListAsync(db, pick.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<PickListSummaryDto>> ListPickListsAsync(bool openOnly = true, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<PickList>().AsNoTracking().Include(p => p.Lines).AsQueryable();
        if (openOnly)
        {
            q = q.Where(p => p.Status == PickListStatus.Open);
        }

        return await SalesQueries.PickSummariesAsync(db, await q.OrderByDescending(p => p.Number).Take(500).ToListAsync(cancellationToken), cancellationToken);
    }

    public async Task<PickListDto> GetPickListAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await PickListAsync(db, pickListId, cancellationToken);
    }

    public async Task<PickListDto> ConfirmPickAsync(Guid pickListLineId, ConfirmPickRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var line = await db.Set<PickListLine>().FirstOrDefaultAsync(l => l.Id == pickListLineId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Pick list line", pickListLineId);
        var pick = await db.Set<PickList>().AsNoTracking().FirstAsync(p => p.Id == line.PickListId, cancellationToken);
        if (pick.Status != PickListStatus.Open)
        {
            throw new InvalidOperationException($"{pick.Number} is {pick.Status.ToString().ToLowerInvariant()}.");
        }

        var quantity = MaterialRules.Quantity(request.QuantityPicked, "Quantity picked", allowZero: true);
        if (quantity > line.Quantity)
        {
            throw new ArgumentException($"Pick at most {line.Quantity:0.####} (what the list asks for).");
        }

        var part = await db.Set<Part>().AsNoTracking().FirstAsync(p => p.Id == line.PartId, cancellationToken);
        var lot = string.IsNullOrWhiteSpace(request.Lot) ? line.Lot : request.Lot.Trim().ToUpperInvariant();
        var serial = string.IsNullOrWhiteSpace(request.Serial) ? line.Serial : request.Serial.Trim().ToUpperInvariant();
        if (lot != line.Lot || serial != line.Serial)
        {
            // A different lot or serial from the same bin and owner: it must be there and free.
            var order = await db.Set<SalesOrder>().AsNoTracking().FirstAsync(o => o.Id == pick.SalesOrderId, cancellationToken);
            var balance = await db.Set<InventoryBalance>().AsNoTracking().FirstOrDefaultAsync(b => b.PartId == line.PartId && b.BinId == line.BinId && b.Lot == lot && b.Serial == serial
                && b.Quantity > 0, cancellationToken) ?? throw new ArgumentException($"{part.Display} {SalesRules.Describe(lot, serial)} isn't in that bin.");
            if (!SalesRules.Usable(balance.Ownership, balance.OwnerProjectId, order.ProjectId))
            {
                StockEngine.EnsureUsableFor(balance, order.ProjectId);
            }

            var reserved = await SalesReservations.LoadAsync(db, [line.PartId], null, cancellationToken);
            if (balance.Quantity - reserved.GetValueOrDefault(SalesReservations.Of(balance)) < quantity)
            {
                throw new ArgumentException($"{part.Display} {SalesRules.Describe(lot, serial)} is already on another pick list or shipment.");
            }

            (line.Lot, line.Serial, line.Ownership, line.OwnerProjectId) = (lot, serial, balance.Ownership, balance.OwnerProjectId);
        }

        line.QuantityPicked = quantity;
        line.PickedBy = audit.Actor;
        line.PickedAtUtc = UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        return await PickListAsync(db, pick.Id, cancellationToken);
    }

    public async Task<PickListDto> CancelPickListAsync(Guid pickListId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pick = await db.Set<PickList>().FirstOrDefaultAsync(p => p.Id == pickListId, cancellationToken) ?? throw WorkbenchNotFoundException.For("Pick list", pickListId);
        if (pick.Status != PickListStatus.Open)
        {
            throw new InvalidOperationException($"{pick.Number} is {pick.Status.ToString().ToLowerInvariant()}.");
        }

        pick.Status = PickListStatus.Cancelled;
        audit.Record(db, SalesRules.OrderEntity, pick.SalesOrderId, "PickListCancelled", after: new { pick.Number });
        await db.SaveChangesAsync(cancellationToken);
        return await PickListAsync(db, pick.Id, cancellationToken);
    }

    private static async Task<PickListDto> PickListAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var pick = await db.Set<PickList>().AsNoTracking().Include(p => p.Lines).FirstOrDefaultAsync(p => p.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Pick list", id);
        var summary = (await SalesQueries.PickSummariesAsync(db, [pick], ct))[0];
        var orderLineIds = pick.Lines.Select(l => l.SalesOrderLineId).Distinct().ToList();
        var orderLines = await db.Set<SalesOrderLine>().AsNoTracking().Where(l => orderLineIds.Contains(l.Id)).ToDictionaryAsync(l => l.Id, l => l.LineNumber, ct);
        var partIds = pick.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = pick.Lines.Select(l => l.BinId).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var projects = await ProjectCodesAsync(db, pick.Lines.Select(l => l.OwnerProjectId), ct);
        var lines = pick.Lines
            .OrderBy(l => warehouses.GetValueOrDefault(l.WarehouseId), StringComparer.Ordinal).ThenBy(l => bins.GetValueOrDefault(l.BinId), StringComparer.Ordinal)
            .ThenBy(l => orderLines.GetValueOrDefault(l.SalesOrderLineId)).ThenBy(l => l.Serial, StringComparer.Ordinal)
            .Select(l =>
            {
                var p = parts[l.PartId];
                return new PickListLineDto(l.Id, l.SalesOrderLineId, orderLines.GetValueOrDefault(l.SalesOrderLineId), p.Id, p.Display, p.Description, p.UnitOfMeasure,
                    l.WarehouseId, warehouses.GetValueOrDefault(l.WarehouseId) ?? "", l.BinId, bins.GetValueOrDefault(l.BinId) ?? "", l.Lot, l.Serial, l.Ownership.ToContract(),
                    l.OwnerProjectId is { } op ? projects.GetValueOrDefault(op) : null, l.Quantity, l.QuantityPicked, l.PickedBy, l.PickedAtUtc);
            }).ToList();
        return new PickListDto(summary, lines);
    }

    // ---- Packing and shipping ----

    public async Task<ShipmentDto> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ids = (request.PickListIds ?? []).Distinct().ToList();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Choose the pick lists to pack.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var picks = await db.Set<PickList>().Include(p => p.Lines).Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        if (picks.Count != ids.Count)
        {
            throw new WorkbenchNotFoundException("A pick list wasn't found.");
        }

        if (picks.Select(p => p.SalesOrderId).Distinct().Count() > 1)
        {
            throw new ArgumentException("A shipment is for one sales order; pack each order's pick lists separately.");
        }

        if (picks.FirstOrDefault(p => p.Status != PickListStatus.Open) is { } closed)
        {
            throw new InvalidOperationException($"{closed.Number} is {closed.Status.ToString().ToLowerInvariant()}.");
        }

        var picked = picks.SelectMany(p => p.Lines).Where(l => l.PickedAtUtc != null && l.QuantityPicked > 0).ToList();
        if (picked.Count == 0)
        {
            throw new ArgumentException("Nothing on those pick lists has been picked yet.");
        }

        var order = await db.Set<SalesOrder>().Include(o => o.Lines).FirstAsync(o => o.Id == picks[0].SalesOrderId, cancellationToken);
        EnsureShippable(order);
        var orderLines = order.Lines.ToDictionary(l => l.Id);
        var shipTos = picked.Select(l => ShipTo(order, orderLines[l.SalesOrderLineId])).Distinct().ToList();
        if (shipTos.Count > 1)
        {
            throw new ArgumentException("Those lines go to different ship-to addresses; a shipment goes to one. Pack them separately.");
        }

        var shipment = new Shipment
        {
            Number = await SalesRules.NextNumberAsync(db.Set<Shipment>(), q => q.Select(s => s.Number), "SH-", cancellationToken),
            CompanyId = order.CompanyId,
            SalesOrderId = order.Id,
            Status = ShipmentStatus.Packing,
            ShipToName = shipTos[0].Name,
            ShipToAddress = shipTos[0].Address,
            ShipToCode = shipTos[0].Code,
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        foreach (var l in picked)
        {
            shipment.Lines.Add(new ShipmentLine
            {
                ShipmentId = shipment.Id,
                SalesOrderLineId = l.SalesOrderLineId,
                PickListLineId = l.Id,
                PartId = l.PartId,
                WarehouseId = l.WarehouseId,
                BinId = l.BinId,
                Lot = l.Lot,
                Serial = l.Serial,
                Ownership = l.Ownership,
                OwnerProjectId = l.OwnerProjectId,
                Quantity = l.QuantityPicked,
                UnitPrice = orderLines[l.SalesOrderLineId].UnitPrice,
                PackageNumber = 1,
            });
        }

        shipment.Packages.Add(new ShipmentPackage { ShipmentId = shipment.Id, Number = 1 });
        foreach (var p in picks)
        {
            p.Status = PickListStatus.Packed;
        }

        db.Set<Shipment>().Add(shipment);
        audit.Record(db, SalesRules.ShipmentEntity, shipment.Id, "Created", after: new { shipment.Number, Order = order.Number, PickLists = picks.Select(p => p.Number).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipment.Id, cancellationToken);
    }

    private sealed record ShipToKey(string? Name, string? Address, string? Code);

    private static ShipToKey ShipTo(SalesOrder order, SalesOrderLine line) =>
        line.ShipToCode is not null || line.ShipToName is not null || line.ShipToAddress is not null
            ? new ShipToKey(line.ShipToName, line.ShipToAddress, line.ShipToCode)
            : new ShipToKey(order.ShipToName, order.ShipToAddress, order.ShipToCode);

    public async Task<IReadOnlyList<ShipmentSummaryDto>> ListShipmentsAsync(ShipmentQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Shipment>().AsNoTracking().Include(s => s.Lines).Include(s => s.Packages).AsQueryable();
        if (query.Status is { } st)
        {
            var status = st.ToDomain();
            q = q.Where(s => s.Status == status);
        }

        if (query.SalesOrderId is { } oid)
        {
            q = q.Where(s => s.SalesOrderId == oid);
        }

        if (query.From is { } from)
        {
            q = q.Where(s => s.ShipDate >= from);
        }

        if (query.To is { } to)
        {
            q = q.Where(s => s.ShipDate <= to);
        }

        var rows = await SalesQueries.ShipmentSummariesAsync(db, await q.OrderByDescending(s => s.Number).Take(Math.Clamp(query.Take, 1, 2000)).ToListAsync(cancellationToken), cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var t = query.Search.Trim();
            rows = rows.Where(r => r.Number.Contains(t, StringComparison.OrdinalIgnoreCase) || (r.ShipmentNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                || r.SalesOrderNumber.Contains(t, StringComparison.OrdinalIgnoreCase) || r.CustomerName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (r.TrackingNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.ContractNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows;
    }

    public async Task<ShipmentDto> GetShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await ShipmentAsync(db, shipmentId, cancellationToken);
    }

    public async Task<ShipmentDto> SaveShipmentAsync(Guid shipmentId, SaveShipmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var shipment = await LoadAsync(db, shipmentId, cancellationToken);
        if (shipment.Status != ShipmentStatus.Packing)
        {
            throw new InvalidOperationException($"{shipment.Number} is {shipment.Status.ToString().ToLowerInvariant()}; its packing can't change.");
        }

        var packages = request.Packages ?? [];
        if (packages.Count == 0)
        {
            throw new ArgumentException("A shipment has at least one package.");
        }

        if (packages.Select(p => p.Number).Distinct().Count() != packages.Count || packages.Any(p => p.Number < 1 || p.Number > 999))
        {
            throw new ArgumentException("Number the packages 1, 2, 3… without repeats.");
        }

        foreach (var p in packages)
        {
            MaterialRules.Quantity(p.Weight, $"Package {p.Number} weight", allowZero: true);
            foreach (var d in new[] { p.Length, p.Width, p.Height })
            {
                if (d is { } v)
                {
                    MaterialRules.Quantity(v, $"Package {p.Number} dimensions", allowZero: true);
                }
            }
        }

        shipment.ShipDate = request.ShipDate;
        shipment.Carrier = PurchasingSupport.Optional(request.Carrier, "The carrier", 100);
        shipment.BillOfLading = PurchasingSupport.Optional(request.BillOfLading, "The bill of lading", 64);
        shipment.TrackingNumber = PurchasingSupport.Optional(request.TrackingNumber, "The tracking number", 64);
        shipment.IsFinal = request.IsFinal;
        shipment.Notes = PurchasingSupport.Optional(request.Notes, "The notes", 2000);
        foreach (var old in shipment.Packages.ToList())
        {
            shipment.Packages.Remove(old);
            db.Set<ShipmentPackage>().Remove(old);
        }

        foreach (var p in packages.OrderBy(p => p.Number))
        {
            var package = new ShipmentPackage
            {
                ShipmentId = shipment.Id,
                Number = p.Number,
                Weight = p.Weight,
                Length = p.Length,
                Width = p.Width,
                Height = p.Height,
                TrackingNumber = PurchasingSupport.Optional(p.TrackingNumber, "A package tracking number", 64),
            };
            shipment.Packages.Add(package);
            db.Set<ShipmentPackage>().Add(package);
        }

        var numbers = packages.Select(p => p.Number).ToHashSet();
        foreach (var lp in request.LinePackages ?? [])
        {
            var line = shipment.Lines.FirstOrDefault(l => l.Id == lp.ShipmentLineId) ?? throw new ArgumentException("A line isn't on this shipment.");
            if (lp.PackageNumber is { } n && !numbers.Contains(n))
            {
                throw new ArgumentException($"There's no package {n}.");
            }

            line.PackageNumber = lp.PackageNumber;
        }

        foreach (var line in shipment.Lines.Where(l => l.PackageNumber is { } n && !numbers.Contains(n)))
        {
            line.PackageNumber = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipment.Id, cancellationToken);
    }

    public async Task<ShipmentDto> ConfirmShipmentAsync(Guid shipmentId, ConfirmShipmentRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var shipment = await LoadAsync(db, shipmentId, cancellationToken);
        if (shipment.Status != ShipmentStatus.Packing)
        {
            throw new InvalidOperationException($"{shipment.Number} is already {shipment.Status.ToString().ToLowerInvariant()}.");
        }

        if (shipment.Lines.Any(l => l.PackageNumber is null))
        {
            throw new ArgumentException("Put every line in a package before shipping.");
        }

        var order = await db.Set<SalesOrder>().Include(o => o.Lines).FirstAsync(o => o.Id == shipment.SalesOrderId, cancellationToken);
        EnsureShippable(order);
        var (clins, contract) = await SalesOrderService.CostContextAsync(db, order, cancellationToken);
        var orderLines = order.Lines.ToDictionary(l => l.Id);
        foreach (var g in shipment.Lines.GroupBy(l => l.SalesOrderLineId))
        {
            var ol = orderLines[g.Key];
            if (ol.IsClosed || g.Sum(l => l.Quantity) > ol.QuantityOpen)
            {
                throw new ArgumentException($"Line {ol.LineNumber} has only {ol.QuantityOpen:0.####} left to ship.");
            }
        }

        var date = request.ShipDate;
        var engine = await StockEngine.CreateAsync(db, ledger, cancellationToken);
        var cogs = engine.Accounts is null ? null : await SalesRules.CostOfGoodsSoldAsync(db, options, UtcNow, cancellationToken);
        WbsElement? wbs = null;
        var number = await ShipmentNumberAsync(db, order, shipment.IsFinal, cancellationToken);
        var memo = $"shipped on {shipment.Number} ({number})";
        foreach (var line in shipment.Lines.OrderBy(l => orderLines[l.SalesOrderLineId].LineNumber))
        {
            var ol = orderLines[line.SalesOrderLineId];
            var part = await engine.LoadPartAsync(line.PartId, cancellationToken);
            var balance = engine.Balances(part.Id).FirstOrDefault(b => b.BinId == line.BinId && b.Lot == line.Lot && b.Serial == line.Serial && b.Ownership == line.Ownership
                && b.OwnerProjectId == line.OwnerProjectId && b.Status == InventoryStockStatus.Available) ?? throw new ArgumentException($"{part.Display} {SalesRules.Describe(line.Lot, line.Serial)} is no longer in its bin.");
            StockEngine.EnsureUsableFor(balance, order.ProjectId);
            InventoryTransaction t;
            line.ChargedToWbs = SalesRules.ChargesWbs(ol, ol.ClinId is { } c ? clins.GetValueOrDefault(c) : null, contract);
            if (line.ChargedToWbs)
            {
                wbs ??= order.WbsElementId is { } wid
                    ? await StockEngine.ChargeableWbsAsync(db, wid, direct: true, cancellationToken)
                    : throw new ArgumentException("Cost-type lines are charged to the order's WBS element; choose it on the order.");
                t = engine.Issue(part, balance, line.Quantity, InventoryTransactionType.IssueToProject, date, engine.Accounts?.DirectMaterial, memo, wbs.ProjectId, wbs.Id);
            }
            else
            {
                t = engine.Issue(part, balance, line.Quantity, InventoryTransactionType.SalesShipment, date, cogs, memo,
                    order.WbsElementId is not null ? order.ProjectId : null, order.WbsElementId);
            }

            t.Reference = shipment.Number;
            t.Reason = $"{order.Number} line {ol.LineNumber}";
            line.UnitCost = t.UnitCost;
            line.CostAmount = -t.Amount;
            line.InventoryTransactionId = t.Id;
            ol.QuantityShipped += line.Quantity;
        }

        var entry = await engine.FlushAsync(JournalSource.Inventory, shipment.Id, date, $"Shipment {shipment.Number} ({number}) on {order.Number}", cancellationToken);
        shipment.JournalEntryId = entry?.Id;
        shipment.ShipmentNumber = number;
        shipment.ShipDate = date;
        shipment.Status = ShipmentStatus.Confirmed;
        shipment.ConfirmedBy = audit.Actor;
        shipment.ConfirmedAtUtc = UtcNow;
        order.Status = order.Lines.All(l => l.QuantityOpen == 0) ? SalesOrderStatus.Shipped : SalesOrderStatus.PartlyShipped;
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.ShipmentEntity, shipment.Id, "Confirmed", after: new
        {
            shipment.Number, ShipmentNumber = number, Date = date, Cost = shipment.Lines.Sum(l => l.CostAmount), Value = shipment.Lines.Sum(l => l.Amount),
        });
        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipment.Id, cancellationToken);
    }

    /// <summary>The DD250 shipment number: the prefix and the next of four digits on the contract (or the order), "Z" on the final shipment.</summary>
    private async Task<string> ShipmentNumberAsync(IWorkbenchDbContext db, SalesOrder order, bool final, CancellationToken ct)
    {
        var prefix = new string((options.Value.ShipmentNumberPrefix ?? "").ToUpperInvariant().Where(char.IsAsciiLetterUpper).ToArray());
        prefix = (prefix + "SHP")[..3];
        var orderIds = order.ContractId is { } cid
            ? await db.Set<SalesOrder>().Where(o => o.ContractId == cid).Select(o => o.Id).ToListAsync(ct)
            : [order.Id];
        var used = await db.Set<Shipment>().Where(s => orderIds.Contains(s.SalesOrderId) && s.ShipmentNumber != null).Select(s => s.ShipmentNumber!).ToListAsync(ct);
        var next = used.Select(n => n.Length >= 7 && int.TryParse(n[3..7], out var v) ? v : 0).DefaultIfEmpty(0).Max() + 1;
        return $"{prefix}{next:D4}{(final ? "Z" : "")}";
    }

    public async Task<ShipmentDto> CancelShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var shipment = await LoadAsync(db, shipmentId, cancellationToken);
        if (shipment.Status != ShipmentStatus.Packing)
        {
            throw new InvalidOperationException(shipment.Status == ShipmentStatus.Confirmed
                ? $"{shipment.Number} has shipped; record a return instead."
                : $"{shipment.Number} is already cancelled.");
        }

        var pickLineIds = shipment.Lines.Where(l => l.PickListLineId != null).Select(l => l.PickListLineId!.Value).ToList();
        var pickIds = await db.Set<PickListLine>().Where(l => pickLineIds.Contains(l.Id)).Select(l => l.PickListId).Distinct().ToListAsync(cancellationToken);
        foreach (var p in await db.Set<PickList>().Where(p => pickIds.Contains(p.Id) && p.Status == PickListStatus.Packed).ToListAsync(cancellationToken))
        {
            p.Status = PickListStatus.Open;
        }

        shipment.Status = ShipmentStatus.Cancelled;
        audit.Record(db, SalesRules.ShipmentEntity, shipment.Id, "Cancelled");
        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipment.Id, cancellationToken);
    }

    private static void EnsureShippable(SalesOrder order)
    {
        if (order.Status is not (SalesOrderStatus.Released or SalesOrderStatus.PartlyShipped))
        {
            throw new InvalidOperationException(order.Status == SalesOrderStatus.Draft
                ? $"{order.Number} is a draft; release it first."
                : $"{order.Number} is {SalesOrderService.Label(order.Status)}; nothing more ships on it.");
        }

        if (order.IsHeld)
        {
            throw new InvalidOperationException($"{order.Number} is on {(order.OnCreditHold ? "credit hold" : "hold")}: {order.HoldReason}");
        }
    }

    // ---- Paperwork and billing ----

    public async Task<Dd250Dto> GetDd250Async(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return (await ShippingDocuments.LoadAsync(db, shipmentId, cancellationToken)).Dd250;
    }

    public async Task<ShippingFile> GetDd250PdfAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var dd250 = (await ShippingDocuments.LoadAsync(db, shipmentId, cancellationToken)).Dd250;
        return new ShippingFile($"DD250-{Safe(dd250.ContractNumber)}-{dd250.ShipmentNumber}.pdf", "application/pdf", Dd250Pdf.Write(dd250));
    }

    public async Task<WawfValidationDto> ValidateWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var paperwork = await ShippingDocuments.LoadAsync(db, shipmentId, cancellationToken);
        return WawfFile.Validate(paperwork, documentType.ToDomain());
    }

    public async Task<ShippingFile> ExportWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var file = await BuildWawfAsync(db, shipmentId, documentType.ToDomain(), cancellationToken);
        var report = await db.Set<WawfReceivingReport>().FirstOrDefaultAsync(r => r.ShipmentId == shipmentId, cancellationToken);
        if (report is null || report.Status is WawfStatus.Exported or WawfStatus.Rejected)
        {
            if (report is null)
            {
                report = new WawfReceivingReport { ShipmentId = shipmentId, FileName = file.FileName, ExportedBy = audit.Actor };
                db.Set<WawfReceivingReport>().Add(report);
            }

            report.DocumentType = documentType.ToDomain();
            report.Status = WawfStatus.Exported;
            report.FileName = file.FileName;
            report.ExportedBy = audit.Actor;
            report.ExportedAtUtc = UtcNow;
            report.UpdatedAtUtc = UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        return file;
    }

    private static async Task<ShippingFile> BuildWawfAsync(IWorkbenchDbContext db, Guid shipmentId, WawfDocumentType type, CancellationToken ct)
    {
        var paperwork = await ShippingDocuments.LoadAsync(db, shipmentId, ct);
        var validation = WawfFile.Validate(paperwork, type);
        if (!validation.IsValid)
        {
            throw new ArgumentException("The WAWF file can't be made yet: " + string.Join(" ", validation.Errors));
        }

        var content = WawfFile.Write(paperwork, type);
        return new ShippingFile($"WAWF-{type}-{Safe(paperwork.Dd250.ContractNumber)}-{paperwork.Dd250.ShipmentNumber}.xml", "application/xml", content);
    }

    public async Task<ShipmentDto> SubmitWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var type = documentType.ToDomain();
        var file = await BuildWawfAsync(db, shipmentId, type, cancellationToken);
        var report = await db.Set<WawfReceivingReport>().FirstOrDefaultAsync(r => r.ShipmentId == shipmentId, cancellationToken);
        if (report?.Status is WawfStatus.Submitted or WawfStatus.Accepted)
        {
            throw new InvalidOperationException($"The receiving report is already {report.Status.ToString().ToLowerInvariant()}.");
        }

        var shipmentNumber = await db.Set<Shipment>().Where(s => s.Id == shipmentId).Select(s => s.Number).FirstAsync(cancellationToken);
        var result = await submitter.SubmitAsync(new WawfSubmission(shipmentNumber, type, file.FileName, file.Content), cancellationToken);
        if (report is null)
        {
            report = new WawfReceivingReport { ShipmentId = shipmentId, FileName = file.FileName, ExportedBy = audit.Actor, ExportedAtUtc = UtcNow };
            db.Set<WawfReceivingReport>().Add(report);
        }

        report.DocumentType = type;
        report.FileName = file.FileName;
        report.Status = WawfStatus.Submitted;
        report.Reference = result.Reference;
        report.Message = result.Message;
        report.SubmittedBy = audit.Actor;
        report.SubmittedAtUtc = UtcNow;
        report.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.ShipmentEntity, shipmentId, "WawfSubmitted", after: new { Type = type.ToString(), Submitter = submitter.Name, result.Reference });
        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipmentId, cancellationToken);
    }

    public async Task<ShipmentDto> SetWawfStatusAsync(Guid shipmentId, SetWawfStatusRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var report = await db.Set<WawfReceivingReport>().FirstOrDefaultAsync(r => r.ShipmentId == shipmentId, cancellationToken)
            ?? throw new InvalidOperationException("Submit the receiving report first.");
        var status = request.Status.ToDomain();
        var allowed = (report.Status, status) switch
        {
            (WawfStatus.Submitted, WawfStatus.Accepted or WawfStatus.Rejected) => true,
            (WawfStatus.Exported, WawfStatus.Submitted) => true,
            (WawfStatus.Rejected, WawfStatus.Submitted) => true,
            _ => false,
        };
        if (!allowed)
        {
            throw new InvalidOperationException($"A receiving report that is {report.Status.ToString().ToLowerInvariant()} can't become {status.ToString().ToLowerInvariant()}.");
        }

        var message = PurchasingSupport.Optional(request.Message, "The message", 1000);
        if (status == WawfStatus.Rejected && message is null)
        {
            throw new ArgumentException("Say why the government rejected it.");
        }

        report.Status = status;
        report.Reference = PurchasingSupport.Optional(request.Reference, "The reference", 100) ?? report.Reference;
        report.Message = message;
        report.UpdatedAtUtc = UtcNow;
        if (status == WawfStatus.Submitted)
        {
            report.SubmittedBy = audit.Actor;
            report.SubmittedAtUtc = UtcNow;
        }

        audit.Record(db, SalesRules.ShipmentEntity, shipmentId, "WawfStatus", after: new { Status = status.ToString(), report.Reference }, reason: message);
        await db.SaveChangesAsync(cancellationToken);
        return await ShipmentAsync(db, shipmentId, cancellationToken);
    }

    public async Task<ShipmentDto> CreateInvoiceAsync(Guid shipmentId, CancellationToken cancellationToken = default)
    {
        if (invoicer is null)
        {
            throw new InvalidOperationException("Invoicing isn't available here.");
        }

        Guid invoiceId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var shipment = await LoadAsync(db, shipmentId, cancellationToken);
            if (shipment.Status != ShipmentStatus.Confirmed)
            {
                throw new InvalidOperationException("Confirm the shipment before invoicing it.");
            }

            if (shipment.InvoiceId is not null)
            {
                throw new InvalidOperationException("This shipment is already on an invoice.");
            }

            var order = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == shipment.SalesOrderId, cancellationToken);
            var (clins, _) = await SalesOrderService.CostContextAsync(db, order, cancellationToken);
            var billable = Billable(shipment, order, clins);
            if (order.ContractId is not { } contractId || billable.Count == 0)
            {
                throw new ArgumentException("Nothing on this shipment is on a firm-fixed-price CLIN. Cost-type material is billed with the contract's cost or T&M invoices.");
            }

            invoiceId = await invoicer.CreateAsync(contractId, shipment.Number, shipment.ShipmentNumber!, shipment.ShipDate!.Value,
                billable.Select(b => (b.ClinId, b.Description, b.Amount)).ToList(), cancellationToken);
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var shipment = await db.Set<Shipment>().FirstAsync(s => s.Id == shipmentId, cancellationToken);
            shipment.InvoiceId = invoiceId;
            audit.Record(db, SalesRules.ShipmentEntity, shipment.Id, "Invoiced", after: new { InvoiceId = invoiceId });
            await db.SaveChangesAsync(cancellationToken);
            return await ShipmentAsync(db, shipment.Id, cancellationToken);
        }
    }

    private sealed record BillableClin(Guid ClinId, string Description, decimal Amount);

    private static List<BillableClin> Billable(Shipment shipment, SalesOrder order, Dictionary<Guid, Clin> clins)
    {
        var lines = order.Lines.ToDictionary(l => l.Id);
        return shipment.Lines
            .Select(l => (Line: l, Order: lines[l.SalesOrderLineId]))
            .Where(x => x.Order.ClinId is { } c && clins.TryGetValue(c, out var clin) && clin.Pricing == ClinPricing.FirmFixedPrice)
            .GroupBy(x => x.Order.ClinId!.Value)
            .Select(g => new BillableClin(g.Key,
                string.Join(", ", g.GroupBy(x => x.Order.Id).Select(o => $"{o.Sum(x => x.Line.Quantity):0.####} × {o.First().Order.Description}")),
                Math.Round(g.Sum(x => x.Line.Quantity * x.Line.UnitPrice), 2, MidpointRounding.AwayFromZero)))
            .Where(b => b.Amount > 0)
            .ToList();
    }

    private static async Task<Shipment> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<Shipment>().Include(s => s.Lines).Include(s => s.Packages).FirstOrDefaultAsync(s => s.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Shipment", id);

    private static async Task<ShipmentDto> ShipmentAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var shipment = await db.Set<Shipment>().AsNoTracking().Include(s => s.Lines).Include(s => s.Packages).FirstOrDefaultAsync(s => s.Id == id, ct)
            ?? throw WorkbenchNotFoundException.For("Shipment", id);
        var summary = (await SalesQueries.ShipmentSummariesAsync(db, [shipment], ct))[0];
        var order = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).FirstAsync(o => o.Id == shipment.SalesOrderId, ct);
        var orderLines = order.Lines.ToDictionary(l => l.Id);
        var partIds = shipment.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var warehouses = await db.Set<Warehouse>().AsNoTracking().ToDictionaryAsync(w => w.Id, w => w.Code, ct);
        var binIds = shipment.Lines.Select(l => l.BinId).Distinct().ToList();
        var bins = await db.Set<Bin>().AsNoTracking().Where(b => binIds.Contains(b.Id)).ToDictionaryAsync(b => b.Id, b => b.Code, ct);
        var txIds = shipment.Lines.Where(l => l.InventoryTransactionId != null).Select(l => l.InventoryTransactionId!.Value).ToList();
        var txs = await db.Set<InventoryTransaction>().AsNoTracking().Where(t => txIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Number, ct);
        var je = shipment.JournalEntryId is { } j ? await db.Set<JournalEntry>().AsNoTracking().Where(e => e.Id == j).Select(e => e.Number).FirstOrDefaultAsync(ct) : null;
        var wawf = await db.Set<WawfReceivingReport>().AsNoTracking().FirstOrDefaultAsync(r => r.ShipmentId == id, ct);
        var (clins, _) = await SalesOrderService.CostContextAsync(db, order, ct);
        var lines = shipment.Lines.OrderBy(l => orderLines[l.SalesOrderLineId].LineNumber).ThenBy(l => l.Serial, StringComparer.Ordinal).ThenBy(l => l.Lot, StringComparer.Ordinal)
            .Select(l =>
            {
                var p = parts[l.PartId];
                var ol = orderLines[l.SalesOrderLineId];
                return new ShipmentLineDto(l.Id, l.SalesOrderLineId, ol.LineNumber, ol.ItemNumber, p.Id, p.Display, p.Description, p.UnitOfMeasure,
                    SalesRules.Location(warehouses.GetValueOrDefault(l.WarehouseId) ?? "", bins.GetValueOrDefault(l.BinId) ?? ""), l.Lot, l.Serial, l.Ownership.ToContract(),
                    l.Quantity, l.UnitPrice, l.Amount, l.PackageNumber, l.UnitCost, l.CostAmount, l.ChargedToWbs,
                    l.InventoryTransactionId is { } t ? txs.GetValueOrDefault(t) : null);
            }).ToList();
        var packages = shipment.Packages.OrderBy(p => p.Number).Select(p => new ShipmentPackageDto(p.Id, p.Number, p.Weight, p.Length, p.Width, p.Height, p.TrackingNumber)).ToList();
        var canInvoice = shipment.Status == ShipmentStatus.Confirmed && shipment.InvoiceId is null && order.ContractId is not null && Billable(shipment, order, clins).Count > 0;
        return new ShipmentDto(summary, shipment.BillOfLading, shipment.ShipToName, shipment.ShipToAddress, shipment.ShipToCode, shipment.Notes, je,
            shipment.CreatedBy, shipment.CreatedAtUtc, shipment.ConfirmedBy, shipment.ConfirmedAtUtc, lines, packages,
            wawf is null ? null : new WawfReportDto(wawf.Id, wawf.DocumentType.ToContract(), wawf.Status.ToContract(), wawf.FileName, wawf.Reference, wawf.Message,
                wawf.ExportedBy, wawf.ExportedAtUtc, wawf.SubmittedBy, wawf.SubmittedAtUtc),
            canInvoice);
    }

    private static async Task<Dictionary<Guid, string>> ProjectCodesAsync(IWorkbenchDbContext db, IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var list = ids.OfType<Guid>().Distinct().ToList();
        return list.Count == 0 ? [] : await db.Set<Project>().AsNoTracking().Where(p => list.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
    }

    private static string Safe(string s) => new(s.Where(char.IsAsciiLetterOrDigit).ToArray());
}

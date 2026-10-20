using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Application.Purchasing;
using Workbench.Contracts;
using Workbench.Contracts.Materials;
using Workbench.Domain.Materials;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Materials;

/// <summary>Sales order entry: customers' orders for parts, by contract CLIN or project, released to MRP and the floor.</summary>
public sealed class SalesOrderService(IWorkbenchDbContextFactory dbFactory, AuditRecorder audit, TimeProvider clock) : ISalesOrderService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public async Task<IReadOnlyList<SalesOrderSummaryDto>> ListAsync(SalesOrderQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (query.Status is { } s)
        {
            var status = s.ToDomain();
            q = q.Where(o => o.Status == status);
        }

        if (query.OpenOnly)
        {
            q = q.Where(o => o.Status == SalesOrderStatus.Draft || o.Status == SalesOrderStatus.Released || o.Status == SalesOrderStatus.PartlyShipped || o.Status == SalesOrderStatus.Shipped);
        }

        if (query.CustomerId is { } c)
        {
            q = q.Where(o => o.CustomerId == c);
        }

        if (query.ContractId is { } k)
        {
            q = q.Where(o => o.ContractId == k);
        }

        var orders = await q.OrderByDescending(o => o.Number).Take(1000).ToListAsync(cancellationToken);
        var rows = await SalesQueries.SummariesAsync(db, orders, cancellationToken);
        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var t = query.Search.Trim();
            rows = rows.Where(r => r.Number.Contains(t, StringComparison.OrdinalIgnoreCase) || r.CustomerName.Contains(t, StringComparison.OrdinalIgnoreCase)
                || (r.ContractNumber ?? "").Contains(t, StringComparison.OrdinalIgnoreCase) || (r.CustomerReference ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)
                || (r.ProjectCode ?? "").Contains(t, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return rows;
    }

    public async Task<SalesOrderDto> GetAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, salesOrderId, cancellationToken);
    }

    public async Task<SalesOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var customers = await db.Set<Customer>().AsNoTracking().OrderBy(c => c.Name).Select(c => new MaterialOption(c.Id, c.Code, c.Name)).ToListAsync(cancellationToken);
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => c.Status != ContractStatus.Closed).OrderBy(c => c.ContractNumber).ToListAsync(cancellationToken);
        var contractIds = contracts.Select(c => c.Id).ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => contractIds.Contains(c.ContractId) && c.IsExercised).OrderBy(c => c.Order).ThenBy(c => c.Number).ToListAsync(cancellationToken);
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.Status == ProjectStatus.Active).OrderBy(p => p.Code)
            .Select(p => new MaterialOption(p.Id, p.Code, p.Name)).ToListAsync(cancellationToken);
        var parts = await db.Set<Part>().AsNoTracking().Where(p => p.IsActive).OrderBy(p => p.Number).ThenBy(p => p.Revision).ToListAsync(cancellationToken);
        var leafWbs = await ChargeCodesAsync(db, cancellationToken);
        return new SalesOptionsDto(
            customers,
            contracts.Select(c => new SalesContractOption(c.Id, c.ContractNumber, c.Name, c.CustomerId, c.Type.ToString(),
                clins.Where(l => l.ContractId == c.Id).Select(l => new SalesClinOption(l.Id, l.ContractId, l.Number, l.Description, l.Pricing.ToString(),
                    SalesRules.IsCostType(l.Pricing), l.CeilingValue, l.PeriodEnd)).ToList())).ToList(),
            projects,
            leafWbs,
            parts.Select(p => new SalesPartOption(p.Id, p.Display, p.Description, p.UnitOfMeasure, p.Tracking.ToContract(), p.StandardCost)).ToList());
    }

    private static async Task<List<MaterialChargeOption>> ChargeCodesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var projects = await db.Set<Project>().AsNoTracking().Where(p => p.Status == ProjectStatus.Active).ToDictionaryAsync(p => p.Id, ct);
        var projectIds = projects.Keys.ToList();
        var wbs = await db.Set<WbsElement>().AsNoTracking().Where(w => projectIds.Contains(w.ProjectId) && w.IsOpen && w.IsChargeable && w.Kind == ChargeKind.Direct).ToListAsync(ct);
        var parents = wbs.Where(w => w.ParentId != null).Select(w => w.ParentId!.Value).ToHashSet();
        var all = await db.Set<WbsElement>().AsNoTracking().Where(w => w.ParentId != null && projectIds.Contains(w.ProjectId)).Select(w => w.ParentId!.Value).ToListAsync(ct);
        parents.UnionWith(all);
        return wbs.Where(w => !parents.Contains(w.Id)).OrderBy(w => projects[w.ProjectId].Code, StringComparer.Ordinal).ThenBy(w => w.Code, StringComparer.Ordinal)
            .Select(w => new MaterialChargeOption(w.Id, w.ProjectId, projects[w.ProjectId].Code, w.Code, w.Name)).ToList();
    }

    public async Task<SalesOrderDto> CreateAsync(SaveSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = new SalesOrder
        {
            Number = await SalesRules.NextNumberAsync(db.Set<SalesOrder>(), q => q.Select(o => o.Number), "SO-", cancellationToken),
            Status = SalesOrderStatus.Draft,
            CreatedBy = audit.Actor,
            CreatedAtUtc = UtcNow,
        };
        await ApplyHeaderAsync(db, order, request, cancellationToken);
        await ReplaceLinesAsync(db, order, request.Lines ?? [], cancellationToken);
        db.Set<SalesOrder>().Add(order);
        audit.Record(db, SalesRules.OrderEntity, order.Id, "Created", after: new { order.Number, order.CustomerId, order.ContractId, Lines = order.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> UpdateAsync(Guid salesOrderId, SaveSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status is SalesOrderStatus.Shipped or SalesOrderStatus.Closed or SalesOrderStatus.Cancelled)
        {
            throw new InvalidOperationException($"{order.Number} is {Label(order.Status)}; it can't change.");
        }

        var before = new { order.CustomerId, order.ContractId, order.ProjectId, order.WbsElementId, order.ShipToCode, Lines = order.Lines.Count };
        if (order.Status != SalesOrderStatus.Draft && (request.CustomerId != order.CustomerId || request.ContractId != order.ContractId || request.ProjectId != order.ProjectId))
        {
            throw new InvalidOperationException("The customer, contract and project can't change once the order is released.");
        }

        await ApplyHeaderAsync(db, order, request, cancellationToken);
        if (order.Status == SalesOrderStatus.Draft)
        {
            await ReplaceLinesAsync(db, order, request.Lines ?? [], cancellationToken);
        }

        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, "Updated", before, new { order.CustomerId, order.ContractId, order.ProjectId, order.WbsElementId, order.ShipToCode, Lines = order.Lines.Count });
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> AddLinesFromClinsAsync(Guid salesOrderId, AddClinLinesRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("Choose the CLINs to add.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status != SalesOrderStatus.Draft)
        {
            throw new InvalidOperationException("Lines are added while the order is a draft.");
        }

        if (order.ContractId is not { } contractId)
        {
            throw new ArgumentException("Choose the order's contract first; its CLINs become the lines.");
        }

        var ids = request.Lines.Select(l => l.ClinId).ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, cancellationToken);
        var lines = new List<SaveSalesOrderLineRequest>();
        foreach (var l in request.Lines)
        {
            if (!clins.TryGetValue(l.ClinId, out var clin) || clin.ContractId != contractId)
            {
                throw new ArgumentException("A CLIN isn't on the order's contract.");
            }

            if (!clin.IsExercised)
            {
                throw new ArgumentException($"CLIN {clin.Number} is an unexercised option.");
            }

            var quantity = MaterialRules.Quantity(l.Quantity, $"CLIN {clin.Number} quantity");
            var price = l.UnitPrice ?? Math.Round(clin.CeilingValue / quantity, 4, MidpointRounding.AwayFromZero);
            lines.Add(new SaveSalesOrderLineRequest(l.PartId, quantity, price, l.NeedDate ?? clin.PeriodEnd, clin.Description, clin.Id, clin.Number));
        }

        var existing = order.Lines.OrderBy(x => x.LineNumber).Select(x => new SaveSalesOrderLineRequest(x.PartId, x.QuantityOrdered, x.UnitPrice, x.NeedDate, x.Description, x.ClinId,
            x.ItemNumber, x.ShipToName, x.ShipToAddress, x.ShipToCode, x.StockNumber, x.UidRequired)).ToList();
        await ReplaceLinesAsync(db, order, [.. existing, .. lines], cancellationToken);
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, "LinesFromClins", after: new { Clins = lines.Select(l => l.ItemNumber).ToList() });
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> ReleaseAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status != SalesOrderStatus.Draft)
        {
            throw new InvalidOperationException($"{order.Number} is already {Label(order.Status)}.");
        }

        if (order.IsHeld)
        {
            throw new InvalidOperationException($"{order.Number} is on {(order.OnCreditHold ? "credit hold" : "hold")}; clear the hold to release it.");
        }

        if (order.Lines.Count == 0)
        {
            throw new ArgumentException("Add at least one line before releasing the order.");
        }

        var (clins, contract) = await CostContextAsync(db, order, cancellationToken);
        if (order.Lines.Any(l => SalesRules.ChargesWbs(l, l.ClinId is { } c ? clins.GetValueOrDefault(c) : null, contract)) && order.WbsElementId is null)
        {
            throw new ArgumentException("Cost-type lines are charged to a WBS element when they ship; choose the order's WBS element.");
        }

        order.Status = SalesOrderStatus.Released;
        order.ReleasedBy = audit.Actor;
        order.ReleasedAtUtc = UtcNow;
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, "Released", after: new { Total = order.Lines.Sum(l => l.Amount) });
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> SetHoldAsync(Guid salesOrderId, SetHoldRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status is SalesOrderStatus.Closed or SalesOrderStatus.Cancelled)
        {
            throw new InvalidOperationException($"{order.Number} is {Label(order.Status)}.");
        }

        var reason = PurchasingSupport.Optional(request.Reason, "The reason", 500);
        if ((request.OnHold || request.OnCreditHold) && reason is null)
        {
            throw new ArgumentException("Say why the order is on hold.");
        }

        var before = new { order.OnHold, order.OnCreditHold, order.HoldReason };
        order.OnHold = request.OnHold;
        order.OnCreditHold = request.OnCreditHold;
        order.HoldReason = order.IsHeld ? reason : null;
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, order.IsHeld ? "Held" : "HoldCleared", before, new { order.OnHold, order.OnCreditHold, order.HoldReason }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> CloseAsync(Guid salesOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status is SalesOrderStatus.Draft or SalesOrderStatus.Closed or SalesOrderStatus.Cancelled)
        {
            throw new InvalidOperationException(order.Status == SalesOrderStatus.Draft ? "Cancel a draft instead of closing it." : $"{order.Number} is already {Label(order.Status)}.");
        }

        if (await db.Set<Shipment>().AnyAsync(s => s.SalesOrderId == order.Id && s.Status == ShipmentStatus.Packing, cancellationToken))
        {
            throw new InvalidOperationException("A shipment is still being packed; confirm or cancel it first.");
        }

        await CancelOpenPickListsAsync(db, order.Id, cancellationToken);
        var shortClosed = order.Lines.Where(l => l.QuantityOpen > 0).Sum(l => l.QuantityOpen);
        foreach (var l in order.Lines)
        {
            l.IsClosed = true;
        }

        order.Status = SalesOrderStatus.Closed;
        order.ClosedAtUtc = UtcNow;
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, "Closed", after: new { ShortClosed = shortClosed });
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    public async Task<SalesOrderDto> CancelAsync(Guid salesOrderId, CancelSalesOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reason = PurchasingSupport.Required(request.Reason, "A reason", 500);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var order = await LoadAsync(db, salesOrderId, cancellationToken);
        if (order.Status is not (SalesOrderStatus.Draft or SalesOrderStatus.Released))
        {
            throw new InvalidOperationException($"{order.Number} is {Label(order.Status)}; only orders nothing has shipped on can be cancelled (close it instead).");
        }

        if (await db.Set<Shipment>().AnyAsync(s => s.SalesOrderId == order.Id && s.Status != ShipmentStatus.Cancelled, cancellationToken))
        {
            throw new InvalidOperationException("The order has a shipment; cancel the shipment first.");
        }

        await CancelOpenPickListsAsync(db, order.Id, cancellationToken);
        order.Status = SalesOrderStatus.Cancelled;
        order.CancelReason = reason;
        order.ClosedAtUtc = UtcNow;
        order.UpdatedAtUtc = UtcNow;
        audit.Record(db, SalesRules.OrderEntity, order.Id, "Cancelled", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await SalesQueries.OrderAsync(db, order.Id, cancellationToken);
    }

    private static async Task CancelOpenPickListsAsync(IWorkbenchDbContext db, Guid orderId, CancellationToken ct)
    {
        foreach (var p in await db.Set<PickList>().Where(p => p.SalesOrderId == orderId && p.Status == PickListStatus.Open).ToListAsync(ct))
        {
            p.Status = PickListStatus.Cancelled;
        }
    }

    internal static string Label(SalesOrderStatus s) => s switch
    {
        SalesOrderStatus.PartlyShipped => "partly shipped",
        _ => s.ToString().ToLowerInvariant(),
    };

    private static async Task<SalesOrder> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<SalesOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Sales order", id);

    internal static async Task<(Dictionary<Guid, Clin> Clins, Contract? Contract)> CostContextAsync(IWorkbenchDbContext db, SalesOrder order, CancellationToken ct)
    {
        var clinIds = order.Lines.Where(l => l.ClinId != null).Select(l => l.ClinId!.Value).Distinct().ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var contract = order.ContractId is { } cid ? await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) : null;
        return (clins, contract);
    }

    private async Task ApplyHeaderAsync(IWorkbenchDbContext db, SalesOrder order, SaveSalesOrderRequest r, CancellationToken ct)
    {
        if (!await db.Set<Customer>().AnyAsync(c => c.Id == r.CustomerId, ct))
        {
            throw new ArgumentException("Choose the customer.");
        }

        Contract? contract = null;
        if (r.ContractId is { } cid)
        {
            contract = await db.Set<Contract>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid, ct) ?? throw new ArgumentException("That contract wasn't found.");
            if (contract.CustomerId != r.CustomerId)
            {
                throw new ArgumentException($"Contract {contract.ContractNumber} is with another customer.");
            }
        }

        Project? project = null;
        if (r.ProjectId is { } pid)
        {
            project = await db.Set<Project>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == pid, ct) ?? throw new ArgumentException("That project wasn't found.");
            if (contract is not null && project.ContractId != contract.Id)
            {
                throw new ArgumentException($"Project {project.Code} isn't on contract {contract.ContractNumber}.");
            }
        }

        if (r.WbsElementId is { } wid)
        {
            var wbs = await StockEngine.ChargeableWbsAsync(db, wid, direct: true, ct);
            if (project is null || wbs.ProjectId != project.Id)
            {
                throw new ArgumentException($"{wbs.Code} isn't on the order's project.");
            }
        }

        order.CustomerId = r.CustomerId;
        order.ContractId = r.ContractId;
        order.ProjectId = r.ProjectId;
        order.WbsElementId = r.WbsElementId;
        order.CompanyId = contract?.CompanyId ?? project?.CompanyId ?? order.CompanyId;
        order.OrderDate = r.OrderDate;
        order.CustomerReference = PurchasingSupport.Optional(r.CustomerReference, "The customer reference", 64);
        order.ShipToName = PurchasingSupport.Optional(r.ShipToName, "The ship-to name", 200);
        order.ShipToAddress = PurchasingSupport.Optional(r.ShipToAddress, "The ship-to address", 500);
        order.ShipToCode = SalesRules.Code(r.ShipToCode, "The ship-to code");
        order.MarkForCode = SalesRules.Code(r.MarkForCode, "The mark-for code");
        order.IssuedByCode = SalesRules.Code(r.IssuedByCode, "The issued-by code");
        order.InspectByCode = SalesRules.Code(r.InspectByCode, "The inspect-by code");
        order.AcceptByCode = SalesRules.Code(r.AcceptByCode, "The accept-by code");
        order.InspectionPoint = r.InspectionPoint.ToDomain();
        order.AcceptancePoint = r.AcceptancePoint.ToDomain();
        order.Notes = PurchasingSupport.Optional(r.Notes, "The notes", 2000);
        order.UpdatedAtUtc = UtcNow;
    }

    private static async Task ReplaceLinesAsync(IWorkbenchDbContext db, SalesOrder order, IReadOnlyList<SaveSalesOrderLineRequest> lines, CancellationToken ct)
    {
        var partIds = lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var clinIds = lines.Where(l => l.ClinId != null).Select(l => l.ClinId!.Value).Distinct().ToList();
        var clins = await db.Set<Clin>().AsNoTracking().Where(c => clinIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        foreach (var old in order.Lines.ToList())
        {
            order.Lines.Remove(old);
            db.Set<SalesOrderLine>().Remove(old);
        }

        var n = 0;
        foreach (var l in lines)
        {
            if (!parts.TryGetValue(l.PartId, out var part) || !part.IsActive)
            {
                throw new ArgumentException("Choose an active part on every line.");
            }

            Clin? clin = null;
            if (l.ClinId is { } cid && (!clins.TryGetValue(cid, out clin) || clin.ContractId != order.ContractId))
            {
                throw new ArgumentException("A line's CLIN isn't on the order's contract.");
            }

            var quantity = MaterialRules.Quantity(l.Quantity, $"Line {n + 1} quantity");
            if (part.Tracking == TrackingMode.Serial && quantity != decimal.Truncate(quantity))
            {
                throw new ArgumentException($"{part.Display} is serial tracked; order whole units.");
            }

            var price = MaterialRules.Cost(l.UnitPrice, $"Line {n + 1} unit price");
            var item = PurchasingSupport.Optional(l.ItemNumber, "The item number", 16)?.ToUpperInvariant() ?? clin?.Number;
            order.Lines.Add(new SalesOrderLine
            {
                SalesOrderId = order.Id,
                LineNumber = ++n,
                PartId = part.Id,
                ClinId = clin?.Id,
                ItemNumber = item,
                Description = PurchasingSupport.Optional(l.Description, "The description", 500) ?? part.Description,
                QuantityOrdered = quantity,
                UnitPrice = price,
                NeedDate = l.NeedDate,
                ShipToName = PurchasingSupport.Optional(l.ShipToName, "The line ship-to name", 200),
                ShipToAddress = PurchasingSupport.Optional(l.ShipToAddress, "The line ship-to address", 500),
                ShipToCode = SalesRules.Code(l.ShipToCode, "The line ship-to code"),
                StockNumber = PurchasingSupport.Optional(l.StockNumber, "The stock number", 32),
                UidRequired = l.UidRequired ?? (part.Tracking == TrackingMode.Serial && price >= SalesRules.UidThreshold),
            });
        }

        if (order.Lines.FirstOrDefault(l => l.UidRequired && parts[l.PartId].Tracking != TrackingMode.Serial) is { } bad)
        {
            throw new ArgumentException($"Line {bad.LineNumber}: UID-marked items need a serial-tracked part (the serial number is part of the unique item identifier).");
        }

        foreach (var line in order.Lines)
        {
            db.Set<SalesOrderLine>().Add(line);
        }
    }
}

/// <summary>Reads and DTO mapping shared by the sales order and shipping services.</summary>
internal static class SalesQueries
{
    public static async Task<List<SalesOrderSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<SalesOrder> orders, CancellationToken ct)
    {
        var customerIds = orders.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var contractIds = orders.Where(o => o.ContractId != null).Select(o => o.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var projectIds = orders.Where(o => o.ProjectId != null).Select(o => o.ProjectId!.Value).Distinct().ToList();
        var projects = await db.Set<Project>().AsNoTracking().Where(p => projectIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Code, ct);
        return orders.Select(o => new SalesOrderSummaryDto(o.Id, o.Number, o.CustomerId, customers.GetValueOrDefault(o.CustomerId) ?? "", o.ContractId,
            o.ContractId is { } c ? contracts.GetValueOrDefault(c) : null, o.ProjectId, o.ProjectId is { } p ? projects.GetValueOrDefault(p) : null,
            o.CustomerReference, o.OrderDate, o.Lines.Where(l => l.QuantityOpen > 0).Select(l => (DateOnly?)l.NeedDate).Min(), o.Status.ToContract(), o.OnHold, o.OnCreditHold,
            o.Lines.Sum(l => l.Amount), o.Lines.Sum(l => Math.Round(l.QuantityShipped * l.UnitPrice, 2, MidpointRounding.AwayFromZero)), o.Lines.Count)).ToList();
    }

    public static async Task<SalesOrderDto> OrderAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var order = await db.Set<SalesOrder>().AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Sales order", id);
        var summary = (await SummariesAsync(db, [order], ct))[0];
        var partIds = order.Lines.Select(l => l.PartId).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var (clins, contract) = await SalesOrderService.CostContextAsync(db, order, ct);
        var lineIds = order.Lines.Select(l => l.Id).ToList();
        var onPick = await db.Set<PickListLine>().AsNoTracking().Where(l => lineIds.Contains(l.SalesOrderLineId))
            .Join(db.Set<PickList>().AsNoTracking().Where(p => p.Status == PickListStatus.Open), l => l.PickListId, p => p.Id, (l, p) => l).ToListAsync(ct);
        var packing = await db.Set<ShipmentLine>().AsNoTracking().Where(l => lineIds.Contains(l.SalesOrderLineId))
            .Join(db.Set<Shipment>().AsNoTracking().Where(s => s.Status == ShipmentStatus.Packing), l => l.ShipmentId, s => s.Id, (l, s) => l).ToListAsync(ct);
        var balances = await db.Set<InventoryBalance>().AsNoTracking().Where(b => partIds.Contains(b.PartId) && b.Quantity > 0).ToListAsync(ct);
        var reserved = await SalesReservations.LoadAsync(db, partIds, null, ct);
        decimal Available(Guid partId) => balances.Where(b => b.PartId == partId && b.Ownership != InventoryOwnership.GovernmentFurnished && SalesRules.Usable(b.Ownership, b.OwnerProjectId, order.ProjectId))
            .Sum(b => Math.Max(0, b.Quantity - reserved.GetValueOrDefault(SalesReservations.Of(b))));
        var wbsCode = order.WbsElementId is { } w ? await db.Set<WbsElement>().AsNoTracking().Where(x => x.Id == w).Select(x => x.Code).FirstOrDefaultAsync(ct) : null;
        var lines = order.Lines.OrderBy(l => l.LineNumber).Select(l =>
        {
            var p = parts[l.PartId];
            var picked = onPick.Where(x => x.SalesOrderLineId == l.Id).Sum(x => x.PickedAtUtc is null ? x.Quantity : x.QuantityPicked)
                + packing.Where(x => x.SalesOrderLineId == l.Id).Sum(x => x.Quantity);
            return new SalesOrderLineDto(l.Id, l.LineNumber, p.Id, p.Display, p.Description, p.UnitOfMeasure, p.Tracking.ToContract(), l.ClinId, l.ItemNumber, l.Description,
                l.QuantityOrdered, l.QuantityShipped, picked, l.QuantityOpen, l.UnitPrice, l.Amount, l.NeedDate, l.ShipToName, l.ShipToAddress, l.ShipToCode, l.StockNumber,
                l.UidRequired, l.IsClosed, SalesRules.ChargesWbs(l, l.ClinId is { } c ? clins.GetValueOrDefault(c) : null, contract), Available(p.Id));
        }).ToList();
        var pickLists = await db.Set<PickList>().AsNoTracking().Include(p => p.Lines).Where(p => p.SalesOrderId == id).OrderByDescending(p => p.Number).ToListAsync(ct);
        var shipments = await db.Set<Shipment>().AsNoTracking().Include(s => s.Lines).Include(s => s.Packages).Where(s => s.SalesOrderId == id).OrderByDescending(s => s.Number).ToListAsync(ct);
        return new SalesOrderDto(summary, order.WbsElementId, wbsCode, order.HoldReason, order.ShipToName, order.ShipToAddress, order.ShipToCode, order.MarkForCode,
            order.IssuedByCode, order.InspectByCode, order.AcceptByCode, order.InspectionPoint.ToContract(), order.AcceptancePoint.ToContract(), order.Notes,
            order.CreatedBy, order.CreatedAtUtc, order.ReleasedBy, order.ReleasedAtUtc, order.CancelReason, lines,
            await PickSummariesAsync(db, pickLists, ct), await ShipmentSummariesAsync(db, shipments, ct));
    }

    public static async Task<List<PickListSummaryDto>> PickSummariesAsync(IWorkbenchDbContext db, IReadOnlyList<PickList> lists, CancellationToken ct)
    {
        var orderIds = lists.Select(p => p.SalesOrderId).Distinct().ToList();
        var orders = await db.Set<SalesOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var customerIds = orders.Values.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        return lists.Select(p => new PickListSummaryDto(p.Id, p.Number, p.SalesOrderId, orders[p.SalesOrderId].Number, customers.GetValueOrDefault(orders[p.SalesOrderId].CustomerId) ?? "",
            p.Status.ToContract(), p.Lines.Count, p.Lines.Count(l => l.PickedAtUtc != null), p.CreatedAtUtc)).ToList();
    }

    public static async Task<List<ShipmentSummaryDto>> ShipmentSummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Shipment> shipments, CancellationToken ct)
    {
        var orderIds = shipments.Select(s => s.SalesOrderId).Distinct().ToList();
        var orders = await db.Set<SalesOrder>().AsNoTracking().Where(o => orderIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id, ct);
        var customerIds = orders.Values.Select(o => o.CustomerId).Distinct().ToList();
        var customers = await db.Set<Customer>().AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.Name, ct);
        var contractIds = orders.Values.Where(o => o.ContractId != null).Select(o => o.ContractId!.Value).Distinct().ToList();
        var contracts = await db.Set<Contract>().AsNoTracking().Where(c => contractIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.ContractNumber, ct);
        var invoiceIds = shipments.Where(s => s.InvoiceId != null).Select(s => s.InvoiceId!.Value).ToList();
        var invoices = await db.Set<Domain.Billing.Invoice>().AsNoTracking().Where(i => invoiceIds.Contains(i.Id)).ToDictionaryAsync(i => i.Id, i => i.Number, ct);
        return shipments.Select(s =>
        {
            var o = orders[s.SalesOrderId];
            return new ShipmentSummaryDto(s.Id, s.Number, s.ShipmentNumber, o.Id, o.Number, customers.GetValueOrDefault(o.CustomerId) ?? "",
                o.ContractId is { } c ? contracts.GetValueOrDefault(c) : null, s.Status.ToContract(), s.ShipDate, s.Carrier, s.TrackingNumber, s.Packages.Count,
                s.Packages.Sum(p => p.Weight), s.Lines.Sum(l => l.Amount), s.IsFinal, s.InvoiceId, s.InvoiceId is { } i ? invoices.GetValueOrDefault(i) : null);
        }).ToList();
    }
}

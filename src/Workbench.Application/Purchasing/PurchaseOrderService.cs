using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Notifications;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;
using Workbench.Application.Integrations;

namespace Workbench.Application.Purchasing;

/// <summary>
/// Purchase orders: drafts entered by Finance or created from approved requisitions, issue, change orders (each a new
/// revision with its history), receiving with partial receipts and service acceptance, close and cancel. Open orders'
/// unbilled amounts are commitments; nothing here posts to the general ledger.
/// </summary>
public sealed class PurchaseOrderService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    INotificationPublisher notifications,
    TimeProvider clock,
    Customization.DocumentNumbers? documentNumbers = null) : IPurchaseOrderService
{
    private Customization.DocumentNumbers DocNumbers => documentNumbers ?? new(dbFactory, clock);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private string Me => access.Email;

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).AsQueryable();
        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(o => o.Status == s);
        }

        if (query.OpenOnly)
        {
            q = q.Where(o => o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived || o.Status == PurchaseOrderStatus.Received);
        }

        if (query.VendorId is { } vendorId)
        {
            q = q.Where(o => o.VendorId == vendorId);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(o => o.Number.Contains(term) || o.Lines.Any(l => l.Description.Contains(term)));
        }

        var orders = (await q.ToListAsync(cancellationToken)).OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Number).Take(1000).ToList();
        return await SummariesAsync(db, orders, cancellationToken);
    }

    public async Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var finance = await access.HasAsync(Permission.PurchaseOrdersManage, cancellationToken);
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines)
            .Where(o => o.Status == PurchaseOrderStatus.Issued || o.Status == PurchaseOrderStatus.PartiallyReceived).ToListAsync(cancellationToken);
        var mine = orders.Where(o => finance || ProcurementSupport.SplitEmails(o.ReceiverEmails).Contains(Me)).OrderBy(o => o.OrderDate).ThenBy(o => o.Number).ToList();
        return await SummariesAsync(db, mine, cancellationToken);
    }

    public async Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, purchaseOrderId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var reqs = await db.Set<PurchaseRequisition>().AsNoTracking().Include(r => r.Lines)
            .Where(r => r.Status == RequisitionStatus.Approved).OrderBy(r => r.ApprovedAtUtc).ToListAsync(cancellationToken);
        var lines = reqs.SelectMany(r => r.Lines.Where(l => l.PurchaseOrderLineId == null).OrderBy(l => l.LineNumber).Select(l => (Req: r, Line: l))).ToList();
        var lookups = await ProcurementSupport.Lookups.LoadAsync(db, lines.Select(x => x.Line.ProjectId), lines.Select(x => x.Line.WbsElementId),
            lines.Select(x => x.Line.AccountId), lines.Select(x => x.Line.OrgUnitId), lines.Select(x => x.Line.SuggestedVendorId), cancellationToken);
        return lines.Select(x => new OrderableRequisitionLineDto(x.Req.Id, x.Req.Number, x.Req.RequesterEmail,
            RequisitionService.LineDto(x.Line, lookups, new Dictionary<Guid, string>()))).ToList();
    }

    public async Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await NewOrderAsync(db, request.VendorId, request.OrderDate, request.Terms, request.ShipTo, request.Receivers, cancellationToken);
        po.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 2000);
        await ApplyLinesAsync(db, po, request.Lines, cancellationToken);
        db.Set<PurchaseOrder>().Add(po);
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Created", after: Snapshot(po));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default)
    {
        if (request.RequisitionIds is null || request.RequisitionIds.Count == 0)
        {
            throw new ArgumentException("Choose at least one approved requisition.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ids = request.RequisitionIds.Distinct().ToList();
        var reqs = await db.Set<PurchaseRequisition>().Include(r => r.Lines).Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        var missing = ids.FirstOrDefault(id => reqs.All(r => r.Id != id));
        if (missing != Guid.Empty)
        {
            throw WorkbenchNotFoundException.For("Requisition", missing);
        }

        var notApproved = reqs.FirstOrDefault(r => r.Status != RequisitionStatus.Approved);
        if (notApproved is not null)
        {
            throw new ArgumentException($"Requisition {notApproved.Number} is {notApproved.Status}; only approved requisitions can be ordered.");
        }

        var chosen = request.RequisitionLineIds?.ToHashSet();
        var lines = reqs.OrderBy(r => r.Number).SelectMany(r => r.Lines.OrderBy(l => l.LineNumber))
            .Where(l => l.PurchaseOrderLineId == null && (chosen is null || chosen.Contains(l.Id))).ToList();
        if (lines.Count == 0)
        {
            throw new ArgumentException("Those requisitions have no lines left to order.");
        }

        var po = await NewOrderAsync(db, request.VendorId, request.OrderDate, request.Terms, request.ShipTo, request.Receivers, cancellationToken);
        po.Notes = $"From {string.Join(", ", reqs.Where(r => lines.Any(l => l.RequisitionId == r.Id)).Select(r => r.Number))}";
        foreach (var rl in lines)
        {
            // Re-check the charging: WBS elements and accounts may have closed since approval.
            await ProcurementSupport.ValidateChargeAsync(db, $"{reqs.First(r => r.Id == rl.RequisitionId).Number} line {rl.LineNumber}",
                rl.WbsElementId, rl.OdcType, rl.AccountId, rl.OrgUnitId, null, cancellationToken);
            var line = new PurchaseOrderLine
            {
                PurchaseOrderId = po.Id,
                LineNumber = po.Lines.Count + 1,
                Description = rl.Description,
                Quantity = rl.Quantity,
                UnitPrice = rl.UnitCost,
                Amount = rl.Amount,
                ProjectId = rl.ProjectId,
                WbsElementId = rl.WbsElementId,
                OdcType = rl.OdcType,
                AccountId = rl.AccountId,
                OrgUnitId = rl.OrgUnitId,
                NeededBy = rl.NeededBy,
                RequisitionLineId = rl.Id,
            };
            po.Lines.Add(line);
            rl.PurchaseOrderLineId = line.Id;
        }

        po.Total = po.Lines.Sum(l => l.Amount);
        foreach (var req in reqs.Where(r => r.Lines.All(l => l.PurchaseOrderLineId != null)))
        {
            req.Status = RequisitionStatus.Ordered;
            audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Ordered", after: new { PurchaseOrder = po.Number });
        }

        db.Set<PurchaseOrder>().Add(po);
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "CreatedFromRequisitions", after: Snapshot(po));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (po.Status != PurchaseOrderStatus.Draft)
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; issued orders change through a change order.");
        }

        if (po.Lines.Any(l => l.RequisitionLineId != null))
        {
            throw new InvalidOperationException($"{po.Number} came from requisitions; issue it and use a change order, or cancel it.");
        }

        var before = Snapshot(po);
        var vendor = await PurchasingSupport.FindVendorAsync(db, request.VendorId, cancellationToken);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "ordering");
        po.VendorId = vendor.Id;
        po.OrderDate = request.OrderDate;
        po.Terms = PurchasingSupport.Optional(request.Terms, "Terms", 300) ?? $"Net {vendor.PaymentTermsDays}";
        po.ShipTo = PurchasingSupport.Optional(request.ShipTo, "Ship to", 500);
        po.Notes = PurchasingSupport.Optional(request.Notes, "Notes", 2000);
        po.ReceiverEmails = ProcurementSupport.JoinEmails(request.Receivers);
        db.Set<PurchaseOrderLine>().RemoveRange(po.Lines);
        po.Lines.Clear();
        await ApplyLinesAsync(db, po, request.Lines, cancellationToken);
        db.Set<PurchaseOrderLine>().AddRange(po.Lines);
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Updated", before, Snapshot(po));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (po.Status != PurchaseOrderStatus.Draft)
        {
            throw new InvalidOperationException($"{po.Number} is already {po.Status}.");
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, po.VendorId, cancellationToken);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "issuing orders");
        po.Status = PurchaseOrderStatus.Issued;
        po.IssuedBy = Me;
        po.IssuedAtUtc = UtcNow;
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Issued", after: new { po.Total, Vendor = vendor.Code });
        IntegrationOutbox.Raise(db, Domain.Integrations.IntegrationEventType.PurchaseOrderApproved, IntegrationEventPayloads.PurchaseOrder(po), Me, UtcNow);
        await db.SaveChangesAsync(cancellationToken);

        var reqLineIds = po.Lines.Where(l => l.RequisitionLineId != null).Select(l => l.RequisitionLineId!.Value).ToList();
        var requesters = await db.Set<PurchaseRequisitionLine>().AsNoTracking().Where(l => reqLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseRequisition>(), l => l.RequisitionId, r => r.Id, (l, r) => r.RequesterEmail).Distinct().ToListAsync(cancellationToken);
        await notifications.PublishAsync(requesters.Concat(ProcurementSupport.SplitEmails(po.ReceiverEmails)).Where(e => !PurchasingSupport.SameEmail(e, Me)),
            "purchasing.po_issued", $"Purchase order {po.Number} was issued to {vendor.Name}", $"{po.Total:C}", link: $"purchasing/orders/{po.Id}");
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason for the change order", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (!po.IsOpen)
        {
            throw new InvalidOperationException(po.Status == PurchaseOrderStatus.Draft
                ? $"{po.Number} is a draft; edit it directly."
                : $"{po.Number} is {po.Status}; it can't be changed.");
        }

        if (request!.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.");
        }

        var billed = await ProcurementSupport.BilledAsync(db, po.Lines.Select(l => l.Id).ToList(), cancellationToken);
        var changes = new List<string>();
        var totalBefore = po.Total;
        var kept = new HashSet<Guid>();
        var byId = po.Lines.ToDictionary(l => l.Id);
        var nextNumber = po.Lines.Select(l => l.LineNumber).DefaultIfEmpty(0).Max() + 1;
        var updated = new List<PurchaseOrderLine>();
        foreach (var input in request.Lines)
        {
            if (input.LineId is { } id)
            {
                if (!byId.TryGetValue(id, out var line) || !kept.Add(id))
                {
                    throw new ArgumentException("A change order line refers to a line that isn't on this order (or refers to it twice).");
                }

                var prefix = $"Line {line.LineNumber}";
                var charge = await ProcurementSupport.ValidateChargeAsync(db, prefix, input.WbsElementId, input.OdcType?.ToDomain(), input.AccountId, input.OrgUnitId, null, cancellationToken);
                var quantity = ProcurementSupport.Quantity(input.Quantity, $"{prefix}: quantity");
                var price = ProcurementSupport.UnitPrice(input.UnitPrice, $"{prefix}: unit price");
                var lineBilled = billed.GetValueOrDefault(line.Id);
                if (quantity < line.QuantityReceived || quantity < (lineBilled?.Quantity ?? 0))
                {
                    throw new ArgumentException($"{prefix}: the quantity can't go below what's been received ({Q(line.QuantityReceived)}) or billed ({Q(lineBilled?.Quantity ?? 0)}).");
                }

                if ((line.QuantityReceived > 0 || lineBilled is not null) && (charge.WbsElementId != line.WbsElementId || charge.AccountId != line.AccountId))
                {
                    throw new ArgumentException($"{prefix}: the charging can't change once the line has receipts or bills.");
                }

                var description = PurchasingSupport.Required(input.Description, $"{prefix}: description", 500);
                Diff(changes, prefix, "description", line.Description, description);
                Diff(changes, prefix, "quantity", Q(line.Quantity), Q(quantity));
                Diff(changes, prefix, "unit price", Q(line.UnitPrice), Q(price));
                Diff(changes, prefix, "charge", await ChargeLabelAsync(db, line.WbsElementId, line.AccountId, cancellationToken),
                    await ChargeLabelAsync(db, charge.WbsElementId, charge.AccountId, cancellationToken));
                Diff(changes, prefix, "needed by", line.NeededBy?.ToString("yyyy-MM-dd"), input.NeededBy?.ToString("yyyy-MM-dd"));
                line.Description = description;
                line.Quantity = quantity;
                line.UnitPrice = price;
                line.Amount = ProcurementSupport.Extend(quantity, price);
                line.ProjectId = charge.ProjectId;
                line.WbsElementId = charge.WbsElementId;
                line.OdcType = charge.OdcType;
                line.AccountId = charge.AccountId;
                line.OrgUnitId = charge.OrgUnitId;
                line.NeededBy = input.NeededBy;
                line.IsService = input.IsService;
                updated.Add(line);
            }
            else
            {
                var line = await NewLineAsync(db, po, nextNumber++, input, cancellationToken);
                db.Set<PurchaseOrderLine>().Add(line);
                updated.Add(line);
                changes.Add($"Line {line.LineNumber} added: {line.Description}, {Q(line.Quantity)} × {Q(line.UnitPrice)} = {line.Amount:N2}");
            }
        }

        foreach (var removed in po.Lines.Where(l => !kept.Contains(l.Id) && !updated.Contains(l)).ToList())
        {
            if (removed.QuantityReceived > 0 || billed.ContainsKey(removed.Id))
            {
                throw new ArgumentException($"Line {removed.LineNumber} has receipts or bills and can't be removed; reduce it to what's been received instead.");
            }

            if (removed.RequisitionLineId is { } rlId)
            {
                await ReleaseRequisitionLineAsync(db, rlId, cancellationToken);
            }

            changes.Add($"Line {removed.LineNumber} removed: {removed.Description} ({removed.Amount:N2})");
            po.Lines.Remove(removed);
            db.Set<PurchaseOrderLine>().Remove(removed);
        }

        foreach (var line in updated.Where(l => !po.Lines.Contains(l)))
        {
            po.Lines.Add(line);
        }

        var terms = PurchasingSupport.Optional(request.Terms, "Terms", 300) ?? po.Terms;
        var shipTo = PurchasingSupport.Optional(request.ShipTo, "Ship to", 500) ?? po.ShipTo;
        Diff(changes, "Order", "terms", po.Terms, terms);
        Diff(changes, "Order", "ship to", po.ShipTo, shipTo);
        po.Terms = terms;
        po.ShipTo = shipTo;
        po.Total = po.Lines.Sum(l => l.Amount);
        if (changes.Count == 0)
        {
            throw new ArgumentException("The change order doesn't change anything.");
        }

        po.Revision++;
        UpdateReceiptStatus(po);
        db.Set<PurchaseOrderRevision>().Add(new PurchaseOrderRevision
        {
            PurchaseOrderId = po.Id,
            Revision = po.Revision,
            Reason = reason,
            TotalBefore = totalBefore,
            TotalAfter = po.Total,
            Changes = string.Join('\n', changes),
            ChangedBy = Me,
            ChangedAtUtc = UtcNow,
        });
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "ChangeOrder", new { Revision = po.Revision - 1, Total = totalBefore }, new { po.Revision, po.Total, Changes = changes }, reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (po.Status is not (PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived))
        {
            throw new InvalidOperationException(po.Status == PurchaseOrderStatus.Received
                ? $"Everything on {po.Number} has been received; add a change order to receive more."
                : $"{po.Number} is {po.Status}; only issued orders can be received.");
        }

        if (request.Lines is null || request.Lines.Count(l => l.Quantity != 0) == 0)
        {
            throw new ArgumentException("Enter a quantity received on at least one line.");
        }

        if (request.ReceivedOn > DateOnly.FromDateTime(UtcNow).AddDays(1))
        {
            throw new ArgumentException("The receipt date can't be in the future.");
        }

        var receipt = new PurchaseReceipt
        {
            Number = await DocNumbers.NextAsync(Customization.DocumentNumberTypes.Receipt, new(Date: request.ReceivedOn), cancellationToken),
            PurchaseOrderId = po.Id,
            ReceivedOn = request.ReceivedOn,
            ReceivedBy = Me,
            Note = PurchasingSupport.Optional(request.Note, "The note", 1000),
            CreatedAtUtc = UtcNow,
        };
        foreach (var input in request.Lines.Where(l => l.Quantity != 0).GroupBy(l => l.PurchaseOrderLineId))
        {
            var line = po.Lines.FirstOrDefault(l => l.Id == input.Key) ?? throw new ArgumentException("A receipt line isn't on this order.");
            var quantity = ProcurementSupport.Quantity(input.Sum(i => i.Quantity), $"Line {line.LineNumber}: quantity received");
            if (line.QuantityReceived + quantity > line.Quantity)
            {
                throw new ArgumentException($"Line {line.LineNumber}: {Q(line.QuantityReceived + quantity)} would be more than the {Q(line.Quantity)} ordered ({Q(line.Quantity - line.QuantityReceived)} still open).");
            }

            line.QuantityReceived += quantity;
            receipt.Lines.Add(new PurchaseReceiptLine { ReceiptId = receipt.Id, PurchaseOrderLineId = line.Id, Quantity = quantity, IsAcceptance = line.IsService });
        }

        UpdateReceiptStatus(po);
        db.Set<PurchaseReceipt>().Add(receipt);
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Received", after: new
        {
            Receipt = receipt.Number,
            receipt.ReceivedOn,
            Lines = receipt.Lines.Select(l => new { po.Lines.First(x => x.Id == l.PurchaseOrderLineId).LineNumber, l.Quantity, l.IsAcceptance }),
            Status = po.Status.ToString(),
        });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (!po.IsOpen)
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; only issued orders can be closed.");
        }

        var before = po.Status.ToString();
        po.Status = PurchaseOrderStatus.Closed;
        po.ClosedAtUtc = UtcNow;
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Closed", new { Status = before }, new { Status = po.Status.ToString() });
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    public async Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await LoadAsync(db, purchaseOrderId, cancellationToken);
        if (po.Status is not (PurchaseOrderStatus.Draft or PurchaseOrderStatus.Issued))
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; orders with receipts are closed, not cancelled.");
        }

        if ((await ProcurementSupport.BilledAsync(db, po.Lines.Select(l => l.Id).ToList(), cancellationToken)).Count > 0)
        {
            throw new InvalidOperationException($"{po.Number} has been billed; close it instead.");
        }

        foreach (var line in po.Lines.Where(l => l.RequisitionLineId != null))
        {
            await ReleaseRequisitionLineAsync(db, line.RequisitionLineId!.Value, cancellationToken);
        }

        po.Status = PurchaseOrderStatus.Cancelled;
        po.CancelReason = reason;
        audit.Record(db, ProcurementSupport.OrderEntity, po.Id, "Cancelled", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, po.Id, cancellationToken);
    }

    // ---- Helpers ----

    private async Task<PurchaseOrder> NewOrderAsync(IWorkbenchDbContext db, Guid vendorId, DateOnly orderDate, string? terms, string? shipTo, IReadOnlyList<string>? receivers, CancellationToken ct)
    {
        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, ct);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "ordering");
        return new PurchaseOrder
        {
            Number = await DocNumbers.NextAsync(Customization.DocumentNumberTypes.PurchaseOrder, cancellationToken: ct),
            VendorId = vendor.Id,
            OrderDate = orderDate,
            Terms = PurchasingSupport.Optional(terms, "Terms", 300) ?? $"Net {vendor.PaymentTermsDays}",
            ShipTo = PurchasingSupport.Optional(shipTo, "Ship to", 500),
            ReceiverEmails = ProcurementSupport.JoinEmails(receivers),
            CreatedBy = Me,
            CreatedAtUtc = UtcNow,
        };
    }

    private async Task ApplyLinesAsync(IWorkbenchDbContext db, PurchaseOrder po, IReadOnlyList<SavePurchaseOrderLineRequest>? lines, CancellationToken ct)
    {
        if (lines is null || lines.Count == 0)
        {
            throw new ArgumentException("An order needs at least one line.");
        }

        if (lines.Count > 200)
        {
            throw new ArgumentException("An order can have at most 200 lines.");
        }

        foreach (var input in lines)
        {
            po.Lines.Add(await NewLineAsync(db, po, po.Lines.Count + 1, input, ct));
        }

        po.Total = po.Lines.Sum(l => l.Amount);
    }

    private static async Task<PurchaseOrderLine> NewLineAsync(IWorkbenchDbContext db, PurchaseOrder po, int number, SavePurchaseOrderLineRequest input, CancellationToken ct)
    {
        var prefix = $"Line {number}";
        var charge = await ProcurementSupport.ValidateChargeAsync(db, prefix, input.WbsElementId, input.OdcType?.ToDomain(), input.AccountId, input.OrgUnitId, null, ct);
        var quantity = ProcurementSupport.Quantity(input.Quantity, $"{prefix}: quantity");
        var price = ProcurementSupport.UnitPrice(input.UnitPrice, $"{prefix}: unit price");
        return new PurchaseOrderLine
        {
            PurchaseOrderId = po.Id,
            LineNumber = number,
            Description = PurchasingSupport.Required(input.Description, $"{prefix}: description", 500),
            Quantity = quantity,
            UnitPrice = price,
            Amount = ProcurementSupport.Extend(quantity, price),
            ProjectId = charge.ProjectId,
            WbsElementId = charge.WbsElementId,
            OdcType = charge.OdcType,
            AccountId = charge.AccountId,
            OrgUnitId = charge.OrgUnitId,
            NeededBy = input.NeededBy,
            IsService = input.IsService,
        };
    }

    private async Task ReleaseRequisitionLineAsync(IWorkbenchDbContext db, Guid requisitionLineId, CancellationToken ct)
    {
        var rl = await db.Set<PurchaseRequisitionLine>().FirstOrDefaultAsync(l => l.Id == requisitionLineId, ct);
        if (rl is null)
        {
            return;
        }

        rl.PurchaseOrderLineId = null;
        var req = await db.Set<PurchaseRequisition>().FirstAsync(r => r.Id == rl.RequisitionId, ct);
        if (req.Status == RequisitionStatus.Ordered)
        {
            req.Status = RequisitionStatus.Approved;
            audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Reopened", new { Status = "Ordered" }, new { Status = "Approved" });
        }
    }

    private static void UpdateReceiptStatus(PurchaseOrder po)
    {
        if (!po.IsOpen)
        {
            return;
        }

        po.Status = po.Lines.All(l => l.QuantityReceived >= l.Quantity) ? PurchaseOrderStatus.Received
            : po.Lines.Any(l => l.QuantityReceived > 0) ? PurchaseOrderStatus.PartiallyReceived
            : PurchaseOrderStatus.Issued;
    }

    private static void Diff(List<string> changes, string prefix, string what, string? before, string? after)
    {
        if ((before ?? "") != (after ?? ""))
        {
            changes.Add($"{prefix} {what}: {(string.IsNullOrEmpty(before) ? "(none)" : before)} → {(string.IsNullOrEmpty(after) ? "(none)" : after)}");
        }
    }

    private static async Task<string> ChargeLabelAsync(IWorkbenchDbContext db, Guid? wbs, Guid? account, CancellationToken ct) =>
        wbs is { } w ? await db.Set<Domain.ProjectAccounting.WbsElement>().AsNoTracking().Where(x => x.Id == w).Select(x => "WBS " + x.Code).FirstOrDefaultAsync(ct) ?? ""
        : account is { } a ? await db.Set<Domain.Accounting.Account>().AsNoTracking().Where(x => x.Id == a).Select(x => "account " + x.Number).FirstOrDefaultAsync(ct) ?? ""
        : "";

    private static string Q(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static async Task<PurchaseOrder> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct) =>
        await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Purchase order", id);

    private static object Snapshot(PurchaseOrder o) => new
    {
        o.Number,
        o.VendorId,
        o.OrderDate,
        o.Terms,
        o.ShipTo,
        o.Total,
        o.ReceiverEmails,
        Lines = o.Lines.Select(l => new { l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.WbsElementId, l.AccountId, l.OrgUnitId, l.RequisitionLineId }),
    };

    // ---- DTOs ----

    private static async Task<IReadOnlyList<PurchaseOrderSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<PurchaseOrder> orders, CancellationToken ct)
    {
        var vendorIds = orders.Select(o => o.VendorId).Distinct().ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var billed = await ProcurementSupport.BilledAsync(db, orders.SelectMany(o => o.Lines).Select(l => l.Id).ToList(), ct);
        return orders.Select(o => Summary(o, vendors[o.VendorId], billed)).ToList();
    }

    private static PurchaseOrderSummaryDto Summary(PurchaseOrder o, Vendor v, IReadOnlyDictionary<Guid, ProcurementSupport.Billed> billed) => new(
        o.Id, o.Number, o.VendorId, v.Code, v.Name, o.OrderDate, o.Status.ToContract(), o.Revision, o.Total,
        o.Lines.Sum(l => ProcurementSupport.Extend(l.QuantityReceived, l.UnitPrice)),
        o.Lines.Sum(l => billed.GetValueOrDefault(l.Id)?.Amount ?? 0),
        o.Lines.Sum(l => ProcurementSupport.Committed(o, l, billed.GetValueOrDefault(l.Id))));

    private async Task<PurchaseOrderDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var o = await db.Set<PurchaseOrder>().AsNoTracking().Include(x => x.Lines).FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw WorkbenchNotFoundException.For("Purchase order", id);
        var vendor = await PurchasingSupport.FindVendorAsync(db, o.VendorId, ct);
        var billed = await ProcurementSupport.BilledAsync(db, o.Lines.Select(l => l.Id).ToList(), ct);
        var lookups = await ProcurementSupport.Lookups.LoadAsync(db, o.Lines.Select(l => l.ProjectId), o.Lines.Select(l => l.WbsElementId),
            o.Lines.Select(l => l.AccountId), o.Lines.Select(l => l.OrgUnitId), [], ct);
        var reqLineIds = o.Lines.Where(l => l.RequisitionLineId != null).Select(l => l.RequisitionLineId!.Value).ToList();
        var reqNumbers = await db.Set<PurchaseRequisitionLine>().AsNoTracking().Where(l => reqLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseRequisition>(), l => l.RequisitionId, r => r.Id, (l, r) => new { l.Id, r.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var revisions = await db.Set<PurchaseOrderRevision>().AsNoTracking().Where(r => r.PurchaseOrderId == id).OrderBy(r => r.Revision).ToListAsync(ct);
        var receipts = await db.Set<PurchaseReceipt>().AsNoTracking().Include(r => r.Lines).Where(r => r.PurchaseOrderId == id).ToListAsync(ct);
        var lineIds = o.Lines.Select(l => l.Id).ToList();
        var billIds = await db.Set<ApBillLine>().AsNoTracking().Where(l => l.PurchaseOrderLineId != null && lineIds.Contains(l.PurchaseOrderLineId.Value))
            .Select(l => l.BillId).Distinct().ToListAsync(ct);
        var bills = await db.Set<ApBill>().AsNoTracking().Where(b => billIds.Contains(b.Id)).OrderBy(b => b.Number).ToListAsync(ct);
        var roles = access.IsAuthenticated ? await access.GetPermissionsAsync(ct) : PermissionSet.Empty;
        var receivers = ProcurementSupport.SplitEmails(o.ReceiverEmails);
        var lineById = o.Lines.ToDictionary(l => l.Id);
        return new PurchaseOrderDto(
            Summary(o, vendor, billed),
            o.Terms,
            o.ShipTo,
            o.Notes,
            receivers,
            o.CreatedBy,
            o.IssuedBy,
            o.IssuedAtUtc,
            o.CancelReason,
            o.Lines.OrderBy(l => l.LineNumber).Select(l =>
            {
                var b = billed.GetValueOrDefault(l.Id);
                return new PurchaseOrderLineDto(l.Id, l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.Amount,
                    l.ProjectId, lookups.Project(l.ProjectId), l.WbsElementId, lookups.WbsCode(l.WbsElementId), l.OdcType?.ToContract(),
                    l.AccountId, lookups.Account(l.AccountId), l.OrgUnitId, lookups.Org(l.OrgUnitId), l.NeededBy, l.IsService,
                    l.QuantityReceived, b?.Quantity ?? 0, b?.Amount ?? 0, ProcurementSupport.Committed(o, l, b),
                    l.RequisitionLineId, l.RequisitionLineId is { } r ? reqNumbers.GetValueOrDefault(r) : null);
            }).ToList(),
            revisions.Select(r => new PurchaseOrderRevisionDto(r.Revision, r.Reason, r.TotalBefore, r.TotalAfter, r.Changes.Split('\n'), r.ChangedBy, r.ChangedAtUtc)).ToList(),
            receipts.OrderBy(r => r.ReceivedOn).ThenBy(r => r.Number).Select(r => new ReceiptDto(r.Id, r.Number, r.ReceivedOn, r.ReceivedBy, r.Note,
                r.Lines.Select(l => new ReceiptLineDto(l.PurchaseOrderLineId, lineById.GetValueOrDefault(l.PurchaseOrderLineId)?.LineNumber ?? 0,
                    lineById.GetValueOrDefault(l.PurchaseOrderLineId)?.Description ?? "(removed)", l.Quantity, l.IsAcceptance)).OrderBy(l => l.LineNumber).ToList())).ToList(),
            bills.Select(b => ApBillService.Summary(b, vendor)).ToList(),
            o.Status == PurchaseOrderStatus.Draft && roles.Has(Permission.PurchaseOrdersManage),
            o.Status is PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived && (roles.Has(Permission.PurchaseOrdersManage) || receivers.Contains(Me)));
    }
}

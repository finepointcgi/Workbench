using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.Financials;
using Workbench.Contracts.Materials;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Accounting;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Materials;

/// <summary>
/// Puts goods received on part-linked purchase order lines into inventory, however the receipt was entered (Materials ›
/// Receiving, Purchasing › Receiving, the mobile receiving page or the API). Purchasing's receipt, quantities and
/// three-way match are unchanged; this adds the stock (at the PO price, or standard with a purchase price variance) and
/// the GL entry debiting inventory and crediting the received-not-invoiced clearing account the PO line charges.
/// Lot- and serial-tracked parts need their lots and serials, which only Materials › Receiving collects.
/// </summary>
public sealed class InventoryReceivingPurchaseOrderService(
    IPurchaseOrderService inner, IWorkbenchDbContextFactory dbFactory, MaterialsLedger ledger, InventoryReceiptContext context) : IPurchaseOrderService
{
    public async Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default)
    {
        var lineIds = (request?.Lines ?? []).Where(l => l.Quantity > 0).Select(l => l.PurchaseOrderLineId).Distinct().ToList();
        List<Planned> planned;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var links = lineIds.Count == 0 ? [] : await MrpData.LinksByPoLineAsync(db, lineIds, cancellationToken);
            if (links.Count == 0)
            {
                return await inner.ReceiveAsync(purchaseOrderId, request!, cancellationToken);
            }

            planned = await PlanAsync(db, purchaseOrderId, request!, links, cancellationToken);
        }

        var result = await inner.ReceiveAsync(purchaseOrderId, request!, cancellationToken);
        await StockAsync(purchaseOrderId, request!.ReceivedOn, planned, cancellationToken);
        return result;
    }

    private sealed record Planned(PurchaseOrderLine Line, MaterialPurchaseLink Link, decimal Quantity, Guid? WarehouseId, Guid? BinId, string? Lot, IReadOnlyList<string>? Serials);

    /// <summary>Checks everything the stock side needs before purchasing records the receipt (so a receipt never lands half done).</summary>
    private async Task<List<Planned>> PlanAsync(IWorkbenchDbContext db, Guid purchaseOrderId, ReceiveRequest request, Dictionary<Guid, MaterialPurchaseLink> links, CancellationToken ct)
    {
        var accounts = await ledger.AccountsAsync(db, ct);
        if (accounts is not null)
        {
            await GeneralLedger.OpenPeriodForAsync(db, request.ReceivedOn, ct);
        }

        var engine = await StockEngine.CreateAsync(db, ledger, ct);
        var linkedIds = links.Keys.ToList();
        var lines = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => l.PurchaseOrderId == purchaseOrderId && linkedIds.Contains(l.Id)).ToListAsync(ct);
        var result = new List<Planned>();
        foreach (var input in request.Lines.Where(l => l.Quantity > 0 && links.ContainsKey(l.PurchaseOrderLineId)).GroupBy(l => l.PurchaseOrderLineId))
        {
            var line = lines.FirstOrDefault(l => l.Id == input.Key);
            if (line is null)
            {
                continue; // Not on this order: purchasing refuses it.
            }

            if (accounts is not null && line.AccountId != accounts.ReceivedNotInvoiced)
            {
                throw new ArgumentException($"Line {line.LineNumber} is for a part but isn't charged to the inventory clearing account (received not invoiced); change the line's charging before receiving it into stock.");
            }

            var link = links[line.Id];
            var detail = context.Lines?.GetValueOrDefault(line.Id);
            var part = await engine.LoadPartAsync(link.PartId, ct);
            var quantity = input.Sum(i => i.Quantity);
            var warehouseId = detail?.WarehouseId ?? link.WarehouseId;
            var binId = detail?.BinId ?? link.BinId;
            if (part.Tracking != TrackingMode.None && detail is null)
            {
                throw new ArgumentException($"{part.Display} is {(part.Tracking == TrackingMode.Lot ? "lot" : "serial")} tracked: receive line {line.LineNumber} in Materials › Receiving to record its {(part.Tracking == TrackingMode.Lot ? "lot" : "serial numbers")}.");
            }

            await engine.LocationAsync(warehouseId, binId, part, ct);
            StockEngine.Tracking(part, detail?.Lot, part.Tracking == TrackingMode.Serial ? detail?.Serials?.FirstOrDefault() : null);
            await engine.SerialsAsync(part, quantity, detail?.Serials, ct);
            result.Add(new Planned(line, link, quantity, warehouseId, binId, detail?.Lot, detail?.Serials));
        }

        return result;
    }

    private async Task StockAsync(Guid purchaseOrderId, DateOnly date, IReadOnlyList<Planned> planned, CancellationToken ct)
    {
        if (planned.Count == 0)
        {
            return;
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var order = await db.Set<PurchaseOrder>().AsNoTracking().FirstAsync(o => o.Id == purchaseOrderId, ct);
        var receipt = await db.Set<PurchaseReceipt>().AsNoTracking().Where(r => r.PurchaseOrderId == purchaseOrderId).OrderByDescending(r => r.Number).FirstAsync(ct);
        var engine = await StockEngine.CreateAsync(db, ledger, ct);
        var created = new List<InventoryTransaction>();
        foreach (var p in planned)
        {
            var part = await engine.LoadPartAsync(p.Link.PartId, ct);
            var (warehouse, bin) = await engine.LocationAsync(p.WarehouseId, p.BinId, part, ct);
            var memo = $"{order.Number} line {p.Line.LineNumber} ({receipt.Number})";
            var serials = await engine.SerialsAsync(part, p.Quantity, p.Serials, ct);
            var plan = await Quality.QualityHooks.ActivePlanAsync(db, part.Id, Domain.Quality.InspectionKind.Receiving, ct); // Received into inspection hold.
            var lineCreated = new List<InventoryTransaction>();
            foreach (var serial in serials)
            {
                var (lot, s) = StockEngine.Tracking(part, p.Lot, serial.Length > 0 ? serial : null);
                var qty = s.Length > 0 ? 1 : p.Quantity;
                var key = new StockKey(part.Id, warehouse.Id, bin.Id, lot, s, p.Link.Ownership, p.Link.OwnerProjectId, Quality.QualityHooks.StatusFor(plan));
                var t = engine.Receive(part, key, qty, Money.Round(qty * p.Line.UnitPrice), InventoryTransactionType.PurchaseReceipt, date,
                    engine.Accounts?.ReceivedNotInvoiced, engine.Accounts?.PriceVariance, memo);
                t.PurchaseOrderLineId = p.Line.Id;
                t.PurchaseReceiptId = receipt.Id;
                created.Add(t);
                lineCreated.Add(t);
            }

            if (plan is not null)
            {
                await Quality.QualityHooks.OpenInspectionAsync(db, plan, part, lineCreated, date, engine.Actor, engine.UtcNow, i =>
                {
                    i.VendorId = order.VendorId;
                    i.PurchaseOrderId = order.Id;
                    i.PurchaseOrderLineId = p.Line.Id;
                    i.PurchaseReceiptId = receipt.Id;
                }, ct);
            }
        }

        await engine.FlushAsync(JournalSource.Inventory, receipt.Id, date, $"Receipt {receipt.Number} ({order.Number}) into inventory", ct);
        await db.SaveChangesAsync(ct);
        context.Created.AddRange(created.Select(t => t.Id));
    }

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default) => inner.ListAsync(query, cancellationToken);

    public Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.GetAsync(purchaseOrderId, cancellationToken);

    public Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default) =>
        inner.ListOrderableRequisitionLinesAsync(cancellationToken);

    public Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default) => inner.CreateAsync(request, cancellationToken);

    public Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default) =>
        inner.CreateFromRequisitionsAsync(request, cancellationToken);

    public Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default) =>
        inner.UpdateAsync(purchaseOrderId, request, cancellationToken);

    public Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.IssueAsync(purchaseOrderId, cancellationToken);

    public Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default) =>
        inner.ChangeOrderAsync(purchaseOrderId, request, cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default) => inner.ListReceivableAsync(cancellationToken);

    public Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.CloseAsync(purchaseOrderId, cancellationToken);

    public Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(purchaseOrderId, request, cancellationToken);
}

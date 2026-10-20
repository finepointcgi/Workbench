using Microsoft.EntityFrameworkCore;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// The supplier portal for one vendor: the vendor is always <see cref="SupplierCaller"/>'s (set by
/// <see cref="VendorScopedSupplierPortalService"/>, which also checks every id belongs to it), and every query filters
/// by it again. Writes go through <see cref="SupplierDocuments"/> and <see cref="SourcingSupport"/>, the same rules as
/// buyers and EDI.
/// </summary>
public sealed class SupplierPortalService(IWorkbenchDbContextFactory dbFactory, SupplierCaller caller, SupplierDocuments documents, TimeProvider clock)
    : ISupplierPortalService
{
    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    private Guid VendorId => caller.VendorId;

    public Task<SupplierSessionDto> GetSessionAsync(CancellationToken cancellationToken = default) => Task.FromResult(Session());

    private SupplierSessionDto Session() => new(caller.Contact.Id, caller.Contact.Name, caller.Contact.Email, caller.Vendor.Id, caller.Vendor.Code, caller.Vendor.Name,
        caller.Contact.PasswordHash is not null);

    public async Task<SupplierHomeDto> GetHomeAsync(CancellationToken cancellationToken = default)
    {
        var rfqs = await ListRfqsAsync(cancellationToken);
        var orders = await ListOrdersAsync(cancellationToken);
        var invoices = await ListInvoicesAsync(cancellationToken);
        return new SupplierHomeDto(Session(), rfqs.Count(r => r.CanQuote), orders.Count(o => o.NeedsAcknowledgement),
            orders.Count(o => o.Status is "Open" or "Partly received"), invoices.Count(i => i.Status is "Received" or "In review"),
            invoices.Where(i => i.Status is not ("Not accepted" or "Void" or "Received" or "In review")).Sum(i => i.Balance));
    }

    // ---- RFQs ----

    public async Task<IReadOnlyList<SupplierRfqSummaryDto>> ListRfqsAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invitations = await db.Set<RfqVendor>().AsNoTracking().Where(v => v.VendorId == VendorId).ToListAsync(cancellationToken);
        var ids = invitations.Select(i => i.RfqId).ToList();
        var rfqs = await db.Set<Rfq>().AsNoTracking().Include(r => r.Lines).Where(r => ids.Contains(r.Id) && r.Status != RfqStatus.Draft).ToListAsync(cancellationToken);
        var myQuoteLines = await MyQuoteLineIdsAsync(db, ids, cancellationToken);
        return rfqs.OrderByDescending(r => r.IssuedAtUtc).Select(r => Summary(r, invitations.First(i => i.RfqId == r.Id), myQuoteLines)).ToList();
    }

    public async Task<SupplierRfqDto> GetRfqAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await RfqDetailAsync(db, rfqId, cancellationToken);
    }

    public async Task<SupplierRfqDto> SubmitQuoteAsync(Guid rfqId, SupplierQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await LoadRfqAsync(db, rfqId, cancellationToken);
        if (rfq.DueDate < Today)
        {
            throw new InvalidOperationException($"Quotes for {rfq.Number} were due on {rfq.DueDate:MMMM d, yyyy}. Contact the buyer if you still want to quote.");
        }

        var quote = await SourcingSupport.SaveQuoteAsync(db, rfq, VendorId,
            new SaveQuoteRequest(VendorId, Today, request.LeadTimeDays, request.Lines ?? [], request.ValidUntil, request.Reference, request.Terms, request.Notes),
            VendorQuoteSource.Portal, caller.Contact.Email, UtcNow, cancellationToken);
        documents.Audit(db, SourcingSupport.RfqEntity, rfq.Id, "QuoteSubmitted", caller.Actor, RfqService.QuoteSnapshot(caller.Vendor, quote));
        await db.SaveChangesAsync(cancellationToken);
        return await RfqDetailAsync(db, rfqId, cancellationToken);
    }

    public async Task<SupplierRfqDto> DeclineRfqAsync(Guid rfqId, string? reason, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await LoadRfqAsync(db, rfqId, cancellationToken);
        if (!SourcingSupport.QuotableStatuses.Contains(rfq.Status))
        {
            throw new InvalidOperationException($"{rfq.Number} is closed.");
        }

        if (await db.Set<VendorQuote>().AnyAsync(q => q.RfqId == rfq.Id && q.VendorId == VendorId, cancellationToken))
        {
            throw new InvalidOperationException("You've already sent a quote. Contact the buyer to withdraw it.");
        }

        var invitation = await db.Set<RfqVendor>().FirstAsync(v => v.RfqId == rfq.Id && v.VendorId == VendorId, cancellationToken);
        invitation.Status = RfqInvitationStatus.Declined;
        invitation.DeclineReason = PurchasingSupport.Optional(reason, "The reason", 1000);
        documents.Audit(db, SourcingSupport.RfqEntity, rfq.Id, "Declined", caller.Actor, new { Vendor = caller.Vendor.Code, invitation.DeclineReason });
        await db.SaveChangesAsync(cancellationToken);
        return await RfqDetailAsync(db, rfqId, cancellationToken);
    }

    private async Task<Rfq> LoadRfqAsync(IWorkbenchDbContext db, Guid rfqId, CancellationToken ct)
    {
        var invited = await db.Set<RfqVendor>().AnyAsync(v => v.RfqId == rfqId && v.VendorId == VendorId, ct);
        var rfq = invited ? await db.Set<Rfq>().Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == rfqId && r.Status != RfqStatus.Draft, ct) : null;
        return rfq ?? throw WorkbenchNotFoundException.For("RFQ", rfqId);
    }

    private async Task<SupplierRfqDto> RfqDetailAsync(IWorkbenchDbContext db, Guid rfqId, CancellationToken ct)
    {
        var rfq = await LoadRfqAsync(db, rfqId, ct);
        var invitation = await db.Set<RfqVendor>().AsNoTracking().FirstAsync(v => v.RfqId == rfq.Id && v.VendorId == VendorId, ct);
        var quote = await db.Set<VendorQuote>().AsNoTracking().Include(q => q.Lines).ThenInclude(l => l.PriceBreaks)
            .FirstOrDefaultAsync(q => q.RfqId == rfq.Id && q.VendorId == VendorId, ct);
        var partIds = rfq.Lines.Where(l => l.PartId != null).Select(l => l.PartId!.Value).ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Number, ct);
        var mine = await MyQuoteLineIdsAsync(db, [rfq.Id], ct);
        return new SupplierRfqDto(
            Summary(rfq, invitation, mine),
            rfq.Terms,
            rfq.ShipTo,
            rfq.Instructions,
            rfq.Lines.OrderBy(l => l.LineNumber).Select(l => new SupplierRfqLineDto(l.Id, l.LineNumber, l.Description, l.PartId is { } p ? parts.GetValueOrDefault(p) : null,
                l.Quantity, l.UnitOfMeasure, l.NeededBy)).ToList(),
            quote is null ? null : new SupplierQuoteDto(quote.Reference, quote.QuoteDate, quote.ValidUntil, quote.LeadTimeDays, quote.Terms, quote.Notes, quote.SubmittedAtUtc,
                quote.Lines.Select(l => new SupplierQuoteLineDto(l.RfqLineId, l.NoBid, l.UnitPrice,
                    l.PriceBreaks.OrderBy(b => b.MinimumQuantity).Select(b => new PriceBreakDto(b.MinimumQuantity, b.UnitPrice)).ToList(), l.LeadTimeDays, l.Note)).ToList()));
    }

    private async Task<HashSet<Guid>> MyQuoteLineIdsAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> rfqIds, CancellationToken ct) =>
        (await db.Set<VendorQuote>().AsNoTracking().Where(q => rfqIds.Contains(q.RfqId) && q.VendorId == VendorId)
            .SelectMany(q => q.Lines.Select(l => l.Id)).ToListAsync(ct)).ToHashSet();

    private SupplierRfqSummaryDto Summary(Rfq r, RfqVendor invitation, HashSet<Guid> myQuoteLines)
    {
        var quotable = SourcingSupport.QuotableStatuses.Contains(r.Status) && r.DueDate >= Today && invitation.Status != RfqInvitationStatus.Declined;
        var awardedLines = r.Lines.Where(l => l.AwardedQuoteLineId != null).ToList();
        var won = awardedLines.Count(l => myQuoteLines.Contains(l.AwardedQuoteLineId!.Value));
        string? outcome = awardedLines.Count == 0 || invitation.Status != RfqInvitationStatus.Quoted ? null
            : won == 0 ? (r.Status == RfqStatus.Awarded ? "Not awarded" : null)
            : won == r.Lines.Count ? "Awarded to you"
            : "Partly awarded to you";

        // A vendor sees whether the RFQ is open for them, never anyone else's quotes or prices.
        var status = r.Status switch
        {
            RfqStatus.Cancelled => "Cancelled",
            RfqStatus.Awarded => "Closed",
            _ when r.DueDate < Today => "Closed",
            _ => "Open",
        };
        return new SupplierRfqSummaryDto(r.Id, r.Number, r.Title, r.DueDate, status, quotable, invitation.Status == RfqInvitationStatus.Quoted,
            invitation.Status == RfqInvitationStatus.Declined, outcome);
    }

    // ---- Purchase orders ----

    public async Task<IReadOnlyList<SupplierOrderSummaryDto>> ListOrdersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var orders = await db.Set<PurchaseOrder>().AsNoTracking().Where(o => o.VendorId == VendorId && o.Status != PurchaseOrderStatus.Draft)
            .OrderByDescending(o => o.OrderDate).ThenByDescending(o => o.Number).Take(500).ToListAsync(cancellationToken);
        var ids = orders.Select(o => o.Id).ToList();
        var acks = await db.Set<PurchaseOrderAcknowledgement>().AsNoTracking().Where(a => ids.Contains(a.PurchaseOrderId) && a.VendorId == VendorId).ToListAsync(cancellationToken);
        return orders.Select(o => OrderSummary(o, acks.Where(a => a.PurchaseOrderId == o.Id).ToList())).ToList();
    }

    public async Task<SupplierOrderDto> GetOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await OrderDetailAsync(db, purchaseOrderId, cancellationToken);
    }

    public async Task<SupplierOrderDto> AcknowledgeOrderAsync(Guid purchaseOrderId, AcknowledgeOrderRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await SupplierDocuments.LoadOrderAsync(db, VendorId, purchaseOrderId, cancellationToken);
        var ack = await documents.AcknowledgeAsync(db, VendorId, po, request, SupplierDocumentSource.Portal, caller.Actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        if (ack.Status != PurchaseOrderAcknowledgementStatus.Accepted)
        {
            await documents.NotifyBuyersAsync(po, "purchasing.po_acknowledged",
                $"{caller.Vendor.Name} {(ack.Status == PurchaseOrderAcknowledgementStatus.Rejected ? "rejected" : "accepted with changes")} {po.Number}",
                ack.Note, $"purchasing/orders/{po.Id}");
        }

        return await OrderDetailAsync(db, purchaseOrderId, cancellationToken);
    }

    public async Task<SupplierOrderDto> SubmitShipNoticeAsync(Guid purchaseOrderId, ShipNoticeRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await SupplierDocuments.LoadOrderAsync(db, VendorId, purchaseOrderId, cancellationToken);
        var notice = await documents.ShipAsync(db, VendorId, po, request, SupplierDocumentSource.Portal, caller.Actor, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await documents.NotifyBuyersAsync(po, "purchasing.ship_notice", $"{caller.Vendor.Name} shipped against {po.Number}",
            $"{notice.Number}, shipped {notice.ShipDate:MMM d}" + (notice.ExpectedDelivery is { } eta ? $", due {eta:MMM d}" : ""), $"purchasing/orders/{po.Id}");
        return await OrderDetailAsync(db, purchaseOrderId, cancellationToken);
    }

    private async Task<SupplierOrderDto> OrderDetailAsync(IWorkbenchDbContext db, Guid purchaseOrderId, CancellationToken ct)
    {
        var po = await SupplierDocuments.LoadOrderAsync(db, VendorId, purchaseOrderId, ct);
        var acks = await db.Set<PurchaseOrderAcknowledgement>().AsNoTracking().Include(a => a.Lines)
            .Where(a => a.PurchaseOrderId == po.Id && a.VendorId == VendorId).OrderBy(a => a.AcknowledgedAtUtc).ToListAsync(ct);
        var notices = await db.Set<ShipNotice>().AsNoTracking().Where(s => s.PurchaseOrderId == po.Id && s.VendorId == VendorId).OrderBy(s => s.SubmittedAtUtc).ToListAsync(ct);
        var shipped = await SupplierDocuments.ShippedAsync(db, po.Id, ct);
        var lineIds = po.Lines.Select(l => l.Id).ToList();
        var invoiced = (await db.Set<ApBillLine>().AsNoTracking()
                .Where(l => l.PurchaseOrderLineId != null && lineIds.Contains(l.PurchaseOrderLineId.Value))
                .Join(db.Set<ApBill>().AsNoTracking(), l => l.BillId, b => b.Id, (l, b) => new { PoLine = l.PurchaseOrderLineId!.Value, l.Quantity, b.Status, b.VendorId })
                .ToListAsync(ct))
            .Where(x => x.VendorId == VendorId && x.Status is not (ApBillStatus.Rejected or ApBillStatus.Void))
            .GroupBy(x => x.PoLine).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity ?? 0));
        var latest = acks.LastOrDefault(a => a.Revision == po.Revision);
        var invoices = (await InvoicesAsync(db, ct)).Where(i => i.PurchaseOrderNumber == po.Number).ToList();
        return new SupplierOrderDto(
            OrderSummary(po, acks),
            po.Terms,
            po.ShipTo,
            po.Lines.OrderBy(l => l.LineNumber).Select(l => new SupplierOrderLineDto(l.Id, l.LineNumber, l.Description, l.Quantity, l.UnitPrice, l.Amount, l.NeededBy,
                acks.Where(a => a.Status != PurchaseOrderAcknowledgementStatus.Rejected).SelectMany(a => a.Lines).LastOrDefault(x => x.PurchaseOrderLineId == l.Id && x.PromisedDate != null)?.PromisedDate,
                l.QuantityReceived, shipped.GetValueOrDefault(l.Id), invoiced.GetValueOrDefault(l.Id))).ToList(),
            acks.Select(a => new SupplierAcknowledgementDto(a.Revision, (AcknowledgementState)(int)a.Status, a.Note, (SupplierChannel)(int)a.Source, a.AcknowledgedBy, a.AcknowledgedAtUtc)).ToList(),
            notices.Select(ShipNoticeDto).ToList(),
            invoices,
            po.Status is PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived && latest?.Status != PurchaseOrderAcknowledgementStatus.Rejected,
            ProcurementSupport.OpenStatuses.Contains(po.Status) && latest?.Status != PurchaseOrderAcknowledgementStatus.Rejected);
    }

    private static SupplierShipNoticeDto ShipNoticeDto(ShipNotice s) => new(s.Id, s.Number, s.ShipmentReference, s.ShipDate, s.ExpectedDelivery, s.Carrier, s.TrackingNumber,
        (ShipNoticeState)(int)s.Status, (SupplierChannel)(int)s.Source);

    private static SupplierOrderSummaryDto OrderSummary(PurchaseOrder o, IReadOnlyList<PurchaseOrderAcknowledgement> acks)
    {
        var latest = acks.Where(a => a.Revision == o.Revision).OrderBy(a => a.AcknowledgedAtUtc).LastOrDefault();
        var status = o.Status switch
        {
            PurchaseOrderStatus.Issued => "Open",
            PurchaseOrderStatus.PartiallyReceived => "Partly received",
            PurchaseOrderStatus.Received => "Received",
            PurchaseOrderStatus.Closed => "Closed",
            PurchaseOrderStatus.Cancelled => "Cancelled",
            _ => o.Status.ToString(),
        };
        return new SupplierOrderSummaryDto(o.Id, o.Number, o.OrderDate, o.Revision, o.Total, status,
            latest is null ? null : (AcknowledgementState)(int)latest.Status,
            latest is null && o.Status is PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived);
    }

    // ---- Invoices and payments ----

    public async Task<IReadOnlyList<SupplierInvoiceDto>> ListInvoicesAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await InvoicesAsync(db, cancellationToken);
    }

    public async Task<SupplierInvoiceDto> SubmitInvoiceAsync(SupplierInvoiceRequest request, string fileName, Stream content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (content is null || string.IsNullOrWhiteSpace(fileName))
        {
            throw new ArgumentException("Attach the invoice as a PDF file.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var po = await SupplierDocuments.LoadOrderAsync(db, VendorId, request.PurchaseOrderId, cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, VendorId, cancellationToken);
        var (bill, _) = await documents.InvoiceAsync(db, vendor, po, request, SupplierDocumentSource.Portal, caller.Actor, null, (fileName, content), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await documents.NotifyBuyersAsync(po, "purchasing.supplier_invoice", $"{vendor.Name} sent invoice {bill.InvoiceNumber} for {po.Number}",
            $"Draft bill {bill.Number}, {bill.Amount:N2}", $"purchasing/bills/{bill.Id}");
        return (await InvoicesAsync(db, cancellationToken)).First(i => i.BillId == bill.Id);
    }

    /// <summary>
    /// The vendor's bills that accounts payable is working on or has finished (internal drafts are left out, except the
    /// ones the vendor sent), with payment status in plain words.
    /// </summary>
    private async Task<List<SupplierInvoiceDto>> InvoicesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var sent = await db.Set<SupplierInvoice>().AsNoTracking().Where(i => i.VendorId == VendorId).Select(i => i.BillId).ToListAsync(ct);
        var bills = await db.Set<ApBill>().AsNoTracking().Include(b => b.Lines)
            .Where(b => b.VendorId == VendorId && (b.Status != ApBillStatus.Draft || sent.Contains(b.Id)))
            .OrderByDescending(b => b.InvoiceDate).ThenByDescending(b => b.Number).Take(500).ToListAsync(ct);
        var billIds = bills.Select(b => b.Id).ToList();
        var payments = await db.Set<ApPayment>().AsNoTracking().Where(p => billIds.Contains(p.BillId) && p.VendorId == VendorId && p.Status != ApPaymentStatus.Void).ToListAsync(ct);
        var poLineIds = bills.SelectMany(b => b.Lines).Where(l => l.PurchaseOrderLineId != null).Select(l => l.PurchaseOrderLineId!.Value).Distinct().ToList();
        var poNumbers = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => poLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, o.Number, o.VendorId }).ToListAsync(ct);
        return bills.Select(b =>
        {
            var paid = payments.Where(p => p.BillId == b.Id && p.Status == ApPaymentStatus.Paid).OrderBy(p => p.PaidOn).ToList();
            var scheduled = payments.Any(p => p.BillId == b.Id && p.Status == ApPaymentStatus.Scheduled);
            var status = b.Status switch
            {
                ApBillStatus.Draft => "Received",
                ApBillStatus.PendingApproval => "In review",
                ApBillStatus.Rejected => "Not accepted",
                ApBillStatus.Void => "Void",
                _ when b.Payable > 0 && b.PaidAmount >= b.Payable => "Paid",
                _ when scheduled => "Scheduled for payment",
                _ when b.PaidAmount > 0 => "Partly paid",
                _ => "Approved",
            };
            var po = b.Lines.Select(l => l.PurchaseOrderLineId).Where(id => id != null)
                .Select(id => poNumbers.FirstOrDefault(x => x.Id == id && x.VendorId == VendorId)?.Number).FirstOrDefault(n => n != null);
            return new SupplierInvoiceDto(b.Id, b.InvoiceNumber, b.InvoiceDate, po, b.Amount, b.PaidAmount, b.Status is ApBillStatus.Rejected or ApBillStatus.Void ? 0 : b.Balance,
                b.DueDate, status, paid.LastOrDefault()?.PaidOn, paid.LastOrDefault()?.Reference, b.Status == ApBillStatus.Rejected ? b.RejectionReason : null);
        }).ToList();
    }
}

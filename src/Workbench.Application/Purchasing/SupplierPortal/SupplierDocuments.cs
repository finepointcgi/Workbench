using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Files;
using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// What a vendor sends us, whichever way it comes (supplier portal or EDI): purchase order acknowledgements, ship notices
/// and invoices (as draft AP bills billing the PO lines, for accounts payable to match and submit). One set of rules for
/// both channels. The caller has already established which vendor is sending; every document must be that vendor's.
/// Changes are audited with the vendor's actor ("... (supplier portal)" or "EDI ... "), never an internal user.
/// </summary>
public sealed class SupplierDocuments(FileStorage files, INotificationPublisher notifications, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string ShipNoticeEntity = nameof(ShipNotice);
    public const string InvoiceOwnerType = "ApBill";

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    public static string PortalActor(SupplierContact contact) => $"{contact.Name} <{contact.Email}> (supplier portal)";

    public static string EdiActor(Vendor vendor) => $"EDI ({vendor.Code})";

    /// <summary>A purchase order of <paramref name="vendorId"/> that has been issued (drafts aren't the vendor's business yet).</summary>
    public static async Task<PurchaseOrder> LoadOrderAsync(IWorkbenchDbContext db, Guid vendorId, Guid purchaseOrderId, CancellationToken ct)
    {
        var po = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == purchaseOrderId, ct);
        return po is null || po.VendorId != vendorId || po.Status == PurchaseOrderStatus.Draft ? throw WorkbenchNotFoundException.For("Purchase order", purchaseOrderId) : po;
    }

    public static async Task<PurchaseOrder> LoadOrderByNumberAsync(IWorkbenchDbContext db, Guid vendorId, string number, CancellationToken ct)
    {
        var po = await db.Set<PurchaseOrder>().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Number == number, ct);
        return po is null || po.VendorId != vendorId || po.Status == PurchaseOrderStatus.Draft
            ? throw new WorkbenchNotFoundException($"Purchase order {number} isn't one of this vendor's issued orders.")
            : po;
    }

    // ---- Acknowledgements (portal, EDI 855) ----

    public async Task<PurchaseOrderAcknowledgement> AcknowledgeAsync(
        IWorkbenchDbContext db, Guid vendorId, PurchaseOrder po, AcknowledgeOrderRequest input, SupplierDocumentSource source, string actor, CancellationToken ct)
    {
        if (!ProcurementSupport.OpenStatuses.Contains(po.Status))
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; it can't be acknowledged.");
        }

        var status = (PurchaseOrderAcknowledgementStatus)(int)input.Status;
        var note = PurchasingSupport.Optional(input.Note, "The note", 2000);
        if (status == PurchaseOrderAcknowledgementStatus.Rejected && note is null)
        {
            throw new ArgumentException("Say why you can't accept the order.");
        }

        var ack = new PurchaseOrderAcknowledgement
        {
            PurchaseOrderId = po.Id,
            VendorId = vendorId,
            Revision = po.Revision,
            Status = status,
            Note = note,
            Source = source,
            AcknowledgedBy = actor,
            AcknowledgedAtUtc = UtcNow,
        };
        foreach (var li in (input.Lines ?? []).GroupBy(l => l.PurchaseOrderLineId).Select(g => g.Last()))
        {
            var line = po.Lines.FirstOrDefault(l => l.Id == li.PurchaseOrderLineId) ?? throw new ArgumentException("An acknowledged line isn't on this order.");
            var prefix = $"Line {line.LineNumber}";
            var quantity = li.Quantity is { } q && q != line.Quantity ? ProcurementSupport.Quantity(q, $"{prefix}: quantity") : (decimal?)null;
            var price = li.UnitPrice is { } p && p != line.UnitPrice ? ProcurementSupport.UnitPrice(p, $"{prefix}: unit price") : (decimal?)null;
            if (li.PromisedDate is null && quantity is null && price is null && string.IsNullOrWhiteSpace(li.Note))
            {
                continue;
            }

            ack.Lines.Add(new PurchaseOrderAcknowledgementLine
            {
                AcknowledgementId = ack.Id,
                PurchaseOrderLineId = line.Id,
                PromisedDate = li.PromisedDate,
                Quantity = quantity,
                UnitPrice = price,
                Note = PurchasingSupport.Optional(li.Note, $"{prefix}: note", 500),
            });
        }

        if (status == PurchaseOrderAcknowledgementStatus.Accepted && ack.Lines.Any(l => l.Quantity is not null || l.UnitPrice is not null))
        {
            ack.Status = status = PurchaseOrderAcknowledgementStatus.AcceptedWithChanges;
        }

        if (status == PurchaseOrderAcknowledgementStatus.AcceptedWithChanges && note is null && ack.Lines.All(l => l.Quantity is null && l.UnitPrice is null && l.Note is null))
        {
            throw new ArgumentException("Say what changes: a different quantity or price on a line, or a note.");
        }

        db.Set<PurchaseOrderAcknowledgement>().Add(ack);
        Audit(db, ProcurementSupport.OrderEntity, po.Id, "Acknowledged", actor, new
        {
            po.Revision,
            Status = status.ToString(),
            Source = source.ToString(),
            ack.Note,
            Lines = ack.Lines.Select(l => new { po.Lines.First(x => x.Id == l.PurchaseOrderLineId).LineNumber, l.PromisedDate, l.Quantity, l.UnitPrice, l.Note }),
        });
        return ack;
    }

    // ---- Ship notices (portal, EDI 856) ----

    public async Task<ShipNotice> ShipAsync(
        IWorkbenchDbContext db, Guid vendorId, PurchaseOrder po, ShipNoticeRequest input, SupplierDocumentSource source, string actor, CancellationToken ct)
    {
        if (po.Status is not (PurchaseOrderStatus.Issued or PurchaseOrderStatus.PartiallyReceived))
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; nothing is left to ship on it.");
        }

        var reference = PurchasingSupport.Optional(input.ShipmentReference, "Shipment id", 64);
        if (reference is not null && await db.Set<ShipNotice>().AnyAsync(s => s.VendorId == vendorId && s.ShipmentReference == reference && s.Status != ShipNoticeStatus.Cancelled, ct))
        {
            throw new ArgumentException($"Shipment {reference} was already sent.");
        }

        if (input.ShipDate > DateOnly.FromDateTime(UtcNow).AddDays(30))
        {
            throw new ArgumentException("The ship date is too far ahead; send the notice when it ships.");
        }

        if (input.ExpectedDelivery is { } eta && eta < input.ShipDate)
        {
            throw new ArgumentException("Delivery can't be before the ship date.");
        }

        var lines = (input.Lines ?? []).Where(l => l.Quantity != 0).GroupBy(l => l.PurchaseOrderLineId).ToList();
        if (lines.Count == 0)
        {
            throw new ArgumentException("Enter a quantity shipped on at least one line.");
        }

        var shipped = await ShippedAsync(db, po.Id, ct);
        var numbers = await db.Set<ShipNotice>().Select(s => s.Number).ToListAsync(ct);
        var notice = new ShipNotice
        {
            Number = PurchasingSupport.NextNumber(numbers, "ASN"),
            PurchaseOrderId = po.Id,
            VendorId = vendorId,
            ShipmentReference = reference,
            ShipDate = input.ShipDate,
            ExpectedDelivery = input.ExpectedDelivery,
            Carrier = PurchasingSupport.Optional(input.Carrier, "Carrier", 100),
            TrackingNumber = PurchasingSupport.Optional(input.TrackingNumber, "Tracking number", 100),
            Source = source,
            SubmittedBy = actor,
            SubmittedAtUtc = UtcNow,
        };
        foreach (var g in lines)
        {
            var line = po.Lines.FirstOrDefault(l => l.Id == g.Key) ?? throw new ArgumentException("A shipped line isn't on this order.");
            var prefix = $"Line {line.LineNumber}";
            var quantity = ProcurementSupport.Quantity(g.Sum(l => l.Quantity), $"{prefix}: quantity shipped");
            var open = line.Quantity - Math.Max(line.QuantityReceived, shipped.GetValueOrDefault(line.Id));
            if (quantity > open)
            {
                throw new ArgumentException($"{prefix}: {Q(quantity)} is more than the {Q(Math.Max(0, open))} not yet shipped or received.");
            }

            notice.Lines.Add(new ShipNoticeLine { ShipNoticeId = notice.Id, PurchaseOrderLineId = line.Id, Quantity = quantity });
        }

        db.Set<ShipNotice>().Add(notice);
        Audit(db, ShipNoticeEntity, notice.Id, "Submitted", actor, new
        {
            notice.Number,
            PurchaseOrder = po.Number,
            notice.ShipmentReference,
            notice.ShipDate,
            notice.ExpectedDelivery,
            notice.Carrier,
            notice.TrackingNumber,
            Source = source.ToString(),
            Lines = notice.Lines.Select(l => new { po.Lines.First(x => x.Id == l.PurchaseOrderLineId).LineNumber, l.Quantity }),
        });
        return notice;
    }

    /// <summary>Quantity on ship notices that aren't cancelled, per PO line.</summary>
    public static async Task<Dictionary<Guid, decimal>> ShippedAsync(IWorkbenchDbContext db, Guid purchaseOrderId, CancellationToken ct) =>
        (await db.Set<ShipNoticeLine>().AsNoTracking()
            .Join(db.Set<ShipNotice>().AsNoTracking(), l => l.ShipNoticeId, s => s.Id, (l, s) => new { l.PurchaseOrderLineId, l.Quantity, s.PurchaseOrderId, s.Status })
            .Where(x => x.PurchaseOrderId == purchaseOrderId && x.Status != ShipNoticeStatus.Cancelled)
            .ToListAsync(ct))
        .GroupBy(x => x.PurchaseOrderLineId).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity));

    // ---- Invoices (portal, EDI 810) ----

    /// <summary>
    /// A draft AP bill for the PO lines and quantities invoiced, at the PO prices (the three-way match runs when AP submits
    /// it). EDI unit prices that differ are kept on the line for the match to flag. The PDF, when given, is the bill's copy.
    /// </summary>
    public async Task<(ApBill Bill, SupplierInvoice Invoice)> InvoiceAsync(
        IWorkbenchDbContext db, Vendor vendor, PurchaseOrder po, SupplierInvoiceRequest input, SupplierDocumentSource source, string actor,
        IReadOnlyDictionary<Guid, decimal>? unitPrices, (string FileName, Stream Content)? pdf, CancellationToken ct)
    {
        if (!ProcurementSupport.OpenStatuses.Contains(po.Status))
        {
            throw new InvalidOperationException($"{po.Number} is {po.Status}; it can't be invoiced.");
        }

        PurchasingSupport.EnsureVendorCanTransact(vendor, "invoicing");
        var invoiceNumber = PurchasingSupport.Required(input.InvoiceNumber, "Invoice number", 64);
        if (await db.Set<ApBill>().AnyAsync(b => b.VendorId == vendor.Id && b.InvoiceNumber == invoiceNumber && b.Status != ApBillStatus.Void, ct))
        {
            throw new ArgumentException($"Invoice {invoiceNumber} was already sent.");
        }

        var today = DateOnly.FromDateTime(UtcNow);
        if (input.InvoiceDate > today.AddDays(1) || input.InvoiceDate < today.AddYears(-2))
        {
            throw new ArgumentException("Check the invoice date.");
        }

        var stated = PurchasingSupport.Money(input.StatedTotal, "The invoice total");
        var lines = (input.Lines ?? []).Where(l => l.Quantity != 0).GroupBy(l => l.PurchaseOrderLineId).ToList();
        if (lines.Count == 0)
        {
            throw new ArgumentException("Enter a quantity invoiced on at least one line.");
        }

        // Quantities on every bill that isn't rejected or void, drafts included, so a vendor can't invoice a line twice over.
        var lineIds = po.Lines.Select(l => l.Id).ToList();
        var invoiced = (await db.Set<ApBillLine>().AsNoTracking()
                .Where(l => l.PurchaseOrderLineId != null && lineIds.Contains(l.PurchaseOrderLineId.Value))
                .Join(db.Set<ApBill>().AsNoTracking(), l => l.BillId, b => b.Id, (l, b) => new { PoLine = l.PurchaseOrderLineId!.Value, l.Quantity, b.Status })
                .ToListAsync(ct))
            .Where(x => x.Status is not (ApBillStatus.Rejected or ApBillStatus.Void))
            .GroupBy(x => x.PoLine).ToDictionary(g => g.Key, g => g.Sum(x => x.Quantity ?? 0));

        var numbers = await db.Set<ApBill>().Select(b => b.Number).ToListAsync(ct);
        var bill = new ApBill
        {
            Number = PurchasingSupport.NextNumber(numbers, "V"),
            VendorId = vendor.Id,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = input.InvoiceDate,
            DueDate = input.InvoiceDate.AddDays(vendor.PaymentTermsDays),
            EnteredBy = actor.Length > 320 ? actor[..320] : actor,
            CreatedAtUtc = UtcNow,
        };
        foreach (var g in lines.OrderBy(g => po.Lines.FirstOrDefault(l => l.Id == g.Key)?.LineNumber ?? int.MaxValue))
        {
            var poLine = po.Lines.FirstOrDefault(l => l.Id == g.Key) ?? throw new ArgumentException("An invoiced line isn't on this order.");
            var prefix = $"Line {poLine.LineNumber}";
            var quantity = ProcurementSupport.Quantity(g.Sum(l => l.Quantity), $"{prefix}: quantity invoiced");
            var already = invoiced.GetValueOrDefault(poLine.Id);
            if (already + quantity > poLine.Quantity)
            {
                throw new ArgumentException($"{prefix}: {Q(already + quantity)} invoiced would be more than the {Q(poLine.Quantity)} ordered ({Q(Math.Max(0, poLine.Quantity - already))} left to invoice).");
            }

            var line = new ApBillLine
            {
                BillId = bill.Id,
                LineNumber = bill.Lines.Count + 1,
                Description = "",
                PurchaseOrderLineId = poLine.Id,
                Quantity = quantity,
                UnitPrice = unitPrices?.GetValueOrDefault(poLine.Id) is { } p and > 0 ? p : null,
            };
            bill.Lines.Add(line);
            await ThreeWayMatch.ApplyPoLineAsync(db, bill, line, ct);
        }

        bill.Amount = bill.Lines.Sum(l => l.Amount);
        bill.Description = bill.Amount == stated
            ? $"{po.Number}, sent by {(source == SupplierDocumentSource.Edi ? "EDI 810" : "the supplier portal")}"
            : $"{po.Number}, sent by {(source == SupplierDocumentSource.Edi ? "EDI 810" : "the supplier portal")}. The vendor's total is {stated.ToString("N2", CultureInfo.InvariantCulture)}; the lines at PO prices come to {bill.Amount.ToString("N2", CultureInfo.InvariantCulture)}.";
        if (bill.Description.Length > 500)
        {
            bill.Description = bill.Description[..500];
        }

        if (pdf is { } file)
        {
            if (!file.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || !await LooksLikePdfAsync(file.Content, ct))
            {
                throw new ArgumentException("Attach the invoice as a PDF file.");
            }

            var stored = await files.SaveAsync(db, InvoiceOwnerType, bill.Id, file.FileName, file.Content, ct);
            stored.UploadedBy = bill.EnteredBy;
            bill.AttachmentFileId = stored.Id;
        }

        var invoice = new SupplierInvoice
        {
            VendorId = vendor.Id,
            PurchaseOrderId = po.Id,
            BillId = bill.Id,
            InvoiceNumber = invoiceNumber,
            InvoiceDate = input.InvoiceDate,
            StatedTotal = stated,
            Source = source,
            SubmittedBy = bill.EnteredBy,
            SubmittedAtUtc = UtcNow,
        };
        db.Set<ApBill>().Add(bill);
        db.Set<SupplierInvoice>().Add(invoice);
        Audit(db, PurchasingSupport.BillEntity, bill.Id, "Created", actor, new
        {
            bill.Number,
            bill.InvoiceNumber,
            bill.InvoiceDate,
            bill.Amount,
            StatedTotal = stated,
            PurchaseOrder = po.Number,
            Source = source.ToString(),
            Lines = bill.Lines.Select(l => new { l.LineNumber, l.PurchaseOrderLineId, l.Quantity, l.UnitPrice, l.Amount }),
        });
        return (bill, invoice);
    }

    /// <summary>Tells the people who created and issued the order (after the caller saved).</summary>
    public async Task NotifyBuyersAsync(PurchaseOrder po, string kind, string title, string? body, string link)
    {
        await notifications.PublishAsync([po.CreatedBy, po.IssuedBy], kind, title, body, link);
    }

    internal void Audit(IWorkbenchDbContext db, string entityType, Guid entityId, string action, string actor, object? after) =>
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            EntityType = entityType,
            EntityId = entityId,
            Action = action,
            AfterJson = after is null ? null : JsonSerializer.Serialize(after, Json),
            Actor = actor.Length > 320 ? actor[..320] : actor,
            OccurredAtUtc = UtcNow,
        });

    private static async Task<bool> LooksLikePdfAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek)
        {
            return true;
        }

        var start = content.Position;
        var header = new byte[5];
        var read = await content.ReadAsync(header, ct);
        content.Position = start;
        return read == 5 && header[0] == '%' && header[1] == 'P' && header[2] == 'D' && header[3] == 'F' && header[4] == '-';
    }

    private static string Q(decimal value) => value.ToString("0.####", CultureInfo.InvariantCulture);
}

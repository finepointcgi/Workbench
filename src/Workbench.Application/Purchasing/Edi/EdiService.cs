using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing.Edi;

/// <summary>
/// EDI with vendors: 850s out (by hand, or when an order is issued, see <see cref="EdiSendingPurchaseOrderService"/>), and
/// 855/856/810 in through <see cref="SupplierDocuments"/>, the supplier portal's own rules. Each transaction set is applied
/// in its own unit of work and gets one EDI log row (with the set alone as X12, so a failed one can be retried); a set
/// already processed for the partner (same ISA13/ST02) is logged as a duplicate and not applied again.
/// </summary>
public sealed partial class EdiService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    AuditRecorder audit,
    SupplierDocuments documents,
    IEnumerable<IEdiTransport> transports,
    IOptions<EdiOptions> options,
    TimeProvider clock,
    ILogger<EdiService> logger) : IEdiService
{
    public const string PartnerEntity = nameof(EdiPartner);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private EdiOptions Options => options.Value;

    // ---- Partners ----

    public async Task<IReadOnlyList<EdiPartnerDto>> ListPartnersAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var partners = await db.Set<EdiPartner>().AsNoTracking().ToListAsync(cancellationToken);
        var vendors = await VendorsAsync(db, partners.Select(p => p.VendorId), cancellationToken);
        return partners.OrderBy(p => vendors[p.VendorId].Name).Select(p => PartnerDto(p, vendors[p.VendorId])).ToList();
    }

    public async Task<EdiPartnerDto> SavePartnerAsync(Guid? partnerId, SaveEdiPartnerRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var id = (request.InterchangeId ?? "").Trim().ToUpperInvariant();
        if (!InterchangeIdPattern().IsMatch(id))
        {
            throw new ArgumentException("The interchange id is 1 to 15 letters, digits or spaces (as the vendor sends it in ISA06).");
        }

        var qualifier = (request.InterchangeQualifier ?? "").Trim().ToUpperInvariant();
        if (!QualifierPattern().IsMatch(qualifier))
        {
            throw new ArgumentException("The qualifier is two letters or digits, for example ZZ or 01.");
        }

        var app = PurchasingSupport.Optional(request.ApplicationCode, "The application code", 15)?.ToUpperInvariant();
        var kind = (EdiTransportKind)(int)request.Transport;
        var address = (request.Address ?? "").Trim();
        if (kind == EdiTransportKind.FileDrop ? !FileDropEdiTransport.IsValidAddress(address) : !HostPattern().IsMatch(address))
        {
            throw new ArgumentException(kind == EdiTransportKind.FileDrop
                ? "The file drop folder is a plain folder name: letters, digits, dashes and underscores."
                : "Enter the SFTP server's host name.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, request.VendorId, cancellationToken);
        var partner = partnerId is { } pid
            ? await db.Set<EdiPartner>().FirstOrDefaultAsync(p => p.Id == pid, cancellationToken) ?? throw WorkbenchNotFoundException.For("EDI partner", pid)
            : null;
        if (await db.Set<EdiPartner>().AnyAsync(p => p.VendorId == vendor.Id && p.Id != partnerId, cancellationToken))
        {
            throw new ArgumentException($"{vendor.Name} already has an EDI partner set up.");
        }

        if (await db.Set<EdiPartner>().AnyAsync(p => p.InterchangeQualifier == qualifier && p.InterchangeId == id && p.Id != partnerId, cancellationToken))
        {
            throw new ArgumentException($"Interchange id {qualifier}/{id} belongs to another partner.");
        }

        var before = partner is null ? null : Snapshot(partner);
        partner ??= db.Set<EdiPartner>().Add(new EdiPartner { InterchangeId = id, Address = address, UpdatedBy = access.Email }).Entity;
        partner.VendorId = vendor.Id;
        partner.InterchangeId = id;
        partner.InterchangeQualifier = qualifier;
        partner.ApplicationCode = app;
        partner.Transport = kind;
        partner.Address = address;
        partner.SendPurchaseOrders = request.SendPurchaseOrders;
        partner.IsActive = request.IsActive;
        partner.UpdatedBy = access.Email;
        partner.UpdatedAtUtc = UtcNow;
        audit.Record(db, PartnerEntity, partner.Id, before is null ? "Created" : "Updated", before, Snapshot(partner));
        await db.SaveChangesAsync(cancellationToken);
        return PartnerDto(partner, vendor);
    }

    // ---- Log ----

    public async Task<IReadOnlyList<EdiMessageDto>> ListLogAsync(EdiLogQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<EdiMessage>().AsNoTracking();
        if (query.Direction is { } d)
        {
            var dir = (EdiDirection)(int)d;
            q = q.Where(m => m.Direction == dir);
        }

        if (query.Status is { } s)
        {
            var status = (EdiMessageStatus)(int)s;
            q = q.Where(m => m.Status == status);
        }

        if (!string.IsNullOrWhiteSpace(query.TransactionSet))
        {
            q = q.Where(m => m.TransactionSet == query.TransactionSet);
        }

        if (query.VendorId is { } v)
        {
            q = q.Where(m => m.VendorId == v);
        }

        var rows = await q.OrderByDescending(m => m.CreatedAtUtc).Take(Math.Clamp(query.Take, 1, 1000))
            .Select(m => new { m.Id, m.Direction, m.TransactionSet, m.VendorId, m.ControlNumber, m.FileName, m.Status, m.Error, m.DocumentNumber, m.DocumentId, m.CreatedBy, m.CreatedAtUtc })
            .ToListAsync(cancellationToken);
        var vendors = await VendorsAsync(db, rows.Where(r => r.VendorId != null).Select(r => r.VendorId!.Value), cancellationToken);
        return rows.Select(m => new EdiMessageDto(m.Id, (EdiFlow)(int)m.Direction, m.TransactionSet, m.VendorId is { } vid ? vendors.GetValueOrDefault(vid)?.Name : null,
            m.ControlNumber, m.FileName, (EdiOutcome)(int)m.Status, m.Error, m.DocumentNumber, m.DocumentId, m.CreatedBy, m.CreatedAtUtc)).ToList();
    }

    public async Task<EdiMessageDetailDto> GetMessageAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var m = await db.Set<EdiMessage>().AsNoTracking().FirstOrDefaultAsync(x => x.Id == messageId, cancellationToken) ?? throw WorkbenchNotFoundException.For("EDI message", messageId);
        var vendors = await VendorsAsync(db, m.VendorId is { } v ? [v] : [], cancellationToken);
        return new EdiMessageDetailDto(Dto(m, vendors), m.Content);
    }

    // ---- Outbound 850 ----

    public async Task<EdiMessageDto> SendPurchaseOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        var message = await SendPurchaseOrderCoreAsync(purchaseOrderId, access.Email, requirePartner: true, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return Dto(message!, await VendorsAsync(db, [message!.VendorId!.Value], cancellationToken));
    }

    /// <summary>
    /// Builds the order's 850 and hands it to the partner's transport, logging the outcome (a transport failure is a Failed
    /// row, not an exception). Without an active partner: throws when <paramref name="requirePartner"/>, else returns null.
    /// </summary>
    internal async Task<EdiMessage?> SendPurchaseOrderCoreAsync(Guid purchaseOrderId, string createdBy, bool requirePartner, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        var po = await db.Set<PurchaseOrder>().AsNoTracking().Include(o => o.Lines).FirstOrDefaultAsync(o => o.Id == purchaseOrderId, ct)
            ?? throw WorkbenchNotFoundException.For("Purchase order", purchaseOrderId);
        var partner = await db.Set<EdiPartner>().FirstOrDefaultAsync(p => p.VendorId == po.VendorId && p.IsActive, ct);
        if (partner is null)
        {
            if (!requirePartner)
            {
                return null;
            }

            var vendor = await PurchasingSupport.FindVendorAsync(db, po.VendorId, ct);
            throw new ArgumentException($"{vendor.Name} isn't set up for EDI (Admin › Integrations › EDI).");
        }

        if (!ProcurementSupport.OpenStatuses.Contains(po.Status))
        {
            throw new ArgumentException($"{po.Number} is {po.Status}; only issued orders are sent.");
        }

        var control = partner.NextControlNumber;
        partner.NextControlNumber = control >= 999_999_999 ? 1 : control + 1;
        await db.SaveChangesAsync(ct);

        var lineIds = po.Lines.Select(l => l.Id).ToList();
        var rfqLines = await db.Set<RfqLine>().AsNoTracking().Where(l => l.PurchaseOrderLineId != null && lineIds.Contains(l.PurchaseOrderLineId.Value)).ToListAsync(ct);
        var links = await db.Set<MaterialPurchaseLink>().AsNoTracking().Where(l => l.PurchaseOrderLineId != null && lineIds.Contains(l.PurchaseOrderLineId.Value)).ToListAsync(ct);
        var partIds = rfqLines.Where(l => l.PartId != null).Select(l => l.PartId!.Value).Concat(links.Select(l => l.PartId)).Distinct().ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var partByLine = new Dictionary<Guid, Part>();
        foreach (var l in links.Where(l => parts.ContainsKey(l.PartId)))
        {
            partByLine[l.PurchaseOrderLineId!.Value] = parts[l.PartId];
        }

        foreach (var l in rfqLines.Where(l => l.PartId is { } p && parts.ContainsKey(p)))
        {
            partByLine[l.PurchaseOrderLineId!.Value] = parts[l.PartId!.Value];
        }

        var units = rfqLines.ToDictionary(l => l.PurchaseOrderLineId!.Value, l => l.UnitOfMeasure);
        foreach (var (lineId, part) in partByLine.Where(x => !units.ContainsKey(x.Key)))
        {
            units[lineId] = part.UnitOfMeasure;
        }

        var controlText = control.ToString("D9", CultureInfo.InvariantCulture);
        var set = X12Documents.Write850(po, po.Lines, partByLine.ToDictionary(x => x.Key, x => x.Value.Number), units, "0001");
        var text = X12.Write(new X12Interchange
        {
            SenderQualifier = Options.InterchangeQualifier,
            SenderId = Options.InterchangeId,
            ReceiverQualifier = partner.InterchangeQualifier,
            ReceiverId = partner.InterchangeId,
            ControlNumber = controlText,
            Timestamp = UtcNow,
            UsageIndicator = Options.UsageIndicator,
            Groups = [new X12FunctionalGroup("PO", Options.InterchangeId, partner.ApplicationCode ?? partner.InterchangeId, control.ToString(CultureInfo.InvariantCulture), [set])],
        });
        var message = new EdiMessage
        {
            Direction = EdiDirection.Outbound,
            TransactionSet = "850",
            PartnerId = partner.Id,
            VendorId = po.VendorId,
            ControlNumber = $"{controlText}/0001",
            FileName = $"850_{po.Number}_r{po.Revision}_{controlText}.x12",
            DocumentNumber = po.Number,
            DocumentId = po.Id,
            Content = Cap(text),
            CreatedBy = createdBy,
            CreatedAtUtc = UtcNow,
        };
        try
        {
            await Transport(partner).SendAsync(partner, message.FileName, text, ct);
            message.Status = EdiMessageStatus.Processed;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "An EDI 850 couldn't be sent.");
            message.Status = EdiMessageStatus.Failed;
            message.Error = Short($"Couldn't send: {ex.Message}");
        }

        db.Set<EdiMessage>().Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    /// <summary>The vendor has an active partner that takes purchase orders automatically.</summary>
    internal async Task<bool> ShouldSendAsync(Guid vendorId, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<EdiPartner>().AnyAsync(p => p.VendorId == vendorId && p.IsActive && p.SendPurchaseOrders, ct);
    }

    // ---- Inbound ----

    public async Task<EdiPollResultDto> PollAsync(CancellationToken cancellationToken = default)
    {
        int files = 0, processed = 0, failed = 0, duplicates = 0;
        List<EdiPartner> partners;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            partners = await db.Set<EdiPartner>().AsNoTracking().Where(p => p.IsActive).ToListAsync(cancellationToken);
        }

        foreach (var partner in partners)
        {
            IReadOnlyList<EdiInboundFile> inbox;
            try
            {
                inbox = await Transport(partner).ReceiveAsync(partner, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                await LogAsync(new EdiMessage
                {
                    Direction = EdiDirection.Inbound,
                    TransactionSet = "?",
                    PartnerId = partner.Id,
                    VendorId = partner.VendorId,
                    FileName = "(inbox)",
                    Status = EdiMessageStatus.Failed,
                    Error = Short($"Couldn't read the partner's inbox: {ex.Message}"),
                    Content = "",
                    CreatedBy = access.Email,
                    CreatedAtUtc = UtcNow,
                }, cancellationToken);
                continue;
            }

            foreach (var file in inbox)
            {
                files++;
                var results = await ProcessFileAsync(file.Name, file.Content, access.Email, cancellationToken);
                processed += results.Count(r => r.Status == EdiMessageStatus.Processed);
                failed += results.Count(r => r.Status == EdiMessageStatus.Failed);
                duplicates += results.Count(r => r.Status == EdiMessageStatus.Duplicate);
                await Transport(partner).CompleteAsync(partner, file, results.All(r => r.Status != EdiMessageStatus.Failed), cancellationToken);
            }
        }

        return new EdiPollResultDto(files, processed, failed, duplicates);
    }

    public async Task<IReadOnlyList<EdiMessageDto>> ImportAsync(string fileName, string content, CancellationToken cancellationToken = default)
    {
        var name = Path.GetFileName((fileName ?? "").Replace('\\', '/')).Trim();
        var results = await ProcessFileAsync(name.Length == 0 ? "upload.x12" : name, content ?? "", access.Email, cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var vendors = await VendorsAsync(db, results.Where(r => r.VendorId != null).Select(r => r.VendorId!.Value), cancellationToken);
        return results.Select(r => Dto(r, vendors)).ToList();
    }

    public async Task<IReadOnlyList<EdiMessageDto>> RetryAsync(Guid messageId, CancellationToken cancellationToken = default)
    {
        EdiMessage message;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            message = await db.Set<EdiMessage>().AsNoTracking().FirstOrDefaultAsync(m => m.Id == messageId, cancellationToken) ?? throw WorkbenchNotFoundException.For("EDI message", messageId);
        }

        if (message.Status != EdiMessageStatus.Failed)
        {
            throw new InvalidOperationException("Only failed messages can be tried again.");
        }

        if (message.Direction == EdiDirection.Outbound)
        {
            return message.DocumentId is { } poId ? [await SendPurchaseOrderAsync(poId, cancellationToken)] : throw new InvalidOperationException("That message has no purchase order to send.");
        }

        if (message.Content.Length == 0)
        {
            throw new InvalidOperationException("That message has no file content to apply (the inbox couldn't be read). Check for files again instead.");
        }

        return await ImportAsync($"retry of {message.FileName}", message.Content, cancellationToken);
    }

    /// <summary>Parses a file and applies each transaction set; one log row per set (or one for a file that can't be read).</summary>
    internal async Task<IReadOnlyList<EdiMessage>> ProcessFileAsync(string fileName, string content, string createdBy, CancellationToken ct)
    {
        X12Interchange interchange;
        try
        {
            interchange = X12.Parse(content);
        }
        catch (X12FormatException ex)
        {
            return [await LogAsync(new EdiMessage
            {
                Direction = EdiDirection.Inbound,
                TransactionSet = "?",
                FileName = Short(fileName, 255),
                Status = EdiMessageStatus.Failed,
                Error = Short($"Not a readable X12 file: {ex.Message}"),
                Content = Cap(content),
                CreatedBy = createdBy,
                CreatedAtUtc = UtcNow,
            }, ct)];
        }

        EdiPartner? partner;
        await using (var db = await dbFactory.CreateAsync(ct))
        {
            partner = await db.Set<EdiPartner>().AsNoTracking()
                .FirstOrDefaultAsync(p => p.InterchangeQualifier == interchange.SenderQualifier && p.InterchangeId == interchange.SenderId.ToUpperInvariant() && p.IsActive, ct);
        }

        var results = new List<EdiMessage>();
        foreach (var group in interchange.Groups)
        {
            foreach (var set in group.Sets)
            {
                results.Add(await ApplySetAsync(interchange, group, set, partner, fileName, createdBy, ct));
            }
        }

        if (results.Count == 0)
        {
            results.Add(await LogAsync(new EdiMessage
            {
                Direction = EdiDirection.Inbound,
                TransactionSet = "?",
                PartnerId = partner?.Id,
                VendorId = partner?.VendorId,
                ControlNumber = interchange.ControlNumber,
                FileName = Short(fileName, 255),
                Status = EdiMessageStatus.Failed,
                Error = "The file has no transaction sets.",
                Content = Cap(content),
                CreatedBy = createdBy,
                CreatedAtUtc = UtcNow,
            }, ct));
        }

        return results;
    }

    private async Task<EdiMessage> ApplySetAsync(X12Interchange interchange, X12FunctionalGroup group, X12TransactionSet set, EdiPartner? partner,
        string fileName, string createdBy, CancellationToken ct)
    {
        var message = new EdiMessage
        {
            Direction = EdiDirection.Inbound,
            TransactionSet = set.Id.Length <= 3 ? set.Id : set.Id[..3],
            PartnerId = partner?.Id,
            VendorId = partner?.VendorId,
            ControlNumber = Short($"{interchange.ControlNumber}/{set.ControlNumber}", 20),
            FileName = Short(fileName, 255),
            Content = Cap(X12.Single(interchange, group, set)),
            CreatedBy = createdBy,
            CreatedAtUtc = UtcNow,
        };
        try
        {
            if (partner is null)
            {
                throw new ArgumentException($"No active EDI partner has interchange id {interchange.SenderQualifier}/{interchange.SenderId}.");
            }

            if (!string.Equals(interchange.ReceiverId, Options.InterchangeId, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The file is addressed to {interchange.ReceiverId}, not to us ({Options.InterchangeId}).");
            }

            await using var db = await dbFactory.CreateAsync(ct);
            var earlier = await db.Set<EdiMessage>().AsNoTracking()
                .Where(m => m.PartnerId == partner.Id && m.Direction == EdiDirection.Inbound && m.ControlNumber == message.ControlNumber && m.Status == EdiMessageStatus.Processed)
                .Select(m => new { m.CreatedAtUtc, m.DocumentNumber }).FirstOrDefaultAsync(ct);
            if (earlier is not null)
            {
                message.Status = EdiMessageStatus.Duplicate;
                message.DocumentNumber = earlier.DocumentNumber;
                message.Error = $"Already processed on {earlier.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC; not applied again.";
                return await LogAsync(message, ct);
            }

            var vendor = await PurchasingSupport.FindVendorAsync(db, partner.VendorId, ct);
            var actor = SupplierDocuments.EdiActor(vendor);
            switch (set.Id)
            {
                case "855":
                {
                    var doc = X12Documents.Read855(set);
                    var po = await SupplierDocuments.LoadOrderByNumberAsync(db, vendor.Id, doc.PurchaseOrderNumber, ct);
                    var lines = doc.Lines.Select(l =>
                    {
                        var line = PoLine(po, l.LineNumber);
                        return new AcknowledgeLineRequest(line.Id, l.PromisedDate, l.Quantity, l.UnitPrice, l.Code is "IR" ? "Rejected (IR)" : null);
                    }).ToList();
                    var ack = await documents.AcknowledgeAsync(db, vendor.Id, po, new AcknowledgeOrderRequest(doc.Status, lines, doc.Note ?? (doc.Status == AcknowledgementState.Rejected ? "Rejected by EDI 855." : null)),
                        SupplierDocumentSource.Edi, actor, ct);
                    await db.SaveChangesAsync(ct);
                    (message.DocumentNumber, message.DocumentId) = (po.Number, po.Id);
                    if (ack.Status != PurchaseOrderAcknowledgementStatus.Accepted)
                    {
                        await documents.NotifyBuyersAsync(po, "purchasing.po_acknowledged",
                            $"{vendor.Name} {(ack.Status == PurchaseOrderAcknowledgementStatus.Rejected ? "rejected" : "accepted with changes")} {po.Number} (EDI)", ack.Note, $"purchasing/orders/{po.Id}");
                    }

                    break;
                }

                case "856":
                {
                    var doc = X12Documents.Read856(set);
                    var po = await SupplierDocuments.LoadOrderByNumberAsync(db, vendor.Id, doc.PurchaseOrderNumber, ct);
                    var notice = await documents.ShipAsync(db, vendor.Id, po, new ShipNoticeRequest(doc.ShipDate,
                        doc.Lines.Select(l => new ShipNoticeLineRequest(PoLine(po, l.LineNumber).Id, l.Quantity)).ToList(),
                        doc.ExpectedDelivery, doc.Carrier, doc.TrackingNumber, doc.ShipmentId), SupplierDocumentSource.Edi, actor, ct);
                    await db.SaveChangesAsync(ct);
                    (message.DocumentNumber, message.DocumentId) = (notice.Number, notice.Id);
                    break;
                }

                case "810":
                {
                    var doc = X12Documents.Read810(set);
                    var po = await SupplierDocuments.LoadOrderByNumberAsync(db, vendor.Id, doc.PurchaseOrderNumber, ct);
                    var lines = doc.Lines.Select(l => (Line: PoLine(po, l.LineNumber), l.Quantity, l.UnitPrice)).ToList();
                    var prices = lines.Where(l => l.UnitPrice is > 0).GroupBy(l => l.Line.Id).ToDictionary(g => g.Key, g => g.Last().UnitPrice!.Value);
                    var computed = lines.Sum(l => ProcurementSupport.Extend(l.Quantity, l.UnitPrice ?? l.Line.UnitPrice));
                    var (bill, _) = await documents.InvoiceAsync(db, vendor, po,
                        new SupplierInvoiceRequest(po.Id, doc.InvoiceNumber, doc.InvoiceDate, doc.Total ?? computed,
                            lines.Select(l => new SupplierInvoiceLineRequest(l.Line.Id, l.Quantity)).ToList()),
                        SupplierDocumentSource.Edi, actor, prices, null, ct);
                    await db.SaveChangesAsync(ct);
                    (message.DocumentNumber, message.DocumentId) = (bill.Number, bill.Id);
                    await documents.NotifyBuyersAsync(po, "purchasing.supplier_invoice", $"{vendor.Name} sent invoice {bill.InvoiceNumber} for {po.Number} (EDI)",
                        $"Draft bill {bill.Number}, {bill.Amount:N2}", $"purchasing/bills/{bill.Id}");
                    break;
                }

                default:
                    throw new ArgumentException($"Workbench doesn't accept {set.Id} documents (only 855, 856 and 810).");
            }

            message.Status = EdiMessageStatus.Processed;
        }
        catch (Exception ex) when (ex is X12FormatException or ArgumentException or InvalidOperationException or WorkbenchNotFoundException or DbUpdateException)
        {
            message.Status = EdiMessageStatus.Failed;
            message.Error = Short(ex.Message);
        }

        return await LogAsync(message, ct);
    }

    private static PurchaseOrderLine PoLine(PurchaseOrder po, int lineNumber) =>
        po.Lines.FirstOrDefault(l => l.LineNumber == lineNumber) ?? throw new ArgumentException($"{po.Number} has no line {lineNumber}.");

    // ---- Helpers ----

    private IEdiTransport Transport(EdiPartner partner) =>
        transports.LastOrDefault(t => t.Kind == partner.Transport) ?? throw new InvalidOperationException($"No {partner.Transport} transport is registered.");

    private async Task<EdiMessage> LogAsync(EdiMessage message, CancellationToken ct)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        db.Set<EdiMessage>().Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    private static async Task<Dictionary<Guid, Vendor>> VendorsAsync(IWorkbenchDbContext db, IEnumerable<Guid> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await db.Set<Vendor>().AsNoTracking().Where(v => list.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
    }

    private static EdiMessageDto Dto(EdiMessage m, IReadOnlyDictionary<Guid, Vendor> vendors) => new(
        m.Id, (EdiFlow)(int)m.Direction, m.TransactionSet, m.VendorId is { } v ? vendors.GetValueOrDefault(v)?.Name : null, m.ControlNumber, m.FileName,
        (EdiOutcome)(int)m.Status, m.Error, m.DocumentNumber, m.DocumentId, m.CreatedBy, m.CreatedAtUtc);

    private static EdiPartnerDto PartnerDto(EdiPartner p, Vendor v) => new(p.Id, v.Id, v.Code, v.Name, p.InterchangeQualifier, p.InterchangeId, p.ApplicationCode,
        (EdiTransportType)(int)p.Transport, p.Address, p.SendPurchaseOrders, p.IsActive, p.UpdatedAtUtc);

    private static object Snapshot(EdiPartner p) => new { p.VendorId, p.InterchangeQualifier, p.InterchangeId, p.ApplicationCode, Transport = p.Transport.ToString(), p.Address, p.SendPurchaseOrders, p.IsActive };

    private static string Cap(string content) => content.Length <= EdiMessage.MaxContentLength ? content : content[..EdiMessage.MaxContentLength];

    private static string Short(string value, int max = 2000) => value.Length <= max ? value : value[..max];

    [GeneratedRegex("^[A-Z0-9][A-Z0-9 ]{0,14}$")]
    private static partial Regex InterchangeIdPattern();

    [GeneratedRegex("^[A-Z0-9]{2}$")]
    private static partial Regex QualifierPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]([A-Za-z0-9.-]{0,251}[A-Za-z0-9])?$")]
    private static partial Regex HostPattern();
}

/// <summary>
/// Sends an 850 when a purchase order is issued (or changed by a change order) to a vendor with an active EDI partner
/// that takes purchase orders. Never blocks the order: a failure is a Failed row in the EDI log.
/// </summary>
public sealed class EdiSendingPurchaseOrderService(IPurchaseOrderService inner, EdiService edi, AccessControl access, ILogger<EdiSendingPurchaseOrderService> logger)
    : IPurchaseOrderService
{
    private async Task SendAsync(PurchaseOrderDto po, CancellationToken ct)
    {
        try
        {
            if (await edi.ShouldSendAsync(po.Order.VendorId, ct))
            {
                await edi.SendPurchaseOrderCoreAsync(po.Order.Id, access.Email, requirePartner: false, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "The EDI 850 for an issued purchase order failed.");
        }
    }

    public async Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default)
    {
        var po = await inner.IssueAsync(purchaseOrderId, cancellationToken);
        await SendAsync(po, cancellationToken);
        return po;
    }

    public async Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default)
    {
        var po = await inner.ChangeOrderAsync(purchaseOrderId, request, cancellationToken);
        await SendAsync(po, cancellationToken);
        return po;
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

    public Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default) =>
        inner.ReceiveAsync(purchaseOrderId, request, cancellationToken);

    public Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default) => inner.ListReceivableAsync(cancellationToken);

    public Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default) => inner.CloseAsync(purchaseOrderId, cancellationToken);

    public Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default) =>
        inner.CancelAsync(purchaseOrderId, request, cancellationToken);
}

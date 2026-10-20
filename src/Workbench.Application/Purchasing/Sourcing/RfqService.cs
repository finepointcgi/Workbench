using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.Application.Identity;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Materials;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>
/// RFQs: build from approved requisition lines or by hand, invite vendors, record quotes (buyers here, vendors on the
/// supplier portal), compare, and award by line into purchase orders (one per vendor, through
/// <see cref="IPurchaseOrderService"/> so ceilings and permissions apply) with the competition record for the file.
/// </summary>
public sealed class RfqService(
    IWorkbenchDbContextFactory dbFactory,
    AuditRecorder audit,
    AccessControl access,
    IPurchaseOrderService orders,
    SupplierPortalMail mail,
    IOptions<SourcingOptions> options,
    TimeProvider clock) : IRfqService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    private DateOnly Today => DateOnly.FromDateTime(UtcNow);

    private string Me => access.Email;

    public async Task<IReadOnlyList<RfqSummaryDto>> ListAsync(RfqQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var q = db.Set<Rfq>().AsNoTracking().Include(r => r.Lines).AsQueryable();
        if (query.Status is { } status)
        {
            var s = status.ToDomain();
            q = q.Where(r => r.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            q = q.Where(r => r.Number.Contains(term) || r.Title.Contains(term) || r.Lines.Any(l => l.Description.Contains(term)));
        }

        var rfqs = (await q.ToListAsync(cancellationToken)).OrderByDescending(r => r.CreatedAtUtc).Take(1000).ToList();
        return await SummariesAsync(db, rfqs, cancellationToken);
    }

    public async Task<RfqDto> GetAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await DetailAsync(db, rfqId, cancellationToken);
    }

    public async Task<IReadOnlyList<OrderableRequisitionLineDto>> ListSourceableRequisitionLinesAsync(CancellationToken cancellationToken = default)
    {
        var orderable = await orders.ListOrderableRequisitionLinesAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sourcing = await SourcingRequisitionLinesAsync(db, null, cancellationToken);
        return orderable.Where(o => !sourcing.Contains(o.Line.Id)).ToList();
    }

    public async Task<RfqDto> CreateAsync(SaveRfqRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var numbers = await db.Set<Rfq>().Select(r => r.Number).ToListAsync(cancellationToken);
        var rfq = new Rfq
        {
            Number = PurchasingSupport.NextNumber(numbers, "RFQ"),
            Title = "",
            CreatedBy = Me,
            CreatedAtUtc = UtcNow,
        };
        var (lines, invitations) = await ApplyAsync(db, rfq, request, cancellationToken);
        rfq.Lines.AddRange(lines);
        db.Set<Rfq>().Add(rfq);
        db.Set<RfqVendor>().AddRange(invitations);
        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "Created", after: Snapshot(rfq, request.VendorIds));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    public async Task<RfqDto> UpdateAsync(Guid rfqId, SaveRfqRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
        if (rfq.Status != RfqStatus.Draft)
        {
            throw new InvalidOperationException($"{rfq.Number} has been sent to vendors; only drafts can be edited (invite more vendors instead).");
        }

        var oldVendors = await db.Set<RfqVendor>().Where(v => v.RfqId == rfq.Id).ToListAsync(cancellationToken);
        var before = Snapshot(rfq, oldVendors.Select(v => v.VendorId));
        var (lines, invitations) = await ApplyAsync(db, rfq, request, cancellationToken);

        // Valid: replace the lines and invitations (the old ones go first; the unique keys would clash otherwise).
        db.Set<RfqLine>().RemoveRange(rfq.Lines);
        rfq.Lines.Clear();
        db.Set<RfqVendor>().RemoveRange(oldVendors);
        await db.SaveChangesAsync(cancellationToken);
        rfq.Lines.AddRange(lines);
        db.Set<RfqLine>().AddRange(lines);
        db.Set<RfqVendor>().AddRange(invitations);
        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "Updated", before, Snapshot(rfq, request.VendorIds));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    public async Task<RfqDto> IssueAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
        if (rfq.Status != RfqStatus.Draft)
        {
            throw new InvalidOperationException($"{rfq.Number} is already {SourcingSupport.Label(rfq.Status)}.");
        }

        if (rfq.DueDate < Today)
        {
            throw new ArgumentException("The due date has passed; choose a later one before sending.");
        }

        var vendorIds = await db.Set<RfqVendor>().Where(v => v.RfqId == rfq.Id).Select(v => v.VendorId).ToListAsync(cancellationToken);
        foreach (var vendor in await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToListAsync(cancellationToken))
        {
            PurchasingSupport.EnsureVendorCanTransact(vendor, "asking for quotes");
        }

        rfq.Status = RfqStatus.Open;
        rfq.IssuedBy = Me;
        rfq.IssuedAtUtc = UtcNow;
        foreach (var invitation in await db.Set<RfqVendor>().Where(v => v.RfqId == rfq.Id).ToListAsync(cancellationToken))
        {
            invitation.InvitedAtUtc = UtcNow;
        }

        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "Issued", after: new { rfq.DueDate, Vendors = vendorIds.Count });
        await db.SaveChangesAsync(cancellationToken);
        await mail.RfqInvitationAsync(db, rfq, vendorIds, cancellationToken);
        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    public async Task<RfqDto> InviteVendorAsync(Guid rfqId, Guid vendorId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
        if (rfq.Status is RfqStatus.Awarded or RfqStatus.Cancelled)
        {
            throw new InvalidOperationException($"{rfq.Number} is {SourcingSupport.Label(rfq.Status)}.");
        }

        var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, cancellationToken);
        PurchasingSupport.EnsureVendorCanTransact(vendor, "asking for quotes");
        if (await db.Set<RfqVendor>().AnyAsync(v => v.RfqId == rfq.Id && v.VendorId == vendorId, cancellationToken))
        {
            throw new ArgumentException($"{vendor.Name} is already invited.");
        }

        db.Set<RfqVendor>().Add(new RfqVendor { RfqId = rfq.Id, VendorId = vendorId, InvitedAtUtc = UtcNow });
        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "VendorInvited", after: new { Vendor = vendor.Code });
        await db.SaveChangesAsync(cancellationToken);
        if (rfq.Status != RfqStatus.Draft)
        {
            await mail.RfqInvitationAsync(db, rfq, [vendorId], cancellationToken);
        }

        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    public async Task<RfqDto> RecordQuoteAsync(Guid rfqId, SaveQuoteRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
        var vendor = await PurchasingSupport.FindVendorAsync(db, request.VendorId, cancellationToken);
        var quote = await SourcingSupport.SaveQuoteAsync(db, rfq, vendor.Id, request, VendorQuoteSource.Buyer, Me, UtcNow, cancellationToken);
        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "QuoteRecorded", after: QuoteSnapshot(vendor, quote));
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    public async Task<RfqComparisonDto> GetComparisonAsync(Guid rfqId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken, track: false);
        return await ComparisonAsync(db, rfq, cancellationToken);
    }

    public async Task<RfqDto> AwardAsync(Guid rfqId, AwardRfqRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var reasonableness = PurchasingSupport.Required(request.PriceReasonableness, "A price reasonableness note (why the price is fair and reasonable)", 4000);
        var soleSourceJustification = PurchasingSupport.Optional(request.SoleSourceJustification, "The sole-source justification", 4000);
        var rationale = PurchasingSupport.Optional(request.Rationale, "The award rationale", 4000);
        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw new ArgumentException("Choose a quote for at least one line.");
        }

        if (request.Lines.GroupBy(l => l.RfqLineId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A line is awarded twice.");
        }

        // Check everything first, then create the orders, then record the award.
        List<(RfqLine Line, VendorQuote Quote, VendorQuoteLine QuoteLine, decimal Price)> awarded = [];
        Rfq rfq;
        RfqComparisonDto comparison;
        int vendorsInvited;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken, track: false);
            if (!SourcingSupport.QuotableStatuses.Contains(rfq.Status))
            {
                throw new InvalidOperationException($"{rfq.Number} is {SourcingSupport.Label(rfq.Status)}; it can't be awarded.");
            }

            var quotes = await SourcingSupport.QuotesAsync(db, rfq.Id, cancellationToken);
            comparison = await ComparisonAsync(db, rfq, cancellationToken);
            vendorsInvited = await db.Set<RfqVendor>().CountAsync(v => v.RfqId == rfq.Id, cancellationToken);
            var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => quotes.Select(q => q.VendorId).Contains(v.Id)).ToDictionaryAsync(v => v.Id, cancellationToken);
            foreach (var pick in request.Lines)
            {
                var line = rfq.Lines.FirstOrDefault(l => l.Id == pick.RfqLineId) ?? throw new ArgumentException("An awarded line isn't on this RFQ.");
                var prefix = $"Line {line.LineNumber}";
                if (line.AwardedQuoteLineId is not null)
                {
                    throw new ArgumentException($"{prefix} is already awarded.");
                }

                var quote = quotes.FirstOrDefault(q => q.Lines.Any(l => l.Id == pick.QuoteLineId))
                    ?? throw new ArgumentException($"{prefix}: that quote isn't on this RFQ.");
                var quoteLine = quote.Lines.First(l => l.Id == pick.QuoteLineId);
                if (quoteLine.RfqLineId != line.Id)
                {
                    throw new ArgumentException($"{prefix}: that quote line is for another line.");
                }

                if (quoteLine.NoBid)
                {
                    throw new ArgumentException($"{prefix}: {vendors[quote.VendorId].Name} didn't bid on it.");
                }

                if (SourcingSupport.IsExpired(quote, Today))
                {
                    throw new ArgumentException($"{prefix}: {vendors[quote.VendorId].Name}'s quote expired on {quote.ValidUntil:yyyy-MM-dd}. Ask them to confirm or renew it first.");
                }

                PurchasingSupport.EnsureVendorCanTransact(vendors[quote.VendorId], "ordering");
                if (line.RequisitionLineId is { } rlId)
                {
                    var still = await db.Set<PurchaseRequisitionLine>().AsNoTracking()
                        .Where(l => l.Id == rlId && l.PurchaseOrderLineId == null)
                        .Join(db.Set<PurchaseRequisition>(), l => l.RequisitionId, r => r.Id, (l, r) => r.Status).FirstOrDefaultAsync(cancellationToken);
                    if (still != RequisitionStatus.Approved)
                    {
                        throw new ArgumentException($"{prefix}: its requisition line has been ordered or is no longer approved.");
                    }
                }

                awarded.Add((line, quote, quoteLine, SourcingSupport.EffectivePrice(quoteLine, line.Quantity)));
            }

            // The competition rules for the file.
            var cells = awarded.Select(a => comparison.Lines.First(l => l.RfqLineId == a.Line.Id)).ToList();
            if (cells.Any(c => c.ResponsiveQuotes < 2) && soleSourceJustification is null)
            {
                var lines = string.Join(", ", awarded.Where(a => comparison.Lines.First(l => l.RfqLineId == a.Line.Id).ResponsiveQuotes < 2).Select(a => a.Line.LineNumber));
                throw new ArgumentException($"Only one quote came in for line {lines}: enter a sole-source justification (why competition wasn't possible or practical).");
            }

            var notLowest = awarded.Where(a => comparison.Lines.First(l => l.RfqLineId == a.Line.Id).Cells.First(c => c.QuoteLineId == a.QuoteLine.Id).IsLowestPrice == false).ToList();
            if (notLowest.Count > 0 && rationale is null)
            {
                throw new ArgumentException($"Line {string.Join(", ", notLowest.Select(a => a.Line.LineNumber))} isn't awarded to the lowest price: say why (the award rationale).");
            }
        }

        // One purchase order per vendor, through the PO service (ceilings and permissions apply). A failure cancels the ones made.
        var orderDate = request.OrderDate ?? Today;
        var created = new List<(Guid VendorId, PurchaseOrderDto Order, List<RfqLine> Lines)>();
        try
        {
            foreach (var group in awarded.GroupBy(a => a.Quote.VendorId))
            {
                var items = group.OrderBy(a => a.Line.LineNumber).ToList();
                var quote = items[0].Quote;
                var po = await orders.CreateAsync(new SavePurchaseOrderRequest(
                    group.Key,
                    orderDate,
                    items.Select(a => new SavePurchaseOrderLineRequest(a.Line.Description, a.Line.Quantity, a.Price, a.Line.WbsElementId, a.Line.OdcType?.ToContract(),
                        a.Line.AccountId, a.Line.OrgUnitId, a.Line.NeededBy)).ToList(),
                    Terms: quote.Terms ?? rfq.Terms,
                    ShipTo: rfq.ShipTo,
                    Notes: $"Awarded from {rfq.Number}" + (quote.Reference is { } r ? $", quote {r}" : ""),
                    Receivers: request.Receivers), cancellationToken);
                created.Add((group.Key, po, items.Select(a => a.Line).ToList()));
            }
        }
        catch
        {
            foreach (var c in created)
            {
                await orders.CancelAsync(c.Order.Order.Id, new PurchaseOrderReasonRequest($"The award of {rfq.Number} didn't complete."), CancellationToken.None);
            }

            throw;
        }

        Guid awardId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var tracked = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
            var poIds = created.Select(c => c.Order.Order.Id).ToList();
            var poLines = await db.Set<PurchaseOrderLine>().Where(l => poIds.Contains(l.PurchaseOrderId)).ToListAsync(cancellationToken);
            var vendorNames = await db.Set<Vendor>().AsNoTracking().Where(v => created.Select(c => c.VendorId).Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.Name, cancellationToken);
            var awardLines = new List<RfqAwardLineDto>();
            var award = new RfqAward
            {
                RfqId = rfq.Id,
                AwardedBy = Me,
                AwardedAtUtc = UtcNow,
                VendorsInvited = vendorsInvited,
                QuotesReceived = awarded.Min(a => comparison.Lines.First(l => l.RfqLineId == a.Line.Id).ResponsiveQuotes),
                SoleSourceJustification = soleSourceJustification,
                PriceReasonableness = reasonableness,
                Rationale = rationale,
                AwardedTotal = 0,
                ComparisonJson = "",
                PurchaseOrderNumbers = string.Join(", ", created.Select(c => c.Order.Order.Number)),
            };
            award.SoleSource = award.QuotesReceived < 2;
            foreach (var c in created)
            {
                var lines = poLines.Where(l => l.PurchaseOrderId == c.Order.Order.Id).OrderBy(l => l.LineNumber).ToList();
                foreach (var (rfqLine, poLine) in c.Lines.Zip(lines))
                {
                    var a = awarded.First(x => x.Line.Id == rfqLine.Id);
                    var line = tracked.Lines.First(l => l.Id == rfqLine.Id);
                    line.AwardedQuoteLineId = a.QuoteLine.Id;
                    line.AwardId = award.Id;
                    line.PurchaseOrderLineId = poLine.Id;
                    var cmp = comparison.Lines.First(l => l.RfqLineId == rfqLine.Id);
                    awardLines.Add(new RfqAwardLineDto(line.LineNumber, line.Description, vendorNames[c.VendorId], line.Quantity, a.Price, poLine.Amount,
                        cmp.Cells.First(x => x.QuoteLineId == a.QuoteLine.Id).IsLowestPrice, cmp.ResponsiveQuotes));

                    // A sourced requisition line is ordered now, as when ordering it directly.
                    if (line.RequisitionLineId is { } rlId)
                    {
                        poLine.RequisitionLineId = rlId;
                        var rl = await db.Set<PurchaseRequisitionLine>().FirstAsync(l => l.Id == rlId, cancellationToken);
                        rl.PurchaseOrderLineId = poLine.Id;
                        var req = await db.Set<PurchaseRequisition>().Include(r => r.Lines).FirstAsync(r => r.Id == rl.RequisitionId, cancellationToken);
                        if (req.Status == RequisitionStatus.Approved && req.Lines.All(l => l.PurchaseOrderLineId != null))
                        {
                            req.Status = RequisitionStatus.Ordered;
                            audit.Record(db, ProcurementSupport.RequisitionEntity, req.Id, "Ordered", after: new { PurchaseOrder = c.Order.Order.Number, Rfq = rfq.Number });
                        }
                    }
                }
            }

            award.AwardedTotal = awardLines.Sum(l => l.Amount);
            award.ComparisonJson = JsonSerializer.Serialize(new AwardEvidence(comparison, awardLines.OrderBy(l => l.LineNumber).ToList()), Json);
            db.Set<RfqAward>().Add(award);
            tracked.Status = tracked.Lines.All(l => l.AwardedQuoteLineId != null) ? RfqStatus.Awarded : RfqStatus.PartiallyAwarded;
            audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "Awarded", after: new
            {
                award.PurchaseOrderNumbers,
                award.AwardedTotal,
                award.QuotesReceived,
                award.SoleSource,
                Lines = awardLines.Select(l => new { l.LineNumber, l.VendorName, l.UnitPrice }),
            });
            await db.SaveChangesAsync(cancellationToken);
            awardId = award.Id;
        }

        if (request.IssueOrders)
        {
            foreach (var c in created)
            {
                await orders.IssueAsync(c.Order.Order.Id, cancellationToken);
            }
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            return await DetailAsync(db, rfqId, cancellationToken);
        }
    }

    public async Task<RfqDto> CancelAsync(Guid rfqId, RfqReasonRequest request, CancellationToken cancellationToken = default)
    {
        var reason = PurchasingSupport.Required(request?.Reason, "A reason", 1000);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rfq = await SourcingSupport.LoadAsync(db, rfqId, cancellationToken);
        if (rfq.Status is not (RfqStatus.Draft or RfqStatus.Open))
        {
            throw new InvalidOperationException(rfq.Status == RfqStatus.Cancelled ? $"{rfq.Number} is already cancelled." : $"{rfq.Number} has awarded lines; it can't be cancelled.");
        }

        rfq.Status = RfqStatus.Cancelled;
        rfq.CancelReason = reason;
        audit.Record(db, SourcingSupport.RfqEntity, rfq.Id, "Cancelled", reason: reason);
        await db.SaveChangesAsync(cancellationToken);
        return await DetailAsync(db, rfq.Id, cancellationToken);
    }

    // ---- Helpers ----

    /// <summary>The frozen comparison and award lines kept with the award.</summary>
    internal sealed record AwardEvidence(RfqComparisonDto Comparison, IReadOnlyList<RfqAwardLineDto> Lines);

    /// <summary>Validates the request into <paramref name="rfq"/>'s header and returns the new lines and invitations (not yet added).</summary>
    private async Task<(List<RfqLine> Lines, List<RfqVendor> Invitations)> ApplyAsync(IWorkbenchDbContext db, Rfq rfq, SaveRfqRequest r, CancellationToken ct)
    {
        var newLines = new List<RfqLine>();
        var invitations = new List<RfqVendor>();
        rfq.Title = PurchasingSupport.Required(r.Title, "Title", 200);
        rfq.DueDate = r.DueDate;
        rfq.Terms = PurchasingSupport.Optional(r.Terms, "Terms", 300);
        rfq.ShipTo = PurchasingSupport.Optional(r.ShipTo, "Ship to", 500);
        rfq.Instructions = PurchasingSupport.Optional(r.Instructions, "Instructions to vendors", 2000);
        if (r.Lines is null || r.Lines.Count == 0)
        {
            throw new ArgumentException("An RFQ needs at least one line.");
        }

        if (r.Lines.Count > 100)
        {
            throw new ArgumentException("An RFQ can have at most 100 lines.");
        }

        var vendorIds = (r.VendorIds ?? []).Distinct().ToList();
        if (vendorIds.Count == 0)
        {
            throw new ArgumentException("Invite at least one vendor.");
        }

        if (vendorIds.Count > 25)
        {
            throw new ArgumentException("An RFQ can go to at most 25 vendors.");
        }

        foreach (var vendorId in vendorIds)
        {
            var vendor = await PurchasingSupport.FindVendorAsync(db, vendorId, ct);
            PurchasingSupport.EnsureVendorCanTransact(vendor, "asking for quotes");
            invitations.Add(new RfqVendor { RfqId = rfq.Id, VendorId = vendorId, InvitedAtUtc = UtcNow });
        }

        var sourcing = await SourcingRequisitionLinesAsync(db, rfq.Id, ct);
        var reqLines = r.Lines.Where(l => l.RequisitionLineId != null).Select(l => l.RequisitionLineId!.Value).ToList();
        if (reqLines.Distinct().Count() != reqLines.Count)
        {
            throw new ArgumentException("A requisition line is on the RFQ twice.");
        }

        foreach (var input in r.Lines)
        {
            var n = newLines.Count + 1;
            var prefix = $"Line {n}";
            RfqLine line;
            if (input.RequisitionLineId is { } rlId)
            {
                var rl = await db.Set<PurchaseRequisitionLine>().AsNoTracking().FirstOrDefaultAsync(l => l.Id == rlId, ct) ?? throw WorkbenchNotFoundException.For("Requisition line", rlId);
                var req = await db.Set<PurchaseRequisition>().AsNoTracking().FirstAsync(x => x.Id == rl.RequisitionId, ct);
                if (req.Status != RequisitionStatus.Approved || rl.PurchaseOrderLineId is not null)
                {
                    throw new ArgumentException($"{prefix}: {req.Number} line {rl.LineNumber} isn't an approved line waiting to be ordered.");
                }

                if (sourcing.Contains(rl.Id))
                {
                    throw new ArgumentException($"{prefix}: {req.Number} line {rl.LineNumber} is already on another open RFQ.");
                }

                line = new RfqLine
                {
                    RfqId = rfq.Id,
                    LineNumber = n,
                    Description = rl.Description,
                    Quantity = rl.Quantity,
                    NeededBy = rl.NeededBy,
                    EstimatedUnitCost = rl.UnitCost,
                    ProjectId = rl.ProjectId,
                    WbsElementId = rl.WbsElementId,
                    OdcType = rl.OdcType,
                    AccountId = rl.AccountId,
                    OrgUnitId = rl.OrgUnitId,
                    RequisitionLineId = rl.Id,
                    UnitOfMeasure = PurchasingSupport.Optional(input.UnitOfMeasure, $"{prefix}: unit", 8)?.ToUpperInvariant() ?? "EA",
                };
            }
            else
            {
                var charge = await ProcurementSupport.ValidateChargeAsync(db, prefix, input.WbsElementId, input.OdcType?.ToDomain(), input.AccountId, input.OrgUnitId, null, ct);
                line = new RfqLine
                {
                    RfqId = rfq.Id,
                    LineNumber = n,
                    Description = PurchasingSupport.Required(input.Description, $"{prefix}: description", 500),
                    Quantity = ProcurementSupport.Quantity(input.Quantity, $"{prefix}: quantity"),
                    UnitOfMeasure = PurchasingSupport.Optional(input.UnitOfMeasure, $"{prefix}: unit", 8)?.ToUpperInvariant() ?? "EA",
                    NeededBy = input.NeededBy,
                    EstimatedUnitCost = input.EstimatedUnitCost is { } est ? ProcurementSupport.UnitPrice(est, $"{prefix}: estimated unit cost") : null,
                    ProjectId = charge.ProjectId,
                    WbsElementId = charge.WbsElementId,
                    OdcType = charge.OdcType,
                    AccountId = charge.AccountId,
                    OrgUnitId = charge.OrgUnitId,
                };
            }

            if (input.PartId is { } partId)
            {
                var part = await db.Set<Part>().AsNoTracking().FirstOrDefaultAsync(p => p.Id == partId, ct) ?? throw WorkbenchNotFoundException.For("Part", partId);
                line.PartId = part.Id;
                if (input.UnitOfMeasure is null)
                {
                    line.UnitOfMeasure = part.UnitOfMeasure;
                }
            }

            newLines.Add(line);
        }

        return (newLines, invitations);
    }

    /// <summary>Requisition lines on RFQs that are still sourcing them (not cancelled, line not yet awarded).</summary>
    private static async Task<HashSet<Guid>> SourcingRequisitionLinesAsync(IWorkbenchDbContext db, Guid? exceptRfqId, CancellationToken ct) =>
        (await db.Set<RfqLine>().AsNoTracking()
            .Where(l => l.RequisitionLineId != null && l.PurchaseOrderLineId == null && l.RfqId != exceptRfqId)
            .Join(db.Set<Rfq>(), l => l.RfqId, r => r.Id, (l, r) => new { l.RequisitionLineId, r.Status })
            .Where(x => x.Status != RfqStatus.Cancelled && x.Status != RfqStatus.Awarded)
            .Select(x => x.RequisitionLineId!.Value).ToListAsync(ct)).ToHashSet();

    internal async Task<RfqComparisonDto> ComparisonAsync(IWorkbenchDbContext db, Rfq rfq, CancellationToken ct)
    {
        var settings = options.Value;
        var quotes = await SourcingSupport.QuotesAsync(db, rfq.Id, ct);
        var invited = await db.Set<RfqVendor>().AsNoTracking().Where(v => v.RfqId == rfq.Id).ToListAsync(ct);
        var vendorIds = invited.Select(v => v.VendorId).Union(quotes.Select(q => q.VendorId)).ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var performance = await SourcingSupport.PerformanceAsync(db, vendorIds, Today.AddMonths(-settings.PerformanceMonths), ct);

        // Vendors with quotes first (cheapest total first), then the rest by name.
        var ordered = vendorIds.OrderBy(id => quotes.Any(q => q.VendorId == id) ? 0 : 1)
            .ThenBy(id => QuoteTotal(rfq, quotes.FirstOrDefault(q => q.VendorId == id)) ?? decimal.MaxValue)
            .ThenBy(id => vendors[id].Name).ToList();
        var lines = rfq.Lines.OrderBy(l => l.LineNumber).Select(line =>
        {
            var cells = SourcingSupport.ScoreLine(line, ordered, quotes, performance, settings, Today);
            return new ComparisonLineDto(line.Id, line.LineNumber, line.Description, line.Quantity, line.UnitOfMeasure, line.NeededBy, line.EstimatedUnitCost,
                cells.Count(c => c.Responsive),
                cells.Select(c => new ComparisonCellDto(c.VendorId, c.Line?.Id, c.Line is not null, c.Line?.NoBid ?? false, c.Price, c.Extended, c.Lead,
                    c.Score, c.Lowest, c.Recommended, c.Expired,
                    line.NeededBy is { } need && c.Lead is { } lead && Today.AddDays(lead) > need)).ToList(),
                line.AwardedQuoteLineId);
        }).ToList();
        var vendorRows = ordered.Select(id =>
        {
            var v = vendors[id];
            var perf = performance.GetValueOrDefault(id);
            var quote = quotes.FirstOrDefault(q => q.VendorId == id);
            return new VendorPerformanceDto(id, v.Code, v.Name, v.Status == VendorStatus.OnHold, perf?.Measured ?? 0, perf?.OnTime ?? 0, perf?.Percent,
                quote is not null, QuoteTotal(rfq, quote), quote?.ValidUntil, quote is not null && SourcingSupport.IsExpired(quote, Today));
        }).ToList();
        return new RfqComparisonDto(rfq.Id, rfq.Number, rfq.Title, rfq.Status.ToContract(),
            new ScoringWeightsDto(settings.PriceWeight, settings.LeadTimeWeight, settings.DeliveryWeight), vendorRows, lines);
    }

    private static decimal? QuoteTotal(Rfq rfq, VendorQuote? quote) =>
        quote is null ? null : quote.Lines.Where(l => !l.NoBid).Sum(l =>
            rfq.Lines.FirstOrDefault(r => r.Id == l.RfqLineId) is { } line ? ProcurementSupport.Extend(line.Quantity, SourcingSupport.EffectivePrice(l, line.Quantity)) : 0);

    private static object Snapshot(Rfq r, IEnumerable<Guid>? vendorIds) => new
    {
        r.Number,
        r.Title,
        r.DueDate,
        r.Terms,
        r.ShipTo,
        Vendors = vendorIds?.ToList(),
        Lines = r.Lines.Select(l => new { l.LineNumber, l.Description, l.Quantity, l.UnitOfMeasure, l.NeededBy, l.WbsElementId, l.AccountId, l.RequisitionLineId }),
    };

    internal static object QuoteSnapshot(Vendor vendor, VendorQuote q) => new
    {
        Vendor = vendor.Code,
        q.Reference,
        q.QuoteDate,
        q.ValidUntil,
        q.LeadTimeDays,
        q.Source,
        q.SubmittedBy,
        Lines = q.Lines.Select(l => new { l.RfqLineId, l.NoBid, l.UnitPrice, l.LeadTimeDays, Breaks = l.PriceBreaks.Select(b => new { b.MinimumQuantity, b.UnitPrice }) }),
    };

    // ---- DTOs ----

    private async Task<IReadOnlyList<RfqSummaryDto>> SummariesAsync(IWorkbenchDbContext db, IReadOnlyList<Rfq> rfqs, CancellationToken ct)
    {
        var ids = rfqs.Select(r => r.Id).ToList();
        var invited = await db.Set<RfqVendor>().AsNoTracking().Where(v => ids.Contains(v.RfqId)).GroupBy(v => v.RfqId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var quoted = await db.Set<VendorQuote>().AsNoTracking().Where(q => ids.Contains(q.RfqId)).GroupBy(q => q.RfqId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var awards = await db.Set<RfqAward>().AsNoTracking().Where(a => ids.Contains(a.RfqId)).ToListAsync(ct);
        return rfqs.Select(r => new RfqSummaryDto(r.Id, r.Number, r.Title, r.Status.ToContract(), r.DueDate, r.Lines.Count,
            invited.GetValueOrDefault(r.Id), quoted.GetValueOrDefault(r.Id), r.CreatedAtUtc,
            awards.Where(a => a.RfqId == r.Id).OrderBy(a => a.AwardedAtUtc).SelectMany(a => SplitNumbers(a.PurchaseOrderNumbers)).ToList())).ToList();
    }

    private static IEnumerable<string> SplitNumbers(string numbers) => numbers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private async Task<RfqDto> DetailAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct)
    {
        var rfq = await SourcingSupport.LoadAsync(db, id, ct, track: false);
        var lines = rfq.Lines.OrderBy(l => l.LineNumber).ToList();
        var lookups = await ProcurementSupport.Lookups.LoadAsync(db, lines.Select(l => l.ProjectId), lines.Select(l => l.WbsElementId),
            lines.Select(l => l.AccountId), lines.Select(l => l.OrgUnitId), [], ct);
        var partIds = lines.Where(l => l.PartId != null).Select(l => l.PartId!.Value).ToList();
        var parts = await db.Set<Part>().AsNoTracking().Where(p => partIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.Number, ct);
        var reqLineIds = lines.Where(l => l.RequisitionLineId != null).Select(l => l.RequisitionLineId!.Value).ToList();
        var reqNumbers = await db.Set<PurchaseRequisitionLine>().AsNoTracking().Where(l => reqLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseRequisition>(), l => l.RequisitionId, r => r.Id, (l, r) => new { l.Id, r.Number }).ToDictionaryAsync(x => x.Id, x => x.Number, ct);
        var poLineIds = lines.Where(l => l.PurchaseOrderLineId != null).Select(l => l.PurchaseOrderLineId!.Value).ToList();
        var poByLine = await db.Set<PurchaseOrderLine>().AsNoTracking().Where(l => poLineIds.Contains(l.Id))
            .Join(db.Set<PurchaseOrder>(), l => l.PurchaseOrderId, o => o.Id, (l, o) => new { l.Id, OrderId = o.Id, o.Number }).ToDictionaryAsync(x => x.Id, ct);
        var invitations = await db.Set<RfqVendor>().AsNoTracking().Where(v => v.RfqId == id).ToListAsync(ct);
        var quotes = await SourcingSupport.QuotesAsync(db, id, ct);
        var vendorIds = invitations.Select(v => v.VendorId).Union(quotes.Select(q => q.VendorId)).ToList();
        var vendors = await db.Set<Vendor>().AsNoTracking().Where(v => vendorIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);
        var contacts = await db.Set<SupplierContact>().AsNoTracking().Where(c => vendorIds.Contains(c.VendorId) && c.IsActive)
            .GroupBy(c => c.VendorId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var edi = await db.Set<EdiPartner>().AsNoTracking().Where(p => vendorIds.Contains(p.VendorId) && p.IsActive).Select(p => p.VendorId).ToListAsync(ct);
        var quoteLineVendor = quotes.SelectMany(q => q.Lines.Select(l => (l.Id, Quote: q, Line: l))).ToDictionary(x => x.Id);
        var awards = await db.Set<RfqAward>().AsNoTracking().Where(a => a.RfqId == id).OrderBy(a => a.AwardedAtUtc).ToListAsync(ct);
        var summary = (await SummariesAsync(db, [rfq], ct))[0];

        return new RfqDto(
            summary,
            rfq.Terms,
            rfq.ShipTo,
            rfq.Instructions,
            rfq.CreatedBy,
            rfq.IssuedBy,
            rfq.IssuedAtUtc,
            rfq.CancelReason,
            lines.Select(l =>
            {
                var won = l.AwardedQuoteLineId is { } ql && quoteLineVendor.TryGetValue(ql, out var w) ? w : default;
                var po = l.PurchaseOrderLineId is { } pl ? poByLine.GetValueOrDefault(pl) : null;
                return new RfqLineDto(l.Id, l.LineNumber, l.Description, l.PartId, l.PartId is { } p ? parts.GetValueOrDefault(p) : null, l.Quantity, l.UnitOfMeasure,
                    l.NeededBy, l.EstimatedUnitCost, l.ProjectId, lookups.Project(l.ProjectId), l.WbsElementId, lookups.WbsCode(l.WbsElementId), l.OdcType?.ToContract(),
                    l.AccountId, lookups.Account(l.AccountId), l.OrgUnitId, lookups.Org(l.OrgUnitId), l.RequisitionLineId,
                    l.RequisitionLineId is { } r ? reqNumbers.GetValueOrDefault(r) : null,
                    l.AwardedQuoteLineId, won.Quote is { } wq ? vendors[wq.VendorId].Name : null,
                    won.Line is { } wl ? SourcingSupport.EffectivePrice(wl, l.Quantity) : null,
                    po?.OrderId, po?.Number);
            }).ToList(),
            invitations.OrderBy(v => vendors[v.VendorId].Name).Select(v => new RfqInvitationDto(v.VendorId, vendors[v.VendorId].Code, vendors[v.VendorId].Name,
                v.Status.ToContract(), v.DeclineReason, contacts.GetValueOrDefault(v.VendorId), edi.Contains(v.VendorId))).ToList(),
            quotes.OrderBy(q => QuoteTotal(rfq, q)).Select(q => QuoteDto(rfq, q, vendors[q.VendorId])).ToList(),
            awards.Select(a =>
            {
                var evidence = JsonSerializer.Deserialize<AwardEvidence>(a.ComparisonJson, Json);
                return new RfqAwardDto(a.Id, a.AwardedBy, a.AwardedAtUtc, a.VendorsInvited, a.QuotesReceived, a.SoleSource, a.SoleSourceJustification,
                    a.PriceReasonableness, a.Rationale, a.AwardedTotal, SplitNumbers(a.PurchaseOrderNumbers).ToList(), evidence?.Lines ?? []);
            }).ToList());
    }

    private VendorQuoteDto QuoteDto(Rfq rfq, VendorQuote q, Vendor v) => new(
        q.Id, v.Id, v.Code, v.Name, q.Reference, q.QuoteDate, q.ValidUntil, q.LeadTimeDays, q.Terms, q.Notes, q.Source.ToContract(), q.SubmittedBy, q.SubmittedAtUtc,
        SourcingSupport.IsExpired(q, Today), QuoteTotal(rfq, q) ?? 0,
        q.Lines.Select(l =>
        {
            var line = rfq.Lines.First(r => r.Id == l.RfqLineId);
            var price = l.NoBid ? 0 : SourcingSupport.EffectivePrice(l, line.Quantity);
            return new QuoteLineDto(l.Id, l.RfqLineId, line.LineNumber, l.NoBid, l.UnitPrice,
                l.PriceBreaks.OrderBy(b => b.MinimumQuantity).Select(b => new PriceBreakDto(b.MinimumQuantity, b.UnitPrice)).ToList(),
                SourcingSupport.LeadTime(q, l), l.Note, price, l.NoBid ? 0 : ProcurementSupport.Extend(line.Quantity, price));
        }).OrderBy(l => l.LineNumber).ToList());
}

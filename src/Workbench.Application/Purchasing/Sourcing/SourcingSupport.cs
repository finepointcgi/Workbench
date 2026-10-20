using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Purchasing;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>How the RFQ comparison weighs price, lead time and on-time delivery (config <c>Purchasing:Sourcing</c>).</summary>
public sealed class SourcingOptions
{
    public const string Section = "Purchasing:Sourcing";

    public int PriceWeight { get; set; } = 60;
    public int LeadTimeWeight { get; set; } = 20;
    public int DeliveryWeight { get; set; } = 20;

    /// <summary>The delivery score of a vendor with no measured receipts (neither rewarded nor punished much).</summary>
    public decimal NoHistoryDeliveryScore { get; set; } = 80m;

    /// <summary>How far back on-time delivery is measured.</summary>
    public int PerformanceMonths { get; set; } = 24;
}

internal static class SourcingSupport
{
    public const string RfqEntity = nameof(Rfq);

    public static RfqState ToContract(this RfqStatus s) => (RfqState)(int)s;
    public static RfqStatus ToDomain(this RfqState s) => (RfqStatus)(int)s;
    public static RfqInvitationState ToContract(this RfqInvitationStatus s) => (RfqInvitationState)(int)s;
    public static QuoteSource ToContract(this VendorQuoteSource s) => (QuoteSource)(int)s;

    public static readonly RfqStatus[] QuotableStatuses = [RfqStatus.Open, RfqStatus.PartiallyAwarded];

    /// <summary>The unit price at <paramref name="quantity"/>: the deepest price break reached, else the base price.</summary>
    public static decimal EffectivePrice(VendorQuoteLine line, decimal quantity) =>
        line.PriceBreaks.Where(b => b.MinimumQuantity <= quantity).OrderByDescending(b => b.MinimumQuantity).Select(b => b.UnitPrice).FirstOrDefault() is var p && p > 0
            ? p
            : line.UnitPrice;

    public static int LeadTime(VendorQuote quote, VendorQuoteLine line) => line.LeadTimeDays ?? quote.LeadTimeDays;

    public static bool IsExpired(VendorQuote quote, DateOnly today) => quote.ValidUntil is { } until && until < today;

    /// <summary>
    /// Replaces <paramref name="vendorId"/>'s quote on the RFQ (buyer or portal). Every RFQ line must be quoted or marked
    /// no-bid; price breaks need increasing minimum quantities. Refused once one of the vendor's quote lines is awarded.
    /// </summary>
    public static async Task<VendorQuote> SaveQuoteAsync(
        IWorkbenchDbContext db, Rfq rfq, Guid vendorId, SaveQuoteRequest input, VendorQuoteSource source, string submittedBy, DateTime utcNow, CancellationToken ct)
    {
        if (!QuotableStatuses.Contains(rfq.Status))
        {
            throw new InvalidOperationException(rfq.Status == RfqStatus.Draft
                ? $"{rfq.Number} hasn't been sent to vendors yet."
                : $"{rfq.Number} is {Label(rfq.Status)}; it no longer takes quotes.");
        }

        var invitation = await db.Set<RfqVendor>().FirstOrDefaultAsync(v => v.RfqId == rfq.Id && v.VendorId == vendorId, ct)
            ?? throw new ArgumentException("That vendor isn't invited to this RFQ. Invite it first.");
        if (input.LeadTimeDays is < 0 or > 3650)
        {
            throw new ArgumentException("Lead time must be between 0 and 3650 days.");
        }

        if (input.ValidUntil is { } until && until < input.QuoteDate)
        {
            throw new ArgumentException("The quote can't expire before its date.");
        }

        var inputs = (input.Lines ?? []).ToList();
        if (inputs.GroupBy(l => l.RfqLineId).Any(g => g.Count() > 1))
        {
            throw new ArgumentException("A line is quoted twice.");
        }

        var quote = new VendorQuote
        {
            RfqId = rfq.Id,
            VendorId = vendorId,
            Reference = PurchasingSupport.Optional(input.Reference, "Quote reference", 64),
            QuoteDate = input.QuoteDate,
            ValidUntil = input.ValidUntil,
            LeadTimeDays = input.LeadTimeDays,
            Terms = PurchasingSupport.Optional(input.Terms, "Terms", 300),
            Notes = PurchasingSupport.Optional(input.Notes, "Notes", 2000),
            Source = source,
            SubmittedBy = submittedBy,
            SubmittedAtUtc = utcNow,
        };
        foreach (var rfqLine in rfq.Lines.OrderBy(l => l.LineNumber))
        {
            var prefix = $"Line {rfqLine.LineNumber}";
            var li = inputs.FirstOrDefault(l => l.RfqLineId == rfqLine.Id);
            var line = new VendorQuoteLine { QuoteId = quote.Id, RfqLineId = rfqLine.Id, NoBid = li is null || li.NoBid };
            if (!line.NoBid)
            {
                line.UnitPrice = ProcurementSupport.UnitPrice(li!.UnitPrice, $"{prefix}: unit price");
                line.LeadTimeDays = li.LeadTimeDays is { } days ? days is < 0 or > 3650 ? throw new ArgumentException($"{prefix}: lead time must be between 0 and 3650 days.") : days : null;
                var breaks = (li.PriceBreaks ?? []).Where(b => b.MinimumQuantity > 0 || b.UnitPrice > 0).OrderBy(b => b.MinimumQuantity).ToList();
                if (breaks.Count > 10)
                {
                    throw new ArgumentException($"{prefix}: at most 10 price breaks.");
                }

                if (breaks.Select(b => b.MinimumQuantity).Distinct().Count() != breaks.Count)
                {
                    throw new ArgumentException($"{prefix}: two price breaks start at the same quantity.");
                }

                foreach (var b in breaks)
                {
                    line.PriceBreaks.Add(new VendorQuotePriceBreak
                    {
                        QuoteLineId = line.Id,
                        MinimumQuantity = ProcurementSupport.Quantity(b.MinimumQuantity, $"{prefix}: price break quantity"),
                        UnitPrice = ProcurementSupport.UnitPrice(b.UnitPrice, $"{prefix}: price break price"),
                    });
                }
            }

            line.Note = PurchasingSupport.Optional(li?.Note, $"{prefix}: note", 500);
            quote.Lines.Add(line);
        }

        if (inputs.Any(l => rfq.Lines.All(r => r.Id != l.RfqLineId)))
        {
            throw new ArgumentException("A quoted line isn't on this RFQ.");
        }

        if (quote.Lines.All(l => l.NoBid))
        {
            throw new ArgumentException("Quote at least one line (or decline the RFQ).");
        }

        var existing = await db.Set<VendorQuote>().Include(q => q.Lines).ThenInclude(l => l.PriceBreaks)
            .FirstOrDefaultAsync(q => q.RfqId == rfq.Id && q.VendorId == vendorId, ct);
        if (existing is not null)
        {
            var lineIds = existing.Lines.Select(l => (Guid?)l.Id).ToList();
            if (rfq.Lines.Any(l => lineIds.Contains(l.AwardedQuoteLineId)))
            {
                throw new InvalidOperationException("Lines of this quote are already awarded; it can't be changed.");
            }

        }

        if (existing is not null)
        {
            // Only once the new quote is valid: the old one goes first (the vendor has one quote per RFQ).
            db.Set<VendorQuote>().Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        db.Set<VendorQuote>().Add(quote);
        invitation.Status = RfqInvitationStatus.Quoted;
        invitation.DeclineReason = null;
        return quote;
    }

    public static string Label(RfqStatus s) => s switch
    {
        RfqStatus.PartiallyAwarded => "partly awarded",
        _ => s.ToString().ToLowerInvariant(),
    };

    // ---- Vendor performance ----

    public sealed record Performance(int Measured, int OnTime)
    {
        public decimal? Percent => Measured == 0 ? null : decimal.Round(OnTime * 100m / Measured, 1);
    }

    /// <summary>
    /// On-time delivery per vendor: receipt lines since <paramref name="since"/> against PO lines with a need date, on time
    /// when received on or before it.
    /// </summary>
    public static async Task<Dictionary<Guid, Performance>> PerformanceAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> vendorIds, DateOnly since, CancellationToken ct)
    {
        var ids = vendorIds.Distinct().ToList();
        var rows = await (from rl in db.Set<PurchaseReceiptLine>().AsNoTracking()
                          join r in db.Set<PurchaseReceipt>().AsNoTracking() on rl.ReceiptId equals r.Id
                          join pl in db.Set<PurchaseOrderLine>().AsNoTracking() on rl.PurchaseOrderLineId equals pl.Id
                          join o in db.Set<PurchaseOrder>().AsNoTracking() on pl.PurchaseOrderId equals o.Id
                          where ids.Contains(o.VendorId) && pl.NeededBy != null && r.ReceivedOn >= since
                          select new { o.VendorId, r.ReceivedOn, pl.NeededBy }).ToListAsync(ct);
        return ids.ToDictionary(id => id, id =>
        {
            var mine = rows.Where(x => x.VendorId == id).ToList();
            return new Performance(mine.Count, mine.Count(x => x.ReceivedOn <= x.NeededBy!.Value));
        });
    }

    // ---- Comparison ----

    public sealed record Cell(Guid VendorId, VendorQuote? Quote, VendorQuoteLine? Line, decimal? Price, decimal? Extended, int? Lead, bool Expired)
    {
        public bool Responsive => Line is { NoBid: false } && !Expired;
        public decimal? Score { get; set; }
        public bool Lowest { get; set; }
        public bool Recommended { get; set; }
    }

    /// <summary>
    /// Scores each responsive quote line 0–100: price (lowest ÷ this), lead time ((shortest + 7) ÷ (this + 7), so a few days
    /// matter less than weeks) and on-time delivery (the vendor's percentage, or <see cref="SourcingOptions.NoHistoryDeliveryScore"/>),
    /// weighted. The highest score is recommended (ties go to the lower price). Expired and no-bid lines aren't scored.
    /// </summary>
    public static List<Cell> ScoreLine(RfqLine line, IReadOnlyList<Guid> vendorIds, IReadOnlyList<VendorQuote> quotes,
        IReadOnlyDictionary<Guid, Performance> performance, SourcingOptions weights, DateOnly today)
    {
        var cells = vendorIds.Select(v =>
        {
            var quote = quotes.FirstOrDefault(q => q.VendorId == v);
            var ql = quote?.Lines.FirstOrDefault(l => l.RfqLineId == line.Id);
            if (quote is null || ql is null || ql.NoBid)
            {
                return new Cell(v, quote, ql, null, null, null, quote is not null && IsExpired(quote, today));
            }

            var price = EffectivePrice(ql, line.Quantity);
            return new Cell(v, quote, ql, price, ProcurementSupport.Extend(line.Quantity, price), LeadTime(quote, ql), IsExpired(quote, today));
        }).ToList();

        var responsive = cells.Where(c => c.Responsive).ToList();
        if (responsive.Count == 0)
        {
            return cells;
        }

        var minPrice = responsive.Min(c => c.Price!.Value);
        var minLead = responsive.Min(c => c.Lead!.Value);
        var total = Math.Max(1, weights.PriceWeight + weights.LeadTimeWeight + weights.DeliveryWeight);
        foreach (var c in responsive)
        {
            var priceScore = minPrice / c.Price!.Value * 100m;
            var leadScore = (minLead + 7m) / (c.Lead!.Value + 7m) * 100m;
            var delivery = performance.GetValueOrDefault(c.VendorId)?.Percent ?? weights.NoHistoryDeliveryScore;
            c.Score = decimal.Round((priceScore * weights.PriceWeight + leadScore * weights.LeadTimeWeight + delivery * weights.DeliveryWeight) / total, 1);
            c.Lowest = c.Price == minPrice;
        }

        var best = responsive.OrderByDescending(c => c.Score).ThenBy(c => c.Price).First();
        best.Recommended = true;
        return cells;
    }

    public static async Task<Rfq> LoadAsync(IWorkbenchDbContext db, Guid id, CancellationToken ct, bool track = true)
    {
        var q = track ? db.Set<Rfq>() : db.Set<Rfq>().AsNoTracking();
        return await q.Include(r => r.Lines).FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw WorkbenchNotFoundException.For("RFQ", id);
    }

    public static async Task<List<VendorQuote>> QuotesAsync(IWorkbenchDbContext db, Guid rfqId, CancellationToken ct) =>
        await db.Set<VendorQuote>().AsNoTracking().Include(q => q.Lines).ThenInclude(l => l.PriceBreaks).Where(q => q.RfqId == rfqId).ToListAsync(ct);
}

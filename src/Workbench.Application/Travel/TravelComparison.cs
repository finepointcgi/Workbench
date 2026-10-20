using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Travel;
using Workbench.Domain.PerDiem;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

/// <summary>
/// Estimate vs actual: each line of the trip's expense report is put in a cost category and the categories are compared
/// with the authorization. A category over its authorized amount by more than the tolerance (or not authorized at all)
/// flags its lines, as does a trip total over the tolerance. Unallowable and over-per-diem amounts are not decided here:
/// they stay with the per diem lines and the approver's "unallowable" flag.
/// </summary>
public static class TravelComparison
{
    public sealed record CardInfo(Guid TransactionId, string Last4, string Merchant, string? MerchantCategory, DateOnly Date, decimal Amount, bool CompanyPaid);

    /// <summary>The card charges attached to the given expense lines, by line.</summary>
    public static async Task<Dictionary<Guid, CardInfo>> CardLinesAsync(IWorkbenchDbContext db, IReadOnlyCollection<Guid> odcIds, CancellationToken ct)
    {
        if (odcIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Set<CardTransaction>().AsNoTracking()
            .Where(t => t.OdcEntryId != null && odcIds.Contains(t.OdcEntryId.Value))
            .Join(db.Set<CardProgram>(), t => t.ProgramId, p => p.Id, (t, p) => new { t, p.PaymentResponsibility })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.t.OdcEntryId!.Value).ToDictionary(g => g.Key, g =>
        {
            var r = g.First();
            return new CardInfo(r.t.Id, r.t.CardLast4, r.t.Merchant, r.t.MerchantCategory, r.t.TransactionDate, r.t.Amount, r.PaymentResponsibility == CardPaymentResponsibility.CompanyPaid);
        });
    }

    public static async Task<TravelComparisonDto> BuildAsync(IWorkbenchDbContext db, TravelAuthorization ta, Guid reportId, decimal tolerancePercent, CancellationToken ct)
    {
        var lines = await db.Set<OdcEntry>().AsNoTracking().Where(o => o.ExpenseReportId == reportId)
            .OrderBy(o => o.IncurredOn).ThenBy(o => o.CreatedAtUtc).ToListAsync(ct);
        var ids = lines.Select(l => l.Id).ToList();
        var chosen = await db.Set<TravelLineCategory>().AsNoTracking().Where(c => ids.Contains(c.OdcEntryId)).ToDictionaryAsync(c => c.OdcEntryId, c => c.Category, ct);
        var perDiem = await db.Set<PerDiemLine>().AsNoTracking().Where(p => ids.Contains(p.OdcEntryId)).ToDictionaryAsync(p => p.OdcEntryId, p => p.Kind, ct);
        var cards = await CardLinesAsync(db, ids, ct);

        TravelCategory CategoryOf(OdcEntry line) =>
            chosen.TryGetValue(line.Id, out var c) ? (TravelCategory)(int)c
            : perDiem.TryGetValue(line.Id, out var kind) ? TravelCategories.FromPerDiem(kind, line.Description)
            : cards.TryGetValue(line.Id, out var card) ? TravelCategories.Guess(card.Merchant, card.MerchantCategory, line.Description)
            : TravelCategories.Guess(line.Vendor, null, line.Description);

        var categorized = lines.Select(l => (Line: l, Category: CategoryOf(l))).ToList();
        var factor = 1m + tolerancePercent / 100m;
        var authorized = new Dictionary<TravelCategory, decimal>
        {
            [TravelCategory.Airfare] = ta.EstimatedAirfare,
            [TravelCategory.Lodging] = ta.EstimatedLodging,
            [TravelCategory.Mie] = ta.EstimatedMie,
            [TravelCategory.RentalCar] = ta.EstimatedRentalCar,
            [TravelCategory.Other] = ta.EstimatedOther,
        };
        var categories = Enum.GetValues<TravelCategory>().Select(c =>
        {
            var actual = categorized.Where(x => x.Category == c).Sum(x => x.Line.Amount);
            var limit = authorized[c];
            return new TravelCategoryComparisonDto(c, limit, actual, actual > limit, actual > Math.Round(limit * factor, 2));
        }).ToList();
        var byCategory = categories.ToDictionary(c => c.Category);
        var flags = new List<string>();
        foreach (var c in categories.Where(c => c.IsOverTolerance))
        {
            flags.Add(c.Authorized == 0
                ? $"{TravelLabels.Category(c.Category)}: {c.Actual:C} wasn't in the authorization."
                : $"{TravelLabels.Category(c.Category)}: {c.Actual:C} is {c.Difference:C} over the authorized {c.Authorized:C} (more than {tolerancePercent:0.##}% over).");
        }

        var authorizedTotal = ta.EstimatedTotal;
        var actualTotal = lines.Sum(l => l.Amount);
        var totalOver = actualTotal > Math.Round(authorizedTotal * factor, 2);
        if (totalOver)
        {
            flags.Add($"The trip total {actualTotal:C} is {actualTotal - authorizedTotal:C} over the authorized {authorizedTotal:C}.");
        }

        var lineDtos = categorized.Select(x =>
        {
            var card = cards.GetValueOrDefault(x.Line.Id);
            var category = byCategory[x.Category];
            var flag = category.IsOverTolerance
                ? category.Authorized == 0 ? $"{TravelLabels.Category(x.Category)} wasn't authorized" : $"{TravelLabels.Category(x.Category)} is over the authorized amount"
                : null;
            return new TravelComparisonLineDto(x.Line.Id, x.Line.IncurredOn, x.Line.Description, x.Line.Amount, x.Category, chosen.ContainsKey(x.Line.Id),
                x.Line.IsUnallowable, card?.Last4, card?.CompanyPaid == true, flag is not null, flag);
        }).ToList();
        var companyCard = lineDtos.Where(l => l.IsCompanyPaid).Sum(l => l.Amount);
        return new TravelComparisonDto(ta.Id, ta.Number, reportId, (TravelAuthorizationState)(int)ta.Status, tolerancePercent, categories,
            authorizedTotal, actualTotal, totalOver, lineDtos, companyCard, actualTotal - companyCard, ta.AdvanceIssuedAmount ?? 0, flags);
    }
}

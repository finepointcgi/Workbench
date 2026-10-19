using Workbench.Contracts.Crm;

namespace Workbench.Application.Crm;

/// <summary>The pipeline's arithmetic, kept pure so it can be tested on its own.</summary>
public static class PipelineMath
{
    /// <summary>Value × Pwin × Pgo (percentages 0–100), rounded to cents.</summary>
    public static decimal Weighted(decimal value, int pwin, int pgo) =>
        Math.Round(value * Clamp(pwin) / 100m * Clamp(pgo) / 100m, 2, MidpointRounding.AwayFromZero);

    public static int Clamp(int percent) => Math.Clamp(percent, 0, 100);

    /// <summary>
    /// The weighted gate score as a percentage: Σ(score × weight) ÷ Σ(5 × weight) × 100, scores 0–5, weights 1–10.
    /// </summary>
    public static decimal GateScore(IEnumerable<(int Score, int Weight)> scores)
    {
        var list = scores.ToList();
        var max = list.Sum(s => 5m * s.Weight);
        return max == 0 ? 0m : Math.Round(100m * list.Sum(s => (decimal)s.Score * s.Weight) / max, 2, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The fiscal year and quarter of <paramref name="date"/>. A year is named for the calendar year it ends in (FY2027 =
    /// Oct 2026–Sep 2027 when it starts in October). <paramref name="yearStart"/>, when known, is the start of the fiscal year
    /// containing the date (from the fiscal calendar); otherwise years start on the first of <paramref name="startMonth"/>.
    /// </summary>
    public static (int Year, int Quarter) FiscalQuarter(DateOnly date, int startMonth = 10, (DateOnly Start, int Year)? yearStart = null)
    {
        if (yearStart is { } known)
        {
            var monthsIn = (date.Year - known.Start.Year) * 12 + date.Month - known.Start.Month;
            return (known.Year, Math.Clamp(monthsIn / 3 + 1, 1, 4));
        }

        var offset = (date.Month - startMonth + 12) % 12;
        var year = startMonth == 1 || date.Month < startMonth ? date.Year : date.Year + 1;
        return (year, offset / 3 + 1);
    }

    public sealed record ForecastInput(OpportunityStage Stage, OpportunityKind Kind, decimal Value, int Pwin, int Pgo, DateOnly? AwardDate, decimal? AwardedValue = null, DateOnly? DecidedOn = null);

    /// <summary>
    /// Open pipeline by fiscal quarter of the expected award date (weighted by Pwin × Pgo, split new vs recompete), plus what
    /// was won in each quarter. Outcomes other than Won are left out; undated open work is counted separately.
    /// </summary>
    public static ForecastDto Forecast(IEnumerable<ForecastInput> opportunities, Func<DateOnly, (int Year, int Quarter)> quarterOf)
    {
        var buckets = new Dictionary<(int, int), (int Count, decimal Total, decimal Weighted, decimal New, decimal Recompete, decimal Won)>();
        var undatedCount = 0;
        var undatedWeighted = 0m;
        foreach (var o in opportunities)
        {
            if (o.Stage == OpportunityStage.Won)
            {
                if ((o.DecidedOn ?? o.AwardDate) is { } decided)
                {
                    var key = quarterOf(decided);
                    var b = buckets.GetValueOrDefault(key);
                    buckets[key] = b with { Won = b.Won + (o.AwardedValue ?? o.Value) };
                }

                continue;
            }

            if (!o.Stage.IsOpen())
            {
                continue;
            }

            var weighted = Weighted(o.Value, o.Pwin, o.Pgo);
            if (o.AwardDate is not { } date)
            {
                undatedCount++;
                undatedWeighted += weighted;
                continue;
            }

            var q = quarterOf(date);
            var bucket = buckets.GetValueOrDefault(q);
            buckets[q] = (bucket.Count + 1, bucket.Total + o.Value, bucket.Weighted + weighted,
                bucket.New + (o.Kind == OpportunityKind.New ? weighted : 0m), bucket.Recompete + (o.Kind == OpportunityKind.Recompete ? weighted : 0m), bucket.Won);
        }

        var quarters = buckets.OrderBy(b => b.Key.Item1).ThenBy(b => b.Key.Item2)
            .Select(b => new ForecastBucketDto(b.Key.Item1, b.Key.Item2, b.Value.Count, b.Value.Total, b.Value.Weighted, b.Value.New, b.Value.Recompete, b.Value.Won))
            .ToList();
        return new ForecastDto(quarters, quarters.Sum(q => q.TotalValue), quarters.Sum(q => q.WeightedValue) + undatedWeighted,
            quarters.Sum(q => q.NewWeighted), quarters.Sum(q => q.RecompeteWeighted), undatedCount, undatedWeighted);
    }

    /// <summary>A solicitation number reduced to upper-case letters and digits ("W91QUZ-26-R-0042" → "W91QUZ26R0042"), or null.</summary>
    public static string? SolicitationKey(string? solicitationNumber)
    {
        if (string.IsNullOrWhiteSpace(solicitationNumber))
        {
            return null;
        }

        var key = new string(solicitationNumber.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
        return key.Length == 0 ? null : key;
    }
}

namespace Workbench.Contracts.Contracts;

/// <summary>
/// A contract's funding in one glance for a project manager, from the same per-CLIN status (and Limitation of Funds
/// thresholds, via each CLIN's <see cref="FundingLevel"/>) that Finance sees. Only active, funded CLINs count.
/// </summary>
public sealed record FundingRunway(
    string ContractNumber,
    decimal Funded,
    decimal Spent,
    decimal Remaining,
    DateOnly? RunsOutOn,
    int? WeeksLeft,
    FundingLevel Level,
    IReadOnlyList<string> LowClins)
{
    public bool IsLow => Level is FundingLevel.Warning or FundingLevel.Critical or FundingLevel.Exhausted;

    public string? WeeksText => WeeksLeft switch { null => null, 0 => "less than a week", 1 => "1 week", var w => $"{w} weeks" };

    /// <summary>Null when no CLIN here is funded and active.</summary>
    public static FundingRunway? From(ContractFundingStatusDto status)
    {
        var clins = status.Clins.Where(c => c.Level is not (FundingLevel.NotActive or FundingLevel.Unfunded)).ToList();
        if (clins.Count == 0)
        {
            return null;
        }

        var low = clins.Where(c => c.Level is FundingLevel.Warning or FundingLevel.Critical or FundingLevel.Exhausted).ToList();
        // The soonest run-out among the CLINs that are running low (any CLIN when none are).
        var runOut = (low.Count > 0 ? low : clins).Where(c => c.ProjectedExhaustion is not null).Select(c => c.ProjectedExhaustion).Min();
        var weeks = runOut is { } d ? Math.Max(0, (int)Math.Round((d.DayNumber - status.AsOf.DayNumber) / 7.0)) : (int?)null;
        return new FundingRunway(
            status.ContractNumber, clins.Sum(c => c.FundedValue), clins.Sum(c => c.Incurred), (low.Count > 0 ? low : clins).Sum(c => c.Remaining),
            runOut, weeks, clins.Max(c => c.Level), low.Select(c => c.Number).ToList());
    }
}

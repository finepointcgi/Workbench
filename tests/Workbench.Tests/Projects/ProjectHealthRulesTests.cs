using Workbench.Contracts.Projects;

namespace Workbench.Tests.Projects;

/// <summary>The health chip: Over, At risk or On track from spend, forecast, funding and time elapsed.</summary>
public sealed class ProjectHealthRulesTests
{
    [Theory]
    [InlineData(100, 40, 100, null, 0.5, ProjectHealth.OnTrack)]
    [InlineData(100, 40, 104, null, 0.5, ProjectHealth.AtRisk)] // forecast up to 5% over
    [InlineData(100, 40, 105, null, 0.5, ProjectHealth.AtRisk)] // exactly 5% is still at risk
    [InlineData(100, 40, 106, null, 0.5, ProjectHealth.Over)] // more than 5% over
    [InlineData(100, 101, 101, null, 1.0, ProjectHealth.Over)] // spent past the budget
    [InlineData(100, 61, 100, null, 0.5, ProjectHealth.AtRisk)] // 11 points ahead of the time elapsed
    [InlineData(100, 60, 100, null, 0.5, ProjectHealth.OnTrack)] // exactly 10 points ahead is fine
    [InlineData(100, 40, 100, 0.90, 0.5, ProjectHealth.AtRisk)] // 90% of funding used
    [InlineData(100, 40, 100, 0.89, 0.5, ProjectHealth.OnTrack)]
    [InlineData(100, 40, 100, 1.0, 0.5, ProjectHealth.Over)] // funding used up
    [InlineData(100, 90, 100, null, null, ProjectHealth.OnTrack)] // no dates: no burn-rate check
    [InlineData(0, 40, 40, null, 0.5, ProjectHealth.NoBudget)]
    [InlineData(0, 40, 40, 1.2, 0.5, ProjectHealth.Over)] // no budget but out of funding
    public void Health_follows_the_thresholds(double budget, double spent, double eac, double? fundingUsed, double? elapsed, ProjectHealth expected)
    {
        var (health, reasons) = ProjectHealthRules.Evaluate((decimal)budget, (decimal)spent, (decimal)eac, (decimal?)fundingUsed, (decimal?)elapsed);

        Assert.Equal(expected, health);
        Assert.Equal(expected == ProjectHealth.OnTrack, reasons.Count == 0);
    }

    [Fact]
    public void Every_reason_is_listed()
    {
        var (health, reasons) = ProjectHealthRules.Evaluate(100m, 120m, 130m, 1.1m, 0.5m);

        Assert.Equal(ProjectHealth.Over, health);
        Assert.Equal(3, reasons.Count); // funding, spent, forecast (the burn-rate note is only for At risk)
    }
}

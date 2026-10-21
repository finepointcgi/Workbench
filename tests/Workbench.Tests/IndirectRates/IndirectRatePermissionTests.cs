using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Tests.Identity;

namespace Workbench.Tests.IndirectRates;

/// <summary>The real registrations: indirect rates are Finance's; burdened cost follows the project financials rule.</summary>
public sealed class IndirectRatePermissionTests
{
    [Theory]
    [InlineData(SeededHost.Viewer)]
    [InlineData(SeededHost.Owner)]
    [InlineData(SeededHost.Timekeeper)]
    public async Task Indirect_rates_are_for_finance_and_admins_only(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(email).Host;
        var rates = host.Get<IIndirectRateService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rates.ListPoolSetupsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rates.ListRatesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rates.SaveRateAsync(new SaveIndirectRateRequest(Contracts.Accounting.CostPool.Fringe, 2027, RateBasis.Provisional, 0.3m)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rates.PostAllocationAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => rates.GetTrueUpAsync(2027));
        // A project nobody can see (here: none at all) is not found before the financials rule is asked.
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IBurdenedCostService>().GetProjectCostAsync(Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), RateBasis.Provisional));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IBurdenedFinancialsService>().GetProjectAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(SeededHost.Finance)]
    [InlineData(SeededHost.Admin)]
    public async Task Finance_and_admins_can_use_indirect_rates(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var host = seeded.As(email).Host;
        var rates = host.Get<IIndirectRateService>();

        Assert.Empty(await rates.ListPoolSetupsAsync());
        var rate = await rates.SaveRateAsync(new SaveIndirectRateRequest(Contracts.Accounting.CostPool.Fringe, 2027, RateBasis.Provisional, 0.3m));
        Assert.Equal(email, rate.CreatedBy);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Get<IBurdenedCostService>().GetProjectCostAsync(Guid.NewGuid(), new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), RateBasis.Provisional));
    }
}

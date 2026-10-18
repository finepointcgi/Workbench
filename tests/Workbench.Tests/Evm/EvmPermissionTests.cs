using Workbench.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Evm;

/// <summary>The real registrations: earned value is Finance, Admin or the project's PM; approvals and reopening are Finance's.</summary>
public sealed class EvmPermissionTests
{
    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    [InlineData(Owner)] // pm@ manages 1001
    public async Task Finance_admins_and_the_pm_read_earned_value(string email)
    {
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        var host = seeded.As(email).Host;

        Assert.Null(await host.Get<IEvmService>().GetBaselineAsync(fixture.Project1));
        Assert.Empty(await host.Get<IEvmChangeRequestService>().ListAsync(fixture.Project1));
    }

    [Theory]
    [InlineData(Editor)]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    public async Task Nobody_else_sees_or_statuses_earned_value(string email)
    {
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        var host = seeded.As(email).Host;
        var evm = host.Get<IEvmService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => evm.GetBaselineAsync(fixture.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => evm.GetStatusAsync(fixture.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => evm.CreateBaselineAsync(fixture.Project1, new CreateEvmBaselineRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => evm.ClosePeriodAsync(fixture.Project1, Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => evm.SaveVarianceAsync(fixture.Project1, Guid.NewGuid(), Guid.NewGuid(), new SaveVarianceRequest("a", "b", "c", null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IEvmChangeRequestService>().CreateAsync(fixture.Project1, new CreateChangeRequestRequest("t", "j", [])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IEvmExportService>().ExportFormat1Async(fixture.Project1));
    }

    [Fact]
    public async Task A_pm_doesnt_see_another_projects_earned_value()
    {
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => seeded.As(Owner).Host.Get<IEvmService>().GetSeriesAsync(fixture.Project2));
    }

    [Fact]
    public async Task Only_finance_approves_baseline_changes_and_reopens_periods()
    {
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);

        var pm = seeded.As(Owner).Host;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.Get<IEvmChangeRequestService>().ApproveAsync(Guid.NewGuid(), new DecideChangeRequestRequest(null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.Get<IEvmChangeRequestService>().RejectAsync(Guid.NewGuid(), new DecideChangeRequestRequest(null)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pm.Get<IEvmService>().ReopenPeriodAsync(fixture.Project1, Guid.NewGuid(), new ReopenEvmPeriodRequest("x")));

        var finance = seeded.As(Finance).Host;
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => finance.Get<IEvmChangeRequestService>().ApproveAsync(Guid.NewGuid(), new DecideChangeRequestRequest(null)));
    }
}

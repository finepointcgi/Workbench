using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Revenue;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Revenue;

/// <summary>The real registrations: revenue is Finance/Admin; pm@ (manager of 1001 on C1, not Finance) reads and estimates 1001 only.</summary>
public sealed class RevenuePermissionTests
{
    private static async Task<(SeededHost Seeded, ProjectAccountingFixture Fx, Guid Period)> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var fx = await ProjectAccountingFixture.SeedAsync(seeded);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var period = await db.Set<FiscalPeriod>().FirstAsync(p => p.Name == "2026-10");
        return (seeded, fx, period.Id);
    }

    [Theory]
    [InlineData(Viewer)]
    [InlineData(Timekeeper)]
    [InlineData(Outsider)]
    public async Task Revenue_is_closed_to_everyone_outside_finance_who_manages_no_project(string email)
    {
        var (seeded, fx, period) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(email).Host;

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueSetupService>().ListProjectsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueSetupService>().GetProjectAsync(fx.Project1));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueSetupService>().SubmitEstimateAsync(new SubmitEstimateRequest(fx.Project1, ProjectAccountingFixture.Day, 100, 120)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueRunService>().PreviewAsync(period));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueReportService>().GetRevenueVsBillingsAsync(period));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueReportService>().GetBacklogAsync(period));
    }

    [Fact]
    public async Task A_project_manager_reads_and_estimates_their_project_but_runs_and_approves_nothing()
    {
        var (seeded, fx, period) = await CreateAsync();
        await using var _ = seeded;
        var host = seeded.As(Owner).Host;
        var setup = host.Get<IRevenueSetupService>();

        var project = await setup.GetProjectAsync(fx.Project1);
        Assert.False(project.CanManage);
        var estimate = await setup.SubmitEstimateAsync(new SubmitEstimateRequest(fx.Project1, ProjectAccountingFixture.Day, 100_000m, 110_000m));
        Assert.Equal(EstimateState.Submitted, estimate.Status);
        await setup.RecordDeliveryAsync(new RecordDeliveryRequest(fx.Project1, ProjectAccountingFixture.Day, 1, "Unit 1"));

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => setup.GetProjectAsync(fx.Project2));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => setup.SubmitEstimateAsync(new SubmitEstimateRequest(fx.Project2, ProjectAccountingFixture.Day, 1, 1)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => setup.ApproveEstimateAsync(estimate.Id, new DecideEstimateRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => setup.SaveSetupAsync(new SaveRevenueSetupRequest(fx.Project1, RevenueMethodKind.CostPlusFee)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => setup.ListEstimatesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IRevenueRunService>().PostAsync(period, new PostRevenueRunRequest()));

        // Reports show only their project and its contract.
        var backlog = await host.Get<IRevenueReportService>().GetBacklogAsync(period);
        Assert.Equal(fx.Contract1, Assert.Single(backlog.Rows).ContractId);

        // Finance approves it and sees both contracts.
        var finance = seeded.As(Finance).Host;
        var approved = await finance.Get<IRevenueSetupService>().ApproveEstimateAsync(estimate.Id, new DecideEstimateRequest("Agreed"));
        Assert.Equal(EstimateState.Approved, approved.Status);
        Assert.True((await finance.Get<IRevenueSetupService>().GetProjectAsync(fx.Project1)).CanManage);
        Assert.Equal(2, (await finance.Get<IRevenueReportService>().GetBacklogAsync(period)).Rows.Count);
        Assert.NotNull(await finance.Get<IRevenueRunService>().PreviewAsync(period));
    }
}

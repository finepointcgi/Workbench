using Workbench.Contracts;
using Workbench.Contracts.IncurredCost;
using Workbench.Tests.Identity;

namespace Workbench.Tests.IncurredCost;

/// <summary>The real registrations: incurred cost submissions are Finance's and administrators'.</summary>
public sealed class IncurredCostPermissionTests
{
    [Theory]
    [InlineData(SeededHost.Viewer)]
    [InlineData(SeededHost.Owner)]
    [InlineData(SeededHost.Timekeeper)]
    public async Task Others_are_refused(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var service = seeded.As(email).Host.Get<IIncurredCostService>();

        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ListAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.GetAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.CreateAsync(new CreateIncurredCostRequest(2026)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.CertifyAsync(Guid.NewGuid(), new CertifyIncurredCostRequest("A", "B", true)));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.SubmitAsync(Guid.NewGuid(), new SubmitIncurredCostRequest()));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.ExportWorkbookAsync(Guid.NewGuid()));
    }

    [Theory]
    [InlineData(SeededHost.Finance)]
    [InlineData(SeededHost.Admin)]
    public async Task Finance_and_admins_can_work_with_packages(string email)
    {
        await using var seeded = await SeededHost.CreateAsync();
        var service = seeded.As(email).Host.Get<IIncurredCostService>();

        Assert.Empty(await service.ListAsync());
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => service.GetAsync(Guid.NewGuid()));
    }
}

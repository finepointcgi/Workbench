using Workbench.Contracts;
using Workbench.Contracts.Risks;
using Workbench.Tests.Identity;
using static Workbench.Tests.Identity.SeededHost;

namespace Workbench.Tests.Risks;

/// <summary>The real registrations: the register is for whoever sees the project's financials: Finance, Admin and the project's PM.</summary>
public sealed class RiskPermissionTests
{
    private static readonly SaveRiskRequest Sample = new("Vendor slips", RiskCategoryDto.Schedule, 40, 10_000m, 5, RiskStatusDto.Open, RiskResponseDto.Mitigate);

    [Theory]
    [InlineData(Finance)]
    [InlineData(Admin)]
    [InlineData(Owner)] // pm@ manages 1001
    public async Task Finance_admins_and_the_pm_keep_the_register(string email)
    {
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        var risks = seeded.As(email).Host.Get<IRiskService>();

        var created = await risks.CreateAsync(fixture.Project1, Sample);
        Assert.Single((await risks.GetRegisterAsync(fixture.Project1)).Risks);
        await risks.UpdateAsync(created.Id, Sample with { Title = "Vendor slips again" });
        await risks.DeleteAsync(created.Id);
    }

    [Theory]
    [InlineData(Editor, true)]
    [InlineData(Viewer, true)]
    [InlineData(Timekeeper, false)]
    public async Task Nobody_else_sees_or_changes_it(string email, bool onProject1)
    {
        // Not on 1001 at all: not found; on it without the rule: forbidden.
        Task Refused(Func<Task> call) => Assert.ThrowsAsync(onProject1 ? typeof(WorkbenchForbiddenException) : typeof(WorkbenchNotFoundException), call);
        await using var seeded = await CreateAsync();
        var fixture = await ProjectAccountingFixture.SeedAsync(seeded);
        var existing = await seeded.As(Finance).Host.Get<IRiskService>().CreateAsync(fixture.Project1, Sample);
        var risks = seeded.As(email).Host.Get<IRiskService>();

        await Refused(() => risks.GetRegisterAsync(fixture.Project1));
        await Refused(() => risks.CreateAsync(fixture.Project1, Sample));
        await Refused(() => risks.GetAsync(existing.Id));
        await Refused(() => risks.UpdateAsync(existing.Id, Sample));
        await Refused(() => risks.DeleteAsync(existing.Id));
    }
}

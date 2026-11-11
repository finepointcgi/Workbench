using Workbench.Application.ProjectAccounting;
using Workbench.Application.Risks;
using Workbench.Contracts;
using Workbench.Contracts.Evm;
using Workbench.Contracts.Risks;
using Workbench.Tests.Evm;

namespace Workbench.Tests.Risks;

public sealed class RiskRegisterTests
{
    private static SaveRiskRequest Risk(string title, int percent, decimal cost, int days = 0, RiskStatusDto status = RiskStatusDto.Open, DateOnly? review = null) =>
        new(title, RiskCategoryDto.Technical, percent, cost, days, status, RiskResponseDto.Mitigate, ReviewOn: review);

    private static (EvmKit Kit, RiskService Service) Create(EvmKit kit) => (kit, new RiskService(kit.Factory, new AuditRecorder(new EvmKit.User(), kit.Clock), kit.Clock));

    [Fact]
    public async Task Risks_are_numbered_per_project_and_exposure_is_probability_times_impact()
    {
        var (kit, risks) = Create(await EvmKit.CreateAsync());
        var a = await risks.CreateAsync(kit.Project.Id, Risk("Vendor slips", 50, 40_000m, 20));
        var b = await risks.CreateAsync(kit.Project.Id, Risk("Key engineer leaves", 10, 100_000m, 30));

        Assert.Equal(("R-1", "R-2"), (a.Label, b.Label));
        Assert.Equal((20_000m, 10m), (a.ExpectedCost, a.ExpectedDays));
        Assert.Equal((10_000m, 3m), (b.ExpectedCost, b.ExpectedDays));
    }

    [Fact]
    public async Task The_summary_counts_live_risks_only_and_sets_them_against_the_management_reserve()
    {
        var (kit, risks) = Create(await EvmKit.CreateAsync());
        var none = await risks.GetRegisterAsync(kit.Project.Id);
        Assert.Null(none.Summary.ManagementReserve); // no earned-value baseline yet

        await kit.Evm.CreateBaselineAsync(kit.Project.Id, new CreateEvmBaselineRequest(ControlAccountLevel: 2, ManagementReserve: 3_000));
        await risks.CreateAsync(kit.Project.Id, Risk("Likely and costly", 60, 4_000m, 10, review: new DateOnly(2026, 11, 1)));
        await risks.CreateAsync(kit.Project.Id, Risk("Unlikely", 5, 20_000m, 0, RiskStatusDto.Accepted));
        await risks.CreateAsync(kit.Project.Id, Risk("Already happened", 100, 9_000m, 5, RiskStatusDto.Occurred));
        var closed = await risks.CreateAsync(kit.Project.Id, Risk("Gone", 90, 50_000m, 0, RiskStatusDto.Closed));

        var s = (await risks.GetRegisterAsync(kit.Project.Id)).Summary;
        Assert.Equal((2, 1, 1), (s.LiveCount, s.HighCount, s.ReviewsOverdue)); // the review date is before today (Nov 16)
        Assert.Equal(3_400m, s.ExpectedCost); // 60% of 4,000 + 5% of 20,000
        Assert.Equal(24_000m, s.WorstCaseCost);
        Assert.Equal((3_000m, -400m), (s.ManagementReserve, s.ReserveLeft));

        // Closed risks are hidden unless asked for.
        Assert.DoesNotContain((await risks.GetRegisterAsync(kit.Project.Id)).Risks, r => r.Id == closed.Id);
        Assert.Contains((await risks.GetRegisterAsync(kit.Project.Id, includeClosed: true)).Risks, r => r.Id == closed.Id);
    }

    [Theory]
    [InlineData("", 10, 0, 0)]
    [InlineData("Ok", 101, 0, 0)]
    [InlineData("Ok", -1, 0, 0)]
    [InlineData("Ok", 10, -1, 0)]
    [InlineData("Ok", 10, 0, -1)]
    public async Task Bad_input_is_refused(string title, int percent, int cost, int days)
    {
        var (kit, risks) = Create(await EvmKit.CreateAsync());
        await Assert.ThrowsAsync<ArgumentException>(() => risks.CreateAsync(kit.Project.Id, Risk(title, percent, cost, days)));
    }

    [Fact]
    public async Task A_risk_can_be_changed_and_deleted_and_an_unknown_one_is_not_found()
    {
        var (kit, risks) = Create(await EvmKit.CreateAsync());
        var created = await risks.CreateAsync(kit.Project.Id, Risk("Vendor slips", 50, 40_000m));
        var changed = await risks.UpdateAsync(created.Id, Risk("Vendor slips", 20, 40_000m, status: RiskStatusDto.Mitigating) with { OwnerEmail = "PM@Example.com", PartRefs = "P-1042" });
        Assert.Equal((8_000m, RiskStatusDto.Mitigating, "pm@example.com", "P-1042"), (changed.ExpectedCost, changed.Status, changed.OwnerEmail, changed.PartRefs));

        await risks.DeleteAsync(created.Id);
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => risks.GetAsync(created.Id));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => risks.GetRegisterAsync(Guid.NewGuid()));
    }
}

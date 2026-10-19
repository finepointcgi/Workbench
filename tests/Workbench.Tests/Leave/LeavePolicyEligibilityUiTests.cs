using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Leave;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Mobile;
using Workbench.Ui.Leave;

namespace Workbench.Tests.Leave;

public sealed class LeavePolicyEligibilityUiTests
{
    private static readonly Guid Ops = Guid.NewGuid();

    [Fact]
    public async Task The_policy_list_says_who_each_policy_applies_to_and_editing_keeps_it()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var limited = Policy("PTO", new LeaveEligibilityDto([Ops], [], false, []));
        var everyone = Policy("SICK", LeaveEligibilityDto.Everyone);
        var (policies, fake) = Fake<ILeavePolicyService>.Create();
        fake.Returns(nameof(ILeavePolicyService.ListLeaveChargeCodesAsync), new List<LeaveChargeCodeDto> { new(limited.WbsElementId, "LV.PTO", "PTO") })
            .Returns(nameof(ILeavePolicyService.ListAsync), new List<LeavePolicyDto> { limited, everyone })
            .On(nameof(ILeavePolicyService.UpdateAsync), _ => limited);
        var (people, peopleFake) = Fake<IPeopleService>.Create();
        peopleFake.Returns(nameof(IPeopleService.ListOrgUnitsAsync), new List<OrgUnitDto> { new(Ops, "OPS", "Operations", null) });
        ctx.Services.AddSingleton(policies);
        ctx.Services.AddSingleton(people);

        var page = ctx.Render<LeavePoliciesAdmin>();

        page.WaitForAssertion(() => Assert.Equal("OPS · non-exempt", page.Find("[data-testid='applies-PTO']").TextContent.Trim()));
        Assert.Equal("Everyone", page.Find("[data-testid='applies-SICK']").TextContent.Trim());
        Assert.Empty(page.FindAll("[data-testid='eligibility']"));

        page.FindAll("button").First(b => b.TextContent.Trim() == "Edit").Click();
        page.WaitForAssertion(() => Assert.Single(page.FindAll("[data-testid='eligibility']")));
        page.FindAll("button").Single(b => b.TextContent.Trim() == "Save policy").Click();

        var saved = (SaveLeavePolicyRequest)fake.Single(nameof(ILeavePolicyService.UpdateAsync))[1]!;
        Assert.Equal([Ops], saved.Eligibility!.OrgUnitIds);
        Assert.False(saved.Eligibility.Exempt);
    }

    private static LeavePolicyDto Policy(string code, LeaveEligibilityDto eligibility) =>
        new(Guid.NewGuid(), code, code, LeaveTypeKind.Pto, Guid.NewGuid(), "LV.PTO", AccrualMethodKind.PerPayPeriod, [new(0, 4m)], null, null, 0, false, true, eligibility);
}

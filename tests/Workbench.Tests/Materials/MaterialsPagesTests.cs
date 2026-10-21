using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Materials;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Identity;
using Workbench.Ui.Materials;

namespace Workbench.Tests.Materials;

public sealed class MaterialsPagesTests
{
    private static PartSummaryDto Part(string number, decimal onHand, decimal gfm = 0) => new(Guid.NewGuid(), number, "", $"{number} part", "EA", PartSourceKind.Buy, PartTracking.None,
        PartCostMethod.Average, 0, 2.5m, onHand, gfm, onHand * 2.5m, "Acme", 10, false, true);

    private static MaterialsOptionsDto Options() => new([], [], [], [], [], [], [], [], []);

    [Fact]
    public async Task The_parts_page_lists_parts_with_value_and_government_quantities_and_only_materials_can_add()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        IReadOnlyList<PartSummaryDto> parts = [Part("BOLT", 10), Part("SENSOR", 0, gfm: 3)];
        ctx.Services.AddSingleton(Fake<IPartService>.Create(("ListAsync", _ => parts), ("GetOptionsAsync", _ => Options())));

        var finance = ctx.Render<PartsPage>(p => p.AddCascadingValue(new CurrentUserInfo("finance@example.com", "Fin", OrgRoles.Finance, null)));
        finance.WaitForAssertion(() => Assert.Equal(2, finance.FindAll("[data-testid^=part-]").Count));
        Assert.Contains("25.00", finance.Find("[data-testid=part-BOLT]").Closest("tr")!.TextContent);
        Assert.Contains("3", finance.Find("[data-testid=part-SENSOR]").Closest("tr")!.TextContent);
        Assert.Empty(finance.FindAll("[data-testid=new-part]"));
        A11yAssert.Check(finance);

        var materials = ctx.Render<PartsPage>(p => p.AddCascadingValue(new CurrentUserInfo("m@example.com", "Mia", OrgRoles.Materials, null)));
        materials.WaitForAssertion(() => Assert.NotNull(materials.Find("[data-testid=new-part]")));
    }
}

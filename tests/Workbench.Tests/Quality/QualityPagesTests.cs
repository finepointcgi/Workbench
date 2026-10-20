using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Quality;
using Workbench.Tests.Accessibility;
using Workbench.Tests.Identity;
using Workbench.Ui.Quality;

namespace Workbench.Tests.Quality;

public sealed class QualityPagesTests
{
    [Fact]
    public async Task The_inspection_screen_asks_for_each_result_and_the_missing_certificate_and_finance_only_reads()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        var id = Guid.NewGuid();
        var bore = new CharacteristicDto(Guid.NewGuid(), 10, "Bore diameter", CharacteristicType.Measured, 22m, 21.95m, 22.05m, "mm", true);
        var finish = new CharacteristicDto(Guid.NewGuid(), 20, "Anodize finish", CharacteristicType.PassFail, null, null, null, null, false);
        var summary = new InspectionSummaryDto(id, "INS-000001", InspectionType.Receiving, InspectionState.Pending, Guid.NewGuid(), "HSG-220", "Machined housing", "EA",
            6, 2, 0, 0, "PO PO-000003", "Digi-Parts", new DateOnly(2026, 10, 14), null, null, null, null);
        var inspection = new InspectionDto(summary, Guid.NewGuid(), "Receiving", "Measure both ends.", 0, 0, null, null, null, null, [bore, finish],
            [new InspectionItemDto(Guid.NewGuid(), "MAIN / A-02", "", "", 6, 0, 0)], [], ["Certificate of conformance"], []);
        ctx.Services.AddSingleton(Fake<IInspectionService>.Create(("GetAsync", _ => inspection), ("ListDefectCodesAsync", _ => (IReadOnlyList<DefectCodeDto>)[])));
        ctx.Services.AddSingleton(Fake<IQualityFileService>.Create(("GetUrl", _ => "api/v1/quality/files/x")));

        var page = ctx.Render<InspectionPage>(p => p.Add(x => x.InspectionId, id)
            .AddCascadingValue(new CurrentUserInfo("m@example.com", "Mia", OrgRoles.Materials, null)));
        page.WaitForAssertion(() => Assert.NotNull(page.Find("[data-testid=complete-inspection]")));
        Assert.Contains("Inspect <strong>2</strong> of 6", page.Markup);
        Assert.Contains("Bore diameter", page.Markup);
        Assert.Contains("21.95 to 22.05 mm", page.Markup);
        Assert.Contains("Certificate of conformance (required)", page.Markup);
        Assert.Contains("Pass and release to stock", page.Find("[data-testid=complete-inspection]").TextContent);
        A11yAssert.Check(page);

        var finance = ctx.Render<InspectionPage>(p => p.Add(x => x.InspectionId, id)
            .AddCascadingValue(new CurrentUserInfo("f@example.com", "Fin", OrgRoles.Finance, null)));
        finance.WaitForAssertion(() => Assert.Contains("<strong>2</strong> of 6", finance.Markup));
        Assert.Empty(finance.FindAll("[data-testid=complete-inspection]"));
    }
}

using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Assets;
using Workbench.Tests.Identity;
using Workbench.Ui.Assets;

namespace Workbench.Tests.Assets;

public sealed class AssetCategoriesPageTests
{
    private static AssetCategoryDto Category(string code, int assets, bool active = true, decimal? threshold = null) => new(
        Guid.NewGuid(), code, $"{code} name", AssetDepreciationMethod.StraightLine, 5, 5, Guid.NewGuid(), "1510", Guid.NewGuid(), "1590", Guid.NewGuid(), "6140",
        active, AssetDepreciationConvention.MidMonth, threshold, assets);

    [Fact]
    public async Task Categories_in_use_can_be_deactivated_but_not_deleted()
    {
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        IReadOnlyList<AssetCategoryDto> categories = [Category("COMP", assets: 3), Category("SPARE", assets: 0, threshold: 1_000m), Category("OLD", assets: 1, active: false)];
        ctx.Services.AddSingleton(Fake<IAssetService>.Create(
            ("GetSettingsAsync", _ => new AssetSettingsDto(5_000m, "4900", "1010")),
            ("ListCategoriesAsync", _ => categories)));

        var page = ctx.Render<AssetCategoriesPage>();

        page.WaitForAssertion(() => Assert.Equal(3, page.FindAll("[data-testid^=category-]").Count));
        Assert.True(page.Find("[aria-label='Delete COMP']").HasAttribute("disabled"));
        Assert.False(page.Find("[aria-label='Delete SPARE']").HasAttribute("disabled"));
        Assert.NotNull(page.Find("[aria-label='Deactivate COMP']"));
        Assert.NotNull(page.Find("[aria-label='Reactivate OLD']"));
        var comp = page.Find("[data-testid=category-COMP]").TextContent;
        Assert.Contains("Half month (mid-month)", comp);
        Assert.Contains("(default)", comp);
        Assert.Contains("1,000.00", page.Find("[data-testid=category-SPARE]").TextContent);
    }
}

using Bunit;
using MudBlazor.Services;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;
using Workbench.Tests.Accessibility;
using Workbench.Ui.Pricing;

namespace Workbench.Tests.Pricing;

public sealed class PricingUiTests
{
    [Fact]
    public async Task The_summary_shows_totals_price_to_win_and_the_clin_by_period_table()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (_, model) = await kit.NewAsync(target: 2300m, options: 1);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m, 0m], manualRate: 100m));
        await kit.SettingsAsync(model, RateBasis.Provisional, fee: 0.08m);
        var result = await kit.Pricing.CalculateAsync(model.Id);
        await using var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();

        var summary = ctx.Render<PriceSummaryTab>(p => p.Add(x => x.Result, result).Add(x => x.ContractType, PricingContractKind.CostPlusFixedFee));

        Assert.Contains("$2,317", summary.Find("[data-testid=tile-price]").TextContent);
        Assert.Contains("2.3166", summary.Find("[data-testid=tile-wrap]").TextContent);
        Assert.Contains("over", summary.Find("[data-testid=tile-ptw]").TextContent);
        var table = summary.Find("[data-testid=clin-period]");
        Assert.Contains("0001 · Labor", table.TextContent);
        Assert.Contains("Option 1", table.TextContent);
        A11yAssert.Check(summary);
    }
}

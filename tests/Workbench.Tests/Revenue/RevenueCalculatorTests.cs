using Workbench.Application.Revenue;

namespace Workbench.Tests.Revenue;

public sealed class RevenueCalculatorTests
{
    [Theory]
    [InlineData(2_500, 10_000, 12_000, 0.25, 3_000)]
    [InlineData(1_400, 15_000, 12_000, 0.093333, 1_120)]
    [InlineData(12_000, 10_000, 12_000, 1, 12_000)] // overrun: capped at 100%
    [InlineData(0, 10_000, 12_000, 0, 0)]
    public void Cost_to_cost_is_percent_complete_times_the_transaction_price(decimal cost, decimal eac, decimal price, decimal pct, decimal revenue)
    {
        var (p, r) = RevenueCalculator.CostToCost(cost, eac, price);
        Assert.Equal(pct, p);
        Assert.Equal(revenue, r);
    }

    [Fact]
    public void Cost_plus_adds_the_fee_on_allowable_cost()
    {
        Assert.Equal(1_765.50m, RevenueCalculator.CostPlusFee(1_650m, 0.07m));
        Assert.Equal(400m, RevenueCalculator.CostPlusFee(400m, 0));
    }

    [Fact]
    public void Time_and_materials_prices_hours_at_bill_rate_and_marks_up_odcs()
    {
        // 16 h × 100 + 4 h × 120 + ODCs 1,500 with a 10% markup.
        Assert.Equal(3_730m, RevenueCalculator.TimeAndMaterials([(16m, 100m), (4m, 120m)], 1_500m, 0.10m));
        Assert.Equal(1_500m, RevenueCalculator.TimeAndMaterials([], 1_500m, 0));
    }

    [Fact]
    public void Units_delivered_are_capped_at_the_transaction_price()
    {
        Assert.Equal(750m, RevenueCalculator.UnitsDelivered(3, 250m, 1_000m));
        Assert.Equal(1_000m, RevenueCalculator.UnitsDelivered(5, 250m, 1_000m));
        Assert.Equal(1_250m, RevenueCalculator.UnitsDelivered(5, 250m, null));
    }

    [Fact]
    public void A_loss_contract_provides_for_the_loss_not_yet_in_the_books()
    {
        Assert.Equal(3_000m, RevenueCalculator.ExpectedLoss(15_000m, 12_000m));
        Assert.Equal(0m, RevenueCalculator.ExpectedLoss(10_000m, 12_000m));

        // Cost 1,400, revenue 1,120: 280 of the loss is already cost over revenue, so the provision is the other 2,720.
        Assert.Equal(2_720m, RevenueCalculator.LossProvision(3_000m, 1_400m, 1_120m));
        // At completion all of the loss is in cost over revenue; nothing left to provide.
        Assert.Equal(0m, RevenueCalculator.LossProvision(3_000m, 15_000m, 12_000m));
        Assert.Equal(0m, RevenueCalculator.LossProvision(0, 1_400m, 1_000m));
    }

    [Fact]
    public void Recognized_over_billed_is_a_contract_asset_and_under_billed_a_liability()
    {
        Assert.Equal((300m, 0m), RevenueCalculator.Position(1_300m, 1_000m));
        Assert.Equal((0m, 200m), RevenueCalculator.Position(800m, 1_000m));
    }
}

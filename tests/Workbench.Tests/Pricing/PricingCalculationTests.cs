using Workbench.Application.Pricing;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Pricing;
using Workbench.Domain.Pricing;

namespace Workbench.Tests.Pricing;

/// <summary>Rate build-up, escalation, the indirect cascade, fee methods and T&amp;M bill rates.</summary>
public sealed class PricingCalculationTests
{
    [Theory]
    [InlineData("2026-11-15", "2026-10-01", 0)]
    [InlineData("2026-11-15", "2027-11-14", 0)]
    [InlineData("2026-11-15", "2027-11-15", 1)]
    [InlineData("2026-11-15", "2028-11-14", 1)]
    [InlineData("2026-11-15", "2028-11-15", 2)]
    public void Escalation_counts_whole_years_from_the_rate_date(string asOf, string start, int years) =>
        Assert.Equal(years, PriceCalculator.EscalationYears(DateOnly.Parse(asOf), DateOnly.Parse(start)));

    [Fact]
    public void Escalation_compounds_and_rounds_to_four_places()
    {
        var asOf = new DateOnly(2026, 11, 15);
        Assert.Equal(100m, PriceCalculator.EscalatedRate(100m, 0.03m, asOf, new DateOnly(2027, 1, 1)));
        Assert.Equal(103m, PriceCalculator.EscalatedRate(100m, 0.03m, asOf, new DateOnly(2027, 12, 1)));
        Assert.Equal(106.09m, PriceCalculator.EscalatedRate(100m, 0.03m, asOf, new DateOnly(2028, 12, 1)));
        Assert.Equal(57.9692m, PriceCalculator.EscalatedRate(53.05m, 0.03m, asOf, new DateOnly(2029, 12, 1))); // 53.05 × 1.092727
    }

    [Fact]
    public void Weighted_guidelines_combine_performance_contract_type_and_efficiency()
    {
        var terms = new FeeTerms(FeeMethod.WeightedGuidelines, 0.20m, Technical: 0.05m, Management: 0.04m, ContractTypeRisk: 0.005m, CostEfficiency: 0.01m);

        // 60% × 5% + 40% × 4% = 4.6% performance risk; + 0.5% + 1% = 6.1%. The flat rate is ignored.
        Assert.Equal(0.061m, terms.Rate);
        Assert.Contains("Weighted guidelines", terms.Explain());
        Assert.Equal(0.20m, (terms with { Method = FeeMethod.FlatPercent }).Rate);
        Assert.Equal(0.05m, FeeTerms.NormalContractTypeRisk(PricingContractType.FirmFixedPrice));
        Assert.Equal(0.005m, FeeTerms.NormalContractTypeRisk(PricingContractType.CostPlusFixedFee));
        Assert.Equal(0.01m, FeeTerms.NormalContractTypeRisk(PricingContractType.CostPlusIncentiveFee));
    }

    [Fact]
    public async Task Cascade_matches_the_burdened_cost_service_on_the_same_inputs()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.Books.WorkedExampleAsync(); // 10 h × $100, $500 ODC, $200 unallowable on 2001.01 in Oct 2026; FY2027 provisional 30/50/10.
        var books = (await kit.Books.Burdened.GetProjectCostAsync(kit.Books.Project.Id, kit.Books.Oct.Start, kit.Books.Oct.End, RateBasis.Provisional)).Lines.Single();

        var (_, model) = await kit.NewAsync();
        model = await kit.SettingsAsync(model, RateBasis.Provisional);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        await kit.Pricing.SaveCostLinesAsync(model.Id,
        [
            new SavePriceCostLineRequest(null, model.Wbs[0].Id, model.Clins[0].Id, model.Periods[0].Id, PriceCostType.Odc, "Software", UnitCost: 500m),
            new SavePriceCostLineRequest(null, model.Wbs[0].Id, model.Clins[0].Id, model.Periods[0].Id, PriceCostType.Odc, "Team dinner", UnitCost: 200m, IsUnallowable: true),
        ]);

        var price = await kit.Pricing.CalculateAsync(model.Id);

        Assert.Equal((300m, 650m, 245m), (books.Fringe, books.Overhead, books.GeneralAndAdministrative));
        Assert.Equal((books.DirectLabor, books.Fringe, books.Overhead, books.GeneralAndAdministrative, books.DirectOdc),
            (price.Totals.DirectLabor, price.Totals.Fringe, price.Totals.Overhead, price.Totals.GeneralAndAdministrative, price.Totals.Odc));
        Assert.Equal(books.TotalCost, price.Totals.TotalCost);
        Assert.Equal(books.UnallowableExcluded, price.UnallowableExcluded);
        Assert.Contains(price.Notes, n => n.Contains("unallowable", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(["Fringe", "Overhead", "G&A"], price.Cascade.Select(s => s.Pool));
        var rates = Assert.Single(price.PeriodRates);
        Assert.Equal((2027, 2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m), (rates.FiscalYear, rates.RatesFiscalYear, rates.Basis, rates.Fringe, rates.Overhead, rates.GeneralAndAdministrative));
    }

    [Fact]
    public async Task Direct_rates_come_from_cost_rates_by_average_median_selection_or_name()
    {
        var kit = await PricingKit.CreateAsync();
        var a = await kit.AddEmployeeAsync("Ann", 50m);
        await kit.AddEmployeeAsync("Bo", 60m);
        var c = await kit.AddEmployeeAsync("Cy", 100m);
        await kit.AddEmployeeAsync("Di", 200m, from: new DateOnly(2027, 1, 1)); // no rate yet on the rate date: not counted
        var (_, model) = await kit.NewAsync();
        model = await kit.LaborAsync(model,
            kit.Line(model, LaborRateSource.CategoryAverage, [100m]),
            kit.Line(model, LaborRateSource.CategoryMedian, [100m]),
            kit.Line(model, LaborRateSource.SelectedPeople, [100m], selected: [a.Id, c.Id]),
            kit.Line(model, LaborRateSource.NamedEmployee, [100m], employeeId: c.Id),
            kit.Line(model, LaborRateSource.Manual, [100m], manualRate: 81.25m));

        var labor = (await kit.Pricing.CalculateAsync(model.Id)).Labor;

        Assert.Equal([70m, 60m, 75m, 100m, 81.25m], labor.Select(l => l.BaseRate));
        Assert.Equal("Average of 3 ENG2 people", labor[0].RateSource);
        Assert.Equal("Median of 3 ENG2 people", labor[1].RateSource);
        Assert.Equal("Cy", labor[3].Staff);
        Assert.Equal("TBD", labor[4].Staff);
        Assert.Equal([7000m, 6000m, 7500m, 10000m, 8125m], labor.Select(l => l.DirectLabor));

        var options = await kit.Pricing.GetOptionsAsync(new DateOnly(2026, 11, 15));
        var category = options.LaborCategories.Single(x => x.Code == "ENG2");
        Assert.Equal((3, 70m, 60m), (category.People, category.AverageRate, category.MedianRate));
    }

    [Fact]
    public async Task Escalation_applies_per_year_with_labor_category_overrides()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.AddEmployeeAsync("Ann", 100m);
        var other = await kit.AddCategoryAsync("PM3");
        await kit.AddEmployeeAsync("Pat", 100m, other);
        var (_, model) = await kit.NewAsync(options: 2);
        model = await kit.SettingsAsync(model, escalation: 0.03m);
        await kit.Pricing.SaveEscalationsAsync(model.Id, [new PriceEscalationDto(other.Id, 0.05m)]);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.CategoryAverage, [10m, 10m, 10m]),
            kit.Line(model, LaborRateSource.CategoryAverage, [10m, 10m, 10m], category: other));

        var labor = (await kit.Pricing.CalculateAsync(model.Id)).Labor;

        // Rate date 2026-11-15; periods start 2026-10-01, 2027-10-01 (0 full years) and 2028-10-01 (1).
        Assert.Equal([100m, 100m, 103m, 100m, 100m, 105m], labor.Select(l => l.DirectRate));
        Assert.Equal(0.05m, labor.Last().Escalation);
    }

    [Fact]
    public async Task Forward_pricing_rates_are_used_and_out_years_fall_back_to_the_latest_year()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        await kit.SetRatesAsync(2027, RateBasis.ForwardPricing, 0.28m, 0.45m, 0.09m);
        var (_, model) = await kit.NewAsync(options: 1);
        model = await kit.SettingsAsync(model, RateBasis.ForwardPricing);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m, 10m], manualRate: 100m));

        var price = await kit.Pricing.CalculateAsync(model.Id);

        Assert.Equal(RateBasis.ForwardPricing, price.RateBasis);
        Assert.All(price.PeriodRates, r => Assert.Equal((RateBasis.ForwardPricing, 0.28m, 0.45m, 0.09m), (r.Basis, r.Fringe, r.Overhead, r.GeneralAndAdministrative)));
        Assert.Equal((2028, 2027), (price.PeriodRates[1].FiscalYear, price.PeriodRates[1].RatesFiscalYear)); // FY2028 is past the calendar
        Assert.Contains(price.Notes, n => n.Contains("FY2028 has no forward pricing rates; FY2027's are used"));

        // A basis with no rates at all falls back to provisional, and says so.
        model = await kit.SettingsAsync(model, RateBasis.Target);
        price = await kit.Pricing.CalculateAsync(model.Id);
        Assert.Equal((RateBasis.Provisional, 0.30m), (price.PeriodRates[0].Basis, price.PeriodRates[0].Fringe));
        Assert.Contains(price.Notes, n => n.Contains("No target rates"));
    }

    [Fact]
    public async Task What_if_overrides_replace_the_book_rates()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (_, model) = await kit.NewAsync();
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        model = await kit.SettingsAsync(model, RateBasis.Provisional, overhead: 0.40m);

        var price = await kit.Pricing.CalculateAsync(model.Id);

        // Fringe 300; overhead 1,300 × 40% = 520; G&A (1,000 + 300 + 520) × 10% = 182.
        Assert.Equal((300m, 520m, 182m, 2002m), (price.Totals.Fringe, price.Totals.Overhead, price.Totals.GeneralAndAdministrative, price.Totals.TotalCost));
        Assert.True(price.PeriodRates[0].Overridden);
    }

    [Fact]
    public async Task Flat_and_weighted_guidelines_fee_on_total_cost()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (_, model) = await kit.NewAsync(target: 2500m);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        model = await kit.SettingsAsync(model, RateBasis.Provisional, fee: 0.08m);

        var flat = await kit.Pricing.CalculateAsync(model.Id);
        Assert.Equal((2145m, 171.60m, 2316.60m), (flat.Totals.TotalCost, flat.Totals.Fee, flat.Totals.Price)); // 1,000 + 300 + 650 + 195
        Assert.Equal(Math.Round(171.60m / 2316.60m, 4), flat.Margin);
        Assert.Equal(2.3166m, flat.WrapRate);
        Assert.Equal(2.145m, flat.CostMultiplier);
        var ptw = flat.PriceToWin!;
        Assert.True(ptw.UnderTarget);
        Assert.Equal(-183.40m, ptw.Difference);
        Assert.Equal(Math.Round((2500m - 2145m) / 2145m, 4), ptw.FeeRateAtTarget);

        await kit.Pricing.SaveSettingsAsync(model.Id, new SavePriceSettingsRequest(model.Settings with
        {
            FeeMethod = FeeMethodKind.WeightedGuidelines, WgTechnicalRisk = 0.05m, WgManagementRisk = 0.05m, WgContractTypeRisk = 0.005m, WgCostEfficiency = 0m,
        }));
        var weighted = await kit.Pricing.CalculateAsync(model.Id);
        Assert.Equal(0.055m, weighted.FeeRate);
        Assert.Equal(117.98m, weighted.Totals.Fee); // 2,145 × 5.5% = 117.975
    }

    [Fact]
    public async Task Time_and_materials_bill_rates_are_fully_burdened_with_fee()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        await kit.AddEmployeeAsync("Ann", 50m);
        await kit.AddEmployeeAsync("Bo", 60m);
        var (_, model) = await kit.NewAsync(PricingContractKind.TimeAndMaterials);
        model = await kit.SettingsAsync(model, RateBasis.Provisional, fee: 0.10m);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.NamedEmployee, [30m], employeeId: (await kit.Pricing.GetOptionsAsync()).Employees.Single(e => e.Name == "Ann").Id),
            kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 70m));
        await kit.Pricing.SaveCostLinesAsync(model.Id,
            [new SavePriceCostLineRequest(null, model.Wbs[0].Id, model.Clins[0].Id, model.Periods[0].Id, PriceCostType.Materials, "Cables", UnitCost: 1000m)]);

        var price = await kit.Pricing.CalculateAsync(model.Id);

        // Hours-weighted direct rate (30 × 50 + 10 × 70) / 40 = 55; multiplier 1 + 0.30 + 0.65 + 0.195 = 2.145; × 1.10.
        var rate = Assert.Single(price.BillRates);
        Assert.Equal((40m, 55m, 129.7725m), (rate.Hours, rate.DirectRate, rate.BillRate));
        Assert.Equal((16.5m, 35.75m, 10.725m), (rate.Fringe, rate.Overhead, rate.GeneralAndAdministrative));
        Assert.Equal(129.7725m, rate.DirectRate + rate.Fringe + rate.Overhead + rate.GeneralAndAdministrative + rate.Fee);

        // Labor is priced at the bill rate; materials at cost with G&A and no fee.
        // 30 × 129.7725 = 3,893.18 and 10 × 129.7725 = 1,297.73 (each to the cent), + 1,000 materials + 100 G&A.
        Assert.Equal(3893.18m + 1297.73m + 1000m + 100m, price.Totals.Price);
        Assert.Equal(5819m, price.Totals.TotalCost); // 2,200 DL + 660 fringe + 1,430 overhead + 529 G&A + 1,000 materials
        Assert.Equal(price.Totals.Price - price.Totals.TotalCost, price.Totals.Fee);
    }
}

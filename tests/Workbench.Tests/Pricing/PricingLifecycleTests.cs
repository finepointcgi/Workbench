using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Pricing;
using Workbench.Contracts;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Financials;
using Workbench.Contracts.Pricing;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Tests.Pricing;

/// <summary>Scenarios, locked versions, the cost volume and the won handoff.</summary>
public sealed class PricingLifecycleTests
{
    [Fact]
    public async Task Scenarios_are_copies_compared_against_the_target()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (proposal, model) = await kit.NewAsync(target: 2300m);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        model = await kit.SettingsAsync(model, RateBasis.Provisional, fee: 0.08m);

        var scenario = await kit.Pricing.CopyModelAsync(model.Id, new CopyPriceModelRequest("Lower fee"));
        Assert.False(scenario.Ref.IsBase);
        Assert.Equal(model.Id, scenario.Ref.SourceModelId);
        Assert.Equal(model.LaborLines.Single().HoursByPeriod.Values, scenario.LaborLines.Single().HoursByPeriod.Values);
        Assert.NotEqual(model.Periods[0].Id, scenario.Periods[0].Id);
        await kit.SettingsAsync(scenario, RateBasis.Provisional, fee: 0.05m);

        var compare = await kit.Pricing.CompareAsync(proposal.Id);

        Assert.Equal(2300m, compare.TargetPrice);
        Assert.Equal(["Working price", "Lower fee"], compare.Rows.Select(r => r.Model.Name));
        Assert.Equal([2316.60m, 2252.25m], compare.Rows.Select(r => r.Price)); // 2,145 cost + 8% / 5%
        Assert.Equal([false, true], compare.Rows.Select(r => r.PriceToWin!.UnderTarget));
        Assert.Equal(16.60m, compare.Rows[0].PriceToWin!.Difference);

        // The working price is untouched by the scenario's edits.
        Assert.Equal(0.08m, (await kit.Pricing.GetModelAsync(model.Id)).Settings.FeeRate);
        await kit.Pricing.DeleteModelAsync(scenario.Id);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.DeleteModelAsync(model.Id));
        Assert.Single((await kit.Pricing.GetProposalAsync(proposal.Id)).Models);
    }

    [Fact]
    public async Task A_locked_version_is_read_only_frozen_and_audited()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (proposal, model) = await kit.NewAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest()));
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        model = await kit.SettingsAsync(model, RateBasis.Provisional, fee: 0.08m);

        var v1 = await kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest("Initial submission"));

        Assert.True(v1.Ref.IsLocked);
        Assert.Equal((1, "Version 1", "finance@example.com", "Initial submission"), (v1.Ref.Version, v1.Ref.Name, v1.Ref.LockedBy, v1.Ref.LockNote));
        Assert.Equal(ProposalState.Submitted, (await kit.Pricing.GetProposalAsync(proposal.Id)).Status);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.SaveLaborLinesAsync(v1.Id, []));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.SettingsAsync(v1, fee: 0.5m));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.DeleteModelAsync(v1.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.LockModelAsync(v1.Id, new LockPriceModelRequest()));

        // New book rates move the working price but not the submitted one.
        await kit.Books.SetRateAsync(Contracts.Accounting.CostPool.Overhead, 2027, RateBasis.Provisional, 0.40m, "Revised");
        Assert.Equal(2316.60m, (await kit.Pricing.CalculateAsync(v1.Id)).Totals.Price);
        Assert.True((await kit.Pricing.CalculateAsync(v1.Id)).IsLocked);
        Assert.Equal(2162.16m, (await kit.Pricing.CalculateAsync(model.Id)).Totals.Price); // 1,000 + 300 + 520 + 182 = 2,002 + 8% fee 160.16
        Assert.Equal(2, (await kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest())).Ref.Version);

        // A version can be copied to an editable scenario.
        var copy = await kit.Pricing.CopyModelAsync(v1.Id, new CopyPriceModelRequest("Rework v1"));
        Assert.False(copy.Ref.IsLocked);
        await kit.Pricing.SaveLaborLinesAsync(copy.Id, []);

        var history = await kit.Pricing.ListHistoryAsync(proposal.Id);
        var locked = history.Where(h => h.Action == "Locked").ToList();
        Assert.Equal(2, locked.Count);
        Assert.Contains(locked, h => h.EntityId == v1.Id && h.Reason == "Initial submission" && h.Actor == "finance@example.com");
        Assert.Contains(history, h => h.Action == "Created" && h.EntityId == proposal.Id);
        await using var db = await kit.Books.Factory.CreateAsync();
        Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == PricingService.ModelEntity && a.EntityId == v1.Id && a.Action == "Locked"));
    }

    [Fact]
    public async Task Structure_edits_are_validated()
    {
        var kit = await PricingKit.CreateAsync();
        var (_, model) = await kit.NewAsync(options: 1);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.SaveClinsAsync(model.Id, [new SavePriceClinRequest(null, "1", "Bad")]));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.SavePeriodsAsync(model.Id,
            [new SavePricePeriodRequest(null, "A", new DateOnly(2027, 1, 1), new DateOnly(2027, 12, 31), false), new SavePricePeriodRequest(null, "B", new DateOnly(2027, 6, 1), new DateOnly(2028, 5, 31), true)]));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(null, "01", "A", ParentCode: "09")]));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.LaborAsync(model, kit.Line(model, LaborRateSource.NamedEmployee, [1m, 1m])));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.CreateProposalAsync(new SaveProposalRequest(" prp-1", "Duplicate", PricingContractKind.FirmFixedPrice)));

        // Removing a period drops its hours; a WBS element with labor can't be removed.
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [5m, 7m], manualRate: 10m));
        model = await kit.Pricing.SavePeriodsAsync(model.Id, [new SavePricePeriodRequest(model.Periods[0].Id, "Base", model.Periods[0].Start, model.Periods[0].End, false)]);
        Assert.Equal(5m, model.LaborLines.Single().HoursByPeriod.Values.Sum());
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(null, "02", "Other")]));
        model = await kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(model.Wbs[0].Id, "01", "PM", "Basis"), new SavePriceWbsRequest(null, "01.01", "Reports", ParentCode: "01")]);
        Assert.Equal(model.Wbs[0].Id, model.Wbs[1].ParentId);
    }

    [Fact]
    public async Task The_cost_volume_has_every_sheet()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (_, model) = await kit.NewAsync(PricingContractKind.TimeAndMaterials, options: 1);
        await kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(model.Wbs[0].Id, "01", "Program management", "=HYPERLINK(\"http://x\") one PM, per the PWS")]);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [100m, 100m], manualRate: 80m));

        var file = await kit.Pricing.ExportCostVolumeAsync(model.Id);

        Assert.EndsWith(".xlsx", file.FileName);
        using var book = new XLWorkbook(new MemoryStream(file.Content));
        Assert.Equal(["Summary", "Labor build-up", "Indirect rates", "Basis of estimate", "Other direct costs", "T&M rates"], book.Worksheets.Select(w => w.Name));
        var basis = book.Worksheet("Basis of estimate");
        var narrative = basis.CellsUsed().Single(c => c.GetString().Contains("HYPERLINK"));
        Assert.False(narrative.HasFormula);
        Assert.Contains(book.Worksheet("T&M rates").CellsUsed(), c => c.GetString() == "ENG2");
    }

    [Fact]
    public async Task Won_handoff_creates_clins_bill_rates_wbs_and_a_budget()
    {
        var kit = await PricingKit.CreateAsync();
        await kit.SetRatesAsync(2027, RateBasis.Provisional, 0.30m, 0.50m, 0.10m);
        var (proposal, model) = await kit.NewAsync(PricingContractKind.TimeAndMaterials, options: 1);
        model = await kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(model.Wbs[0].Id, "10", "Engineering"), new SavePriceWbsRequest(null, "10.01", "Design", ParentCode: "10")]);
        var leaf = model.Wbs.Single(w => w.Code == "10.01").Id;
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [120m, 60m], manualRate: 80m) with { WbsId = leaf });
        await kit.Pricing.SaveCostLinesAsync(model.Id, [new SavePriceCostLineRequest(null, leaf, model.Clins[0].Id, model.Periods[0].Id, PriceCostType.Travel, "Site visit",
            Destination: "Huntsville", State: "AL", Trips: 1, Travelers: 1, Nights: 2, AirfarePerTrip: 400m)]);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.HandOffAsync(model.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id)));
        var v1 = await kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest());
        var price = await kit.Pricing.CalculateAsync(v1.Id);

        var result = await kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id, BudgetVersionKind.Current));

        Assert.Equal((1, 1, 2, 2), (result.ClinsCreated, result.ClinsUpdated, result.BillRatesAdded, result.WbsCreated));
        await using var db = await kit.Books.Factory.CreateAsync();
        var clins = await db.Set<Clin>().Where(c => c.ContractId == kit.Books.Contract.Id).OrderBy(c => c.Number).ToListAsync();
        Assert.Equal(["0001", "1001"], clins.Select(c => c.Number));
        Assert.Equal(price.Cells.Where(c => c.PeriodId == price.Periods[0].PeriodId).Sum(c => c.Figures.Price), clins[0].CeilingValue);
        Assert.Equal((true, false, ClinPricing.TimeAndMaterials), (clins[1].IsOption, clins[1].IsExercised, clins[1].Pricing));
        Assert.Equal(price.Cells.Single(c => c.PeriodId == price.Periods[1].PeriodId).Figures.Price, clins[1].CeilingValue);

        var rates = await db.Set<ContractLaborRate>().Where(r => r.ContractId == kit.Books.Contract.Id).OrderBy(r => r.EffectiveFrom).ToListAsync();
        Assert.Equal(price.BillRates.Select(b => (b.PeriodStart, b.BillRate)), rates.Select(r => (r.EffectiveFrom, r.HourlyBillRate)));
        Assert.Equal(new DateOnly(2027, 10, 1), rates[1].EffectiveFrom);

        var wbs = await db.Set<WbsElement>().Where(w => w.ProjectId == kit.Books.Project.Id && w.Code.StartsWith("2001.10")).OrderBy(w => w.Code).ToListAsync();
        Assert.Equal(["2001.10", "2001.10.01"], wbs.Select(w => w.Code));
        Assert.Equal((false, true, wbs[0].Id, clins[0].Id), (wbs[0].IsChargeable, wbs[1].IsChargeable, wbs[1].ParentId, wbs[1].ClinId));

        var version = await db.Set<BudgetVersion>().Include(v => v.Lines).SingleAsync(v => v.Id == result.BudgetVersionId);
        Assert.Equal((BudgetKind.Current, "Bid PRP-1 v1"), (version.Kind, version.Name));
        var labor = version.Lines.Where(l => l.CostType == BudgetCostType.Labor).ToList();
        Assert.Equal(180m, labor.Sum(l => l.Hours));
        Assert.All(labor, l => Assert.Equal((wbs[1].Id, kit.Category.Id, 80m), (l.WbsElementId, l.LaborCategoryId!.Value, l.PlanningRate)));
        Assert.Equal(12, labor.Count(l => l.PeriodStart is not null)); // the base period, phased over FY2027's months
        Assert.Equal(60m, labor.Single(l => l.PeriodStart is null).Hours); // FY2028 has no calendar yet: unphased
        Assert.Equal(price.OtherCosts.Single().Amount, version.Lines.Where(l => l.CostType == BudgetCostType.Odc).Sum(l => l.Amount));
        Assert.All(version.Lines.Where(l => l.CostType == BudgetCostType.Odc), l => Assert.Equal(OdcType.Travel, l.OdcType));
        Assert.Contains(result.Notes, n => n.Contains("unphased"));

        var won = await kit.Pricing.GetProposalAsync(proposal.Id);
        Assert.Equal(ProposalState.Won, won.Status);
        Assert.Equal((v1.Id, kit.Books.Contract.Id, kit.Books.Project.Id, result.BudgetVersionId), (won.Handoff!.ModelId, won.Handoff.ContractId, won.Handoff.ProjectId, won.Handoff.BudgetVersionId));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id, BudgetVersionKind.Forecast)));
        Assert.True(await db.Set<AuditEvent>().AnyAsync(a => a.EntityType == PricingService.ProposalEntity && a.Action == "HandedOff"));
    }

    [Fact]
    public async Task Handoff_refuses_a_project_on_another_contract_and_keeps_funded_clins()
    {
        var kit = await PricingKit.CreateAsync();
        var (_, model) = await kit.NewAsync(PricingContractKind.CostPlusFixedFee);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        var v1 = await kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest());
        await using (var db = await kit.Books.Factory.CreateAsync())
        {
            var clin = await db.Set<Clin>().SingleAsync(c => c.Id == kit.Books.ClinId);
            clin.CeilingValue = 5000m;
            clin.FundedValue = 5000m;
            db.Set<Project>().Add(new Project { Code = "3001", Name = "Other" });
            await db.SaveChangesAsync();
        }

        await using (var db = await kit.Books.Factory.CreateAsync())
        {
            var other = await db.Set<Project>().SingleAsync(p => p.Code == "3001");
            await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, other.Id)));
        }

        var result = await kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id));

        Assert.Equal((0, 0, 0), (result.ClinsCreated, result.ClinsUpdated, result.BillRatesAdded));
        Assert.Contains(result.Notes, n => n.Contains("already funded"));
        Assert.Contains(result.Notes, n => n.Contains("time-and-materials and labor-hour prices only"));
        await using var check = await kit.Books.Factory.CreateAsync();
        Assert.Equal(5000m, (await check.Set<Clin>().SingleAsync(c => c.Id == kit.Books.ClinId)).CeilingValue);
    }

    [Fact]
    public async Task Handoff_refuses_to_budget_on_an_existing_summary_element()
    {
        var kit = await PricingKit.CreateAsync();
        var (_, model) = await kit.NewAsync();
        model = await kit.Pricing.SaveWbsAsync(model.Id, [new SavePriceWbsRequest(model.Wbs[0].Id, "01", "Design")]);
        model = await kit.LaborAsync(model, kit.Line(model, LaborRateSource.Manual, [10m], manualRate: 100m));
        var v1 = await kit.Pricing.LockModelAsync(model.Id, new LockPriceModelRequest());
        await using (var db = await kit.Books.Factory.CreateAsync())
        {
            db.Set<WbsElement>().Add(new WbsElement { ProjectId = kit.Books.Project.Id, ParentId = kit.Books.Task1.Id, Code = "2001.01.01", Name = "Child" });
            await db.SaveChangesAsync();
        }

        var error = await Assert.ThrowsAsync<ArgumentException>(() => kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id)));
        Assert.Contains("2001.01", error.Message);

        // Without the budget the CLINs and WBS still go across.
        var result = await kit.Pricing.HandOffAsync(v1.Id, new HandoffRequest(kit.Books.Contract.Id, kit.Books.Project.Id, CreateBudget: false));
        Assert.Null(result.BudgetVersionId);
    }

    [Fact]
    public void Award_clin_numbers_carry_the_period()
    {
        Assert.Equal("0001", PricingHandoff.AwardNumber("0001", 0));
        Assert.Equal("1001", PricingHandoff.AwardNumber("0001", 1));
        Assert.Equal("4002AA", PricingHandoff.AwardNumber("0002aa", 4));
    }
}

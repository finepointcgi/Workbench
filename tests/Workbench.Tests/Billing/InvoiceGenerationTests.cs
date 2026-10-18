using Microsoft.EntityFrameworkCore;
using Workbench.Application.Billing;
using Workbench.Contracts.Billing;
using Workbench.Domain.Billing;

namespace Workbench.Tests.Billing;

public sealed class InvoiceGenerationTests
{
    private static BurdenedCostLine Cost(BillingKit kit, Domain.ProjectAccounting.WbsElement wbs, decimal labor, decimal fringe = 0, decimal overhead = 0, decimal ga = 0, decimal odc = 0, decimal hours = 0, decimal unallowable = 0) =>
        new(kit.ProjectId, wbs.Id, wbs.Code, wbs.ClinId, hours, labor, fringe, overhead, ga, odc, unallowable);

    [Fact]
    public async Task Time_and_materials_prices_each_hour_at_the_bill_rate_effective_on_the_work_date()
    {
        var kit = await BillingKit.CreateAsync();

        var preview = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials));

        var labor = preview.Lines.Where(l => l.Kind == InvoiceLineType.Labor).OrderBy(l => l.Rate).ToList();
        Assert.Equal(2, labor.Count);
        Assert.Equal((16m, 100m, 1_600m), (labor[0].Quantity, labor[0].Rate, labor[0].Amount)); // Oct 12–13, before the Oct 15 rate
        Assert.Equal((4m, 120m, 480m), (labor[1].Quantity, labor[1].Rate, labor[1].Amount));     // Oct 20
        Assert.All(preview.Lines, l => Assert.Equal("0003", l.ClinNumber));
        Assert.Equal(500m, Assert.Single(preview.Lines, l => l.OdcType == "Travel").Amount);
        Assert.Equal(1_000m, Assert.Single(preview.Lines, l => l.OdcType == "Materials").Amount);
        Assert.Equal(50m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.MaterialHandling).Amount);
        Assert.Equal(3_630m, preview.Total);
    }

    [Fact]
    public async Task A_missing_bill_rate_leaves_the_hours_unbilled_with_a_warning()
    {
        var kit = await BillingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            db.Set<Domain.ProjectAccounting.ContractLaborRate>().RemoveRange(await db.Set<Domain.ProjectAccounting.ContractLaborRate>().Where(r => r.EffectiveFrom == BillingKit.October).ToListAsync());
            await db.SaveChangesAsync();
        }

        var preview = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials));

        Assert.Equal(4m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Labor).Quantity);
        Assert.Contains(preview.Warnings, w => w.Contains("No bill rate for SE2", StringComparison.Ordinal) && w.Contains("16 h", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Each_hour_and_odc_is_billed_once_and_regenerating_a_draft_is_idempotent()
    {
        var kit = await BillingKit.CreateAsync();

        var first = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var again = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var regenerated = await kit.Invoices.RegenerateDraftAsync(first.Summary.Id);

        Assert.Equal(first.Summary.Id, again.Summary.Id);
        Assert.Equal(first.Summary.Number, regenerated.Summary.Number);
        Assert.Equal(3_630m, regenerated.Summary.Total);
        await using (var db = await kit.Factory.CreateAsync())
        {
            Assert.Single(await db.Set<Invoice>().ToListAsync());
            var sources = await db.Set<BilledSource>().ToListAsync();
            Assert.Equal(5, sources.Count); // three time entries, travel and materials
            Assert.Equal(sources.Count, sources.Select(s => s.SourceId).Distinct().Count());
        }

        // A draft holds its sources: an overlapping period (or a second run once approved) has nothing left.
        var overlap = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials, BillingKit.October.AddDays(10), BillingKit.OctoberEnd.AddDays(30)));
        Assert.Empty(overlap.Lines);
        await kit.Invoices.ApproveAsync(first.Summary.Id, new ApproveInvoiceRequest());
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1))));
    }

    [Fact]
    public async Task Removing_a_draft_line_frees_its_hours_for_the_next_run()
    {
        var kit = await BillingKit.CreateAsync();
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var line = draft.Lines.Single(l => l.Kind == InvoiceLineType.Labor && l.Rate == 120m);

        var after = await kit.Invoices.RemoveLineAsync(draft.Summary.Id, line.Id);
        Assert.Equal(3_150m, after.Summary.Total);

        var regenerated = await kit.Invoices.RegenerateDraftAsync(draft.Summary.Id);
        Assert.Equal(3_630m, regenerated.Summary.Total);
    }

    [Fact]
    public async Task Unallowable_cost_is_never_billed()
    {
        var kit = await BillingKit.CreateAsync(withIndirectRates: false);

        var tm = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        var cost = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus));
        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TimeAndMaterials));

        Assert.DoesNotContain(tm.Lines, l => l.OdcType == "Other");
        Assert.Contains(tm.Warnings, w => w.Contains("unallowable", StringComparison.OrdinalIgnoreCase) && w.Contains("$75.00", StringComparison.Ordinal));
        Assert.DoesNotContain(cost.Lines, l => l.Amount == 75m);
        var full = await kit.LoadAsync(draft.Summary.Id);
        Assert.DoesNotContain(full.Lines.SelectMany(l => l.Sources), s => s.SourceId == kit.UnallowableOdcId);
    }

    [Fact]
    public async Task Cost_plus_bills_burdened_cost_plus_fee_less_the_withhold()
    {
        var kit = await BillingKit.CreateAsync();
        kit.Burdened!.Set(RateBasis.Provisional,
            Cost(kit, kit.CostWbs, labor: 1_000m, fringe: 300m, overhead: 200m, ga: 150m, hours: 10),
            Cost(kit, kit.OdcWbs, labor: 0, odc: 400m));

        var draft = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.CostPlus));

        Assert.Equal(1_000m, Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Labor).Amount);
        Assert.Equal(650m, Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Burden).Amount);
        Assert.Equal(400m, Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Odc && l.ClinNumber == "0002").Amount);
        var fee = Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.Fee);
        Assert.Equal(("0001", 115.50m), (fee.ClinNumber, fee.Amount)); // 7% of 1,650; CLIN 0002 is cost, no fee
        var withhold = Assert.Single(draft.Lines, l => l.Kind == InvoiceLineType.FeeWithhold);
        Assert.Equal(-17.33m, withhold.Amount); // 15% of fee, rounded half away from zero
        Assert.Equal(2_148.17m, draft.Summary.Total);
        Assert.Equal(17.33m, draft.Withheld);
        Assert.Contains("Provisional FY2027", draft.RateNote);
        Assert.Equal(RateBasis.Provisional, kit.Burdened.Calls[^1].Basis);
    }

    [Fact]
    public async Task Cost_plus_bills_cumulative_cost_less_what_was_billed_and_releases_the_withhold_on_the_final_invoice()
    {
        var kit = await BillingKit.CreateAsync();
        kit.Burdened!.Set(RateBasis.Provisional, Cost(kit, kit.CostWbs, labor: 1_000m, fringe: 300m, overhead: 200m, ga: 150m));
        await kit.ApprovedAsync(InvoiceKind.CostPlus);

        // November: cumulative cost is now 2,500 (labor 1,500 + burden 1,000), so 850 more is billed.
        kit.Burdened.Set(RateBasis.Provisional, Cost(kit, kit.CostWbs, labor: 1_500m, fringe: 450m, overhead: 300m, ga: 250m));
        var november = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1)));
        Assert.Equal(500m, Assert.Single(november.Lines, l => l.Kind == InvoiceLineType.Labor).Amount);
        Assert.Equal(350m, Assert.Single(november.Lines, l => l.Kind == InvoiceLineType.Burden).Amount);
        Assert.Equal(59.50m, Assert.Single(november.Lines, l => l.Kind == InvoiceLineType.Fee).Amount);
        Assert.Equal(BillingKit.October, kit.Burdened.Calls[^1].From);

        var final = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus, BillingKit.October.AddMonths(1), BillingKit.OctoberEnd.AddMonths(1), release: true));
        Assert.DoesNotContain(final.Lines, l => l.Kind == InvoiceLineType.FeeWithhold);
        Assert.Equal(17.33m, Assert.Single(final.Lines, l => l.Kind == InvoiceLineType.WithholdRelease).Amount);
    }

    [Fact]
    public async Task Without_indirect_rates_cost_plus_falls_back_to_direct_cost_and_says_so()
    {
        var kit = await BillingKit.CreateAsync(withIndirectRates: false);

        var preview = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.CostPlus));

        Assert.Equal(BillingCostSource.NotConfiguredNote, preview.RateNote);
        Assert.Equal(500m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Labor).Amount); // 10 h × $50 cost
        Assert.DoesNotContain(preview.Lines, l => l.Kind == InvoiceLineType.Burden);
        Assert.Equal(400m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Odc).Amount);
        Assert.Equal(35m, Assert.Single(preview.Lines, l => l.Kind == InvoiceLineType.Fee).Amount);
        Assert.Equal(929.75m, preview.Total); // 500 + 400 + 35 fee − 5.25 withheld
    }

    [Fact]
    public async Task The_true_up_bills_actual_rates_minus_provisional_billed_with_fee()
    {
        var kit = await BillingKit.CreateAsync();
        kit.Burdened!.Set(RateBasis.Provisional,
            Cost(kit, kit.CostWbs, labor: 1_000m, fringe: 300m, overhead: 200m, ga: 150m),
            Cost(kit, kit.OdcWbs, labor: 0, odc: 400m));
        await kit.ApprovedAsync(InvoiceKind.CostPlus);
        kit.Burdened.Set(RateBasis.Actual,
            Cost(kit, kit.CostWbs, labor: 1_000m, fringe: 330m, overhead: 220m, ga: 165m),
            Cost(kit, kit.OdcWbs, labor: 0, odc: 400m));

        var year = (From: new DateOnly(2026, 10, 1), To: new DateOnly(2027, 9, 30));
        var trueUp = await kit.Invoices.GenerateDraftAsync(kit.Request(InvoiceKind.TrueUp, year.From, year.To));

        Assert.Equal(RateBasis.Actual, kit.Burdened.Calls[^1].Basis);
        Assert.Equal(65m, Assert.Single(trueUp.Lines, l => l.Kind == InvoiceLineType.Burden).Amount);
        Assert.DoesNotContain(trueUp.Lines, l => l.Kind is InvoiceLineType.Labor or InvoiceLineType.Odc);
        Assert.Equal(4.55m, Assert.Single(trueUp.Lines, l => l.Kind == InvoiceLineType.Fee).Amount);
        Assert.Equal(68.87m, trueUp.Summary.Total); // 65 + 4.55 − 0.68 withheld
        Assert.Equal(InvoiceKind.TrueUp, trueUp.Summary.Type);
    }

    [Fact]
    public async Task A_true_up_needs_indirect_rates()
    {
        var kit = await BillingKit.CreateAsync(withIndirectRates: false);
        var preview = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TrueUp, new DateOnly(2026, 10, 1), new DateOnly(2027, 9, 30)));
        Assert.Empty(preview.Lines);
        Assert.Contains(preview.Warnings, w => w.Contains("indirect rates aren't configured", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Ffp_bills_completed_unbilled_milestones_once()
    {
        var kit = await BillingKit.CreateAsync();
        var design = await kit.Milestones.CreateAsync(kit.ContractId, new SaveMilestoneRequest(kit.FfpClin.Id, "Design review", 5_000m, new DateOnly(2026, 10, 15)));
        await kit.Milestones.CreateAsync(kit.ContractId, new SaveMilestoneRequest(kit.FfpClin.Id, "Final report", 7_000m, new DateOnly(2026, 10, 30)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Milestones.CreateAsync(kit.ContractId, new SaveMilestoneRequest(kit.TmClin.Id, "Not FFP", 1m, BillingKit.October)));

        var none = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.FixedPriceMilestone));
        Assert.Empty(none.Lines);
        Assert.Equal(2, none.Warnings.Count(w => w.Contains("isn't complete", StringComparison.Ordinal)));

        await kit.Milestones.CompleteAsync(design.Id, new CompleteMilestoneRequest(new DateOnly(2026, 10, 20)));
        var invoice = await kit.ApprovedAsync(InvoiceKind.FixedPriceMilestone);

        var line = Assert.Single(invoice.Lines);
        Assert.Equal((InvoiceLineType.Milestone, 5_000m, "0004"), (line.Kind, line.Amount, line.ClinNumber));
        var listed = await kit.Milestones.ListAsync(kit.ContractId);
        Assert.Equal(invoice.Summary.Number, listed.Single(m => m.Id == design.Id).InvoiceNumber);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Milestones.ReopenAsync(design.Id));
        Assert.Empty((await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.FixedPriceMilestone))).Lines);
    }

    [Fact]
    public async Task Billing_skips_unexercised_options()
    {
        var kit = await BillingKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            (await db.Set<Domain.ProjectAccounting.Clin>().FirstAsync(c => c.Id == kit.TmClin.Id)).IsExercised = false;
            await db.SaveChangesAsync();
        }

        var preview = await kit.Invoices.PreviewAsync(kit.Request(InvoiceKind.TimeAndMaterials));
        Assert.Empty(preview.Lines);
        Assert.Contains(preview.Warnings, w => w.Contains("unexercised option", StringComparison.Ordinal));
    }
}

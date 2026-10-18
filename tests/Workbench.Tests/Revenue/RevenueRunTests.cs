using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.Revenue;
using Workbench.Domain.Accounting;

namespace Workbench.Tests.Revenue;

/// <summary>Revenue runs over <see cref="RevenueKit"/>: each method, idempotency, catch-up, loss provision, reversal and the close check.</summary>
public sealed class RevenueRunTests
{
    private static readonly DateOnly Oct1 = new(2026, 10, 1);

    [Fact]
    public async Task Cost_plus_recognizes_burdened_cost_plus_fee_and_reclasses_unbilled_once_billing_invoices()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(1_650m, 400m);
        await kit.SetupAsync(RevenueMethodKind.CostPlusFee);

        var preview = await kit.Runs.PreviewAsync(await kit.PeriodAsync("2026-10"));
        var row = Assert.Single(preview.Projects);
        Assert.Equal(2_165.50m, row.Computed.RecognizedToDate); // 1,650 + 7% fee on CLIN 0001, plus 400 at cost on CLIN 0002
        Assert.Null(row.Posted);

        var result = await kit.PostAsync("2026-10");
        var run = Assert.Single(result.Posted);
        Assert.Equal((2_165.50m, 2_165.50m, 2_165.50m, 0m), (run.RecognizedToDate, run.PeriodRevenue, run.UnbilledBalance, run.BilledToDate));
        var entry = Assert.Single(await kit.EntriesAsync(JournalSource.Revenue));
        Assert.Equal(entry.TotalDebit, entry.TotalCredit);
        Assert.Equal(new DateOnly(2026, 10, 31), entry.Date);
        Assert.Equal(2_165.50m, await kit.BalanceAsync("1120"));
        Assert.Equal(-2_165.50m, await kit.BalanceAsync("4010"));

        // Idempotent: posting the period again posts nothing, and the preview shows it posted and current.
        var again = await kit.PostAsync("2026-10");
        Assert.Empty(again.Posted);
        Assert.Single(await kit.EntriesAsync(JournalSource.Revenue));
        var after = Assert.Single((await kit.Runs.PreviewAsync(await kit.PeriodAsync("2026-10"))).Projects);
        Assert.NotNull(after.Posted);
        Assert.False(after.IsStale);

        // Billing invoices October in November (credits revenue itself); November's run reclasses unbilled against it.
        await kit.Billing.ApprovedAsync(InvoiceKind.CostPlus);
        Assert.Equal(-2 * 2_165.50m, await kit.BalanceAsync("4010"));
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal((2_165.50m, 2_165.50m, 0m, 0m), (nov.RecognizedToDate, nov.BilledToDate, nov.PeriodRevenue, nov.UnbilledBalance));
        Assert.Equal(-2_165.50m, await kit.BalanceAsync("4010"));
        Assert.Equal(17.33m, await kit.BalanceAsync("1120")); // only billing's fee withhold remains in AR unbilled
        Assert.Equal(2_165.50m, await kit.RevenueAsync());

        var vs = await kit.Reports.GetRevenueVsBillingsAsync(await kit.PeriodAsync("2026-11"));
        var vsRow = Assert.Single(vs.Rows);
        Assert.Equal((2_165.50m, 2_165.50m, 0m, 0m), (vsRow.RecognizedToDate, vsRow.BilledToDate, vsRow.ContractAsset, vsRow.ContractLiability));

        var byPeriod = await kit.Reports.GetRevenueByPeriodAsync(new RevenueByPeriodQuery(await kit.PeriodAsync("2026-10"), await kit.PeriodAsync("2026-12")));
        Assert.Equal(["2026-10", "2026-11", "2026-12"], byPeriod.Periods);
        Assert.Equal(new[] { 2_165.50m, 0m, 0m }, Assert.Single(byPeriod.Rows).Amounts);

        var backlog = Assert.Single((await kit.Reports.GetBacklogAsync(await kit.PeriodAsync("2026-11"))).Rows);
        Assert.Equal(670_000m, backlog.ContractValue);
        Assert.Equal(180_000m - 2_165.50m, backlog.FundedBacklog);
        Assert.Equal(670_000m - 180_000m, backlog.UnfundedBacklog);
    }

    [Fact]
    public async Task Cost_to_cost_uses_the_approved_estimate_and_catches_up_when_it_changes()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(1_000m, 400m);
        await kit.SetupAsync(RevenueMethodKind.CostToCost);

        // Without an approved estimate it falls back to billings (nothing billed: nothing to post).
        var pending = await kit.Setup.SubmitEstimateAsync(new SubmitEstimateRequest(kit.ProjectId, Oct1, 10_000m, 12_000m));
        Assert.All((await kit.Runs.PreviewAsync(await kit.PeriodAsync("2026-10"))).Projects, p => Assert.False(p.Computed.HasEntry));

        await kit.Setup.ApproveEstimateAsync(pending.Id, new DecideEstimateRequest());
        var oct = Assert.Single((await kit.PostAsync("2026-10")).Posted);
        var line = Assert.Single(oct.Lines);
        Assert.Equal((0.14m, 1_680m), (line.PercentComplete, oct.RecognizedToDate)); // 1,400 / 10,000 × 12,000

        // A lower EAC in November: 1,400 / 7,000 = 20% → 2,400 to date, a 720 catch-up.
        await kit.ApprovedEstimateAsync(new DateOnly(2026, 11, 15), 7_000m, 12_000m);
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal((2_400m, 720m), (nov.RecognizedToDate, nov.PeriodRevenue));
        Assert.Equal(2_400m, await kit.RevenueAsync());
        Assert.Equal(2_400m, await kit.BalanceAsync("1120"));

        // October's figures keep the estimate in force then.
        var octAgain = (await kit.Engine.ComputeAsync(await DbAsync(kit), kit.ProjectId, await PeriodEntityAsync(kit, "2026-10"), default))!;
        Assert.Equal(1_680m, octAgain.RecognizedToDate);
    }

    [Fact]
    public async Task A_loss_contract_recognizes_the_whole_expected_loss_at_once()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(1_000m, 400m);
        await kit.SetupAsync(RevenueMethodKind.CostToCost);
        await kit.ApprovedEstimateAsync(Oct1, 15_000m, 12_000m);

        var run = Assert.Single((await kit.PostAsync("2026-10")).Posted);
        Assert.Equal((1_120m, 2_720m), (run.RecognizedToDate, run.LossProvisionBalance));
        Assert.Equal(3_000m, Assert.Single(run.Lines).ExpectedLoss);
        Assert.Equal(2_720m, await kit.BalanceAsync("7030"));
        Assert.Equal(-2_720m, await kit.BalanceAsync("2310"));

        // Revenue 1,120 − cost 1,400 − provision 2,720 = the full 3,000 loss.
        Assert.Equal(-3_000m, 1_120m - 1_400m - await kit.BalanceAsync("7030"));

        // More cost in November: the provision unwinds as the loss is incurred.
        kit.SetCost(4_600m, 400m);
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal(4_000m, nov.RecognizedToDate); // 5,000 / 15,000 × 12,000
        Assert.Equal(2_000m, nov.LossProvisionBalance); // 3,000 − (5,000 − 4,000)
        Assert.Equal(-2_000m, await kit.BalanceAsync("2310"));
    }

    [Fact]
    public async Task Time_and_materials_as_incurred_prices_hours_at_bill_rates_and_marks_up_odcs()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(0);
        await kit.SetupAsync(RevenueMethodKind.TimeAndMaterialsIncurred, kit.Billing.TmClin.Id, markup: 0.10m);

        var run = Assert.Single((await kit.PostAsync("2026-10")).Posted);
        // 16 h × 100 + 4 h × 120 = 2,080; travel 500 + materials 1,000 allowable, approved, +10% = 1,650.
        Assert.Equal(3_730m, run.RecognizedToDate);
        Assert.Equal(-3_730m, await kit.BalanceAsync("4020"));
        Assert.Equal(2, run.Lines.Count);
        Assert.Equal(RevenueMethodKind.Billings, run.Lines.Single(l => l.ClinId is null).Method);

        // T&M billed in November: the CLIN's billed amount offsets its unbilled.
        await kit.Billing.ApprovedAsync(InvoiceKind.TimeAndMaterials);
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        var tm = nov.Lines.Single(l => l.ClinId == kit.Billing.TmClin.Id);
        Assert.True(tm.BilledToDate > 0);
        Assert.Equal(3_730m - tm.BilledToDate + 0m, nov.UnbilledBalance - nov.BillingsInExcessBalance);
    }

    [Fact]
    public async Task Fixed_price_recognizes_completed_milestones_and_units_delivered()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(0);
        var ffp = kit.Billing.FfpClin.Id;
        await kit.SetupAsync(RevenueMethodKind.FixedPriceMilestones, ffp);
        var done = await kit.Billing.Milestones.CreateAsync(kit.Billing.ContractId, new SaveMilestoneRequest(ffp, "Design review", 5_000m, new DateOnly(2026, 10, 20)));
        await kit.Billing.Milestones.CompleteAsync(done.Id, new CompleteMilestoneRequest(new DateOnly(2026, 10, 20)));
        await kit.Billing.Milestones.CreateAsync(kit.Billing.ContractId, new SaveMilestoneRequest(ffp, "Final delivery", 15_000m, new DateOnly(2027, 3, 1)));

        var run = Assert.Single((await kit.PostAsync("2026-10")).Posted);
        Assert.Equal(5_000m, run.RecognizedToDate);
        Assert.Equal(-5_000m, await kit.BalanceAsync("4040"));

        // Switch the project unit to units delivered: 3 then 2 more units at 250, capped at 1,000.
        await kit.SetupAsync(RevenueMethodKind.FixedPriceUnits, unitPrice: 250m, price: 1_000m);
        await kit.Setup.RecordDeliveryAsync(new RecordDeliveryRequest(kit.ProjectId, new DateOnly(2026, 11, 3), 3, "Lot 1"));
        Assert.Equal(5_750m, (await kit.Engine.ComputeAsync(await DbAsync(kit), kit.ProjectId, await PeriodEntityAsync(kit, "2026-11"), default))!.RecognizedToDate);
        await kit.Setup.RecordDeliveryAsync(new RecordDeliveryRequest(kit.ProjectId, new DateOnly(2026, 11, 20), 2, "Lot 2"));
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal((6_000m, 1_000m), (nov.RecognizedToDate, nov.PeriodRevenue));
    }

    [Fact]
    public async Task Runs_reverse_in_order_and_closed_periods_are_refused()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(1_650m, 400m);
        await kit.SetupAsync(RevenueMethodKind.CostPlusFee);
        var oct = Assert.Single((await kit.PostAsync("2026-10")).Posted);
        kit.SetCost(2_650m, 400m);
        var nov = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal(1_070m, nov.PeriodRevenue);

        // October can't be reversed (or reposted) while November is posted.
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Runs.ReverseAsync(oct.Id, new ReverseRevenueRunRequest("Redo")));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ReverseAsync(nov.Id, new ReverseRevenueRunRequest(" ")));

        var reversed = await kit.Runs.ReverseAsync(nov.Id, new ReverseRevenueRunRequest("EAC update"));
        Assert.Equal(RevenueRunState.Reversed, reversed.Status);
        Assert.NotNull(reversed.ReversalEntryNumber);
        Assert.Equal(2_165.50m, await kit.RevenueAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Runs.ReverseAsync(nov.Id, new ReverseRevenueRunRequest("Again")));

        // Run November again after the reversal.
        var rerun = Assert.Single((await kit.PostAsync("2026-11")).Posted);
        Assert.Equal(3_235.50m, rerun.RecognizedToDate);
        Assert.Equal(2, (await kit.Runs.ListRunsAsync(await kit.PeriodAsync("2026-11"))).Count);

        await kit.ClosePeriodAsync("2026-12");
        var closed = await Assert.ThrowsAsync<ArgumentException>(() => kit.PostAsync("2026-12"));
        Assert.Contains("closed", closed.Message);
        await kit.ClosePeriodAsync("2026-11");
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Runs.ReverseAsync(rerun.Id, new ReverseRevenueRunRequest("Too late")));
    }

    [Fact]
    public async Task The_close_checklist_requires_the_revenue_run()
    {
        var kit = await RevenueKit.CreateAsync();
        kit.SetCost(1_650m, 400m);
        var period = await kit.PeriodAsync("2026-10");

        // Revenue = billings (the default) has nothing to post.
        Assert.True((await kit.Close.GetChecklistAsync(period)).Checks.Single(c => c.Kind == CloseCheckKind.RevenueRecognized).Passed);

        await kit.SetupAsync(RevenueMethodKind.CostPlusFee);
        var check = (await kit.Close.GetChecklistAsync(period)).Checks.Single(c => c.Kind == CloseCheckKind.RevenueRecognized);
        Assert.False(check.Passed);
        Assert.Contains("1 project(s) without a revenue run", check.Summary);

        await kit.PostAsync("2026-10");
        check = (await kit.Close.GetChecklistAsync(period)).Checks.Single(c => c.Kind == CloseCheckKind.RevenueRecognized);
        Assert.True(check.Passed);
        Assert.Empty(check.Details);

        // Late cost after the run: still passes, but says the run is out of date.
        kit.SetCost(1_750m, 400m);
        check = (await kit.Close.GetChecklistAsync(period)).Checks.Single(c => c.Kind == CloseCheckKind.RevenueRecognized);
        Assert.True(check.Passed);
        Assert.Contains("changed since the run", Assert.Single(check.Details));
    }

    [Fact]
    public async Task Without_indirect_rates_cost_plus_uses_direct_cost_and_says_so()
    {
        var kit = await RevenueKit.CreateAsync(withIndirectRates: false);
        await kit.SetupAsync(RevenueMethodKind.CostPlusFee);
        var row = Assert.Single((await kit.Runs.PreviewAsync(await kit.PeriodAsync("2026-10"))).Projects);
        // Dana: 10 h × 50 on CLIN 0001 (+7%) = 535; 20 h × 50 + 1,500 ODCs on the T&M CLIN (no fee) = 2,500; travel 400.
        Assert.Equal(535m + 2_500m + 400m, row.Computed.RecognizedToDate);
        Assert.Contains(row.Computed.Notes, n => n.Contains("Indirect rates not configured"));
    }

    [Fact]
    public async Task Estimates_are_validated_and_decided_once()
    {
        var kit = await RevenueKit.CreateAsync();
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Setup.SubmitEstimateAsync(new SubmitEstimateRequest(kit.ProjectId, Oct1, 0, 100)));
        var e = await kit.Setup.SubmitEstimateAsync(new SubmitEstimateRequest(kit.ProjectId, Oct1, 100, 120, Notes: "First cut"));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Setup.RejectEstimateAsync(e.Id, new DecideEstimateRequest()));
        var rejected = await kit.Setup.RejectEstimateAsync(e.Id, new DecideEstimateRequest("Too low"));
        Assert.Equal(EstimateState.Rejected, rejected.Status);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kit.Setup.ApproveEstimateAsync(e.Id, new DecideEstimateRequest()));
        Assert.Single(await kit.Setup.ListEstimatesAsync(EstimateState.Rejected));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.SetupAsync(RevenueMethodKind.FixedPriceUnits));

        var project = await kit.Setup.GetProjectAsync(kit.ProjectId);
        Assert.Equal(4, project.Clins.Count);
        Assert.Equal(670_000m, project.SuggestedTransactionPrice);
    }

    private static async Task<Workbench.Application.IWorkbenchDbContext> DbAsync(RevenueKit kit) => await kit.Billing.Factory.CreateAsync();

    private static async Task<Domain.ProjectAccounting.FiscalPeriod> PeriodEntityAsync(RevenueKit kit, string name)
    {
        await using var db = await kit.Billing.Factory.CreateAsync();
        return await db.Set<Domain.ProjectAccounting.FiscalPeriod>().AsNoTracking().FirstAsync(p => p.Name == name);
    }
}

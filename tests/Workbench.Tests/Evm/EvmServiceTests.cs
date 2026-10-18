using ClosedXML.Excel;
using Workbench.Application.Evm;
using Workbench.Contracts.Evm;

namespace Workbench.Tests.Evm;

public sealed class EvmServiceTests
{
    private static async Task<(EvmKit Kit, EvmBaselineDto Baseline)> BaselineAsync()
    {
        var kit = await EvmKit.CreateAsync();
        var pmb = await kit.Evm.CreateBaselineAsync(kit.Project.Id, new CreateEvmBaselineRequest(ControlAccountLevel: 2, ManagementReserve: 3_000));
        Guid Wp(string code) => pmb.WorkPackages.Single(w => w.WbsCode == $"1001.01.{code}").Id;
        await kit.Evm.UpdateWorkPackageAsync(Wp("A"), new UpdateWorkPackageRequest(EvTechnique.ZeroHundred));
        await kit.Evm.UpdateWorkPackageAsync(Wp("C"), new UpdateWorkPackageRequest(EvTechnique.Milestones));
        await kit.Evm.UpdateWorkPackageAsync(Wp("D"), new UpdateWorkPackageRequest(EvTechnique.LevelOfEffort));
        pmb = await kit.Evm.SetThresholdsAsync(kit.Project.Id, new SetEvmThresholdsRequest(10, 1_000));

        await kit.SetPlanAsync("A", status: "Done");
        await kit.SetPlanAsync("B", status: "In progress", percent: 40);
        await kit.SetPlanAsync("C1", status: "Done");
        await kit.CostAsync("A", new(2026, 10, 20), 12_000);
        await kit.CostAsync("B", new(2026, 11, 3), 9_000);
        await kit.CostAsync("C", new(2026, 11, 10), 14_000);
        await kit.CostAsync("D", new(2026, 10, 15), 4_000);
        return (kit, pmb);
    }

    [Fact]
    public async Task The_pmb_comes_from_the_locked_budget_timed_by_the_schedule_baseline()
    {
        var (kit, pmb) = await BaselineAsync();

        Assert.Equal(4, pmb.WorkPackages.Count);
        Assert.Equal(72_000m, pmb.DistributedBudget);
        Assert.Equal(5_000m, pmb.UndistributedBudget); // the root isn't a leaf
        Assert.Equal(77_000m, pmb.PerformanceMeasurementBaseline);
        Assert.Equal(80_000m, pmb.ContractBudgetBase);
        Assert.Equal("PMB schedule", pmb.ScheduleBaselineName);
        var b = pmb.WorkPackages.Single(w => w.WbsCode == "1001.01.B");
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 12, 31)), (b.PlannedStart, b.PlannedFinish)); // not the task's current dates
        Assert.Equal(4, pmb.ControlAccounts.Count);
        var c = pmb.WorkPackages.Single(w => w.WbsCode == "1001.01.C");
        Assert.Equal([50m, 50m], c.Milestones.Select(m => m.Weight));
        Assert.Equal(kit.Items["C1"].Id, c.Milestones[0].ItemId);
    }

    [Fact]
    public async Task Building_a_pmb_needs_a_locked_baseline_budget()
    {
        var kit = await EvmKit.CreateAsync();
        await using (var db = await kit.Factory.CreateAsync())
        {
            var version = db.Set<Domain.ProjectAccounting.BudgetVersion>().Single();
            version.IsLocked = false;
            await db.SaveChangesAsync();
        }

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.CreateBaselineAsync(kit.Project.Id, new CreateEvmBaselineRequest()));
        Assert.Contains("Lock", ex.Message);
    }

    [Fact]
    public async Task Status_earns_each_technique_and_rolls_up_the_indices()
    {
        var (kit, _) = await BaselineAsync();
        var nov = kit.Periods["2026-11"];

        var status = await kit.Evm.GetStatusAsync(kit.Project.Id);

        Assert.Equal(nov.Id, status.Period.FiscalPeriodId);
        Assert.Equal(EvmPeriodState.Reporting, status.Period.State);
        var wp = status.ControlAccounts.SelectMany(c => c.WorkPackages).ToDictionary(w => w.Code);
        var pvD = EvmCalculator.PlannedValue(12_000, new(2026, 10, 1), new(2027, 3, 31), nov.End);
        Assert.Equal(10_000m, wp["1001.01.A"].Cumulative.Bcwp); // 0/100, done
        Assert.Equal(8_000m, wp["1001.01.B"].Cumulative.Bcwp); // 40% complete from the plan sheet
        Assert.Equal(15_000m, wp["1001.01.C"].Cumulative.Bcwp); // one of two 50% milestones
        Assert.Equal(pvD, wp["1001.01.D"].Cumulative.Bcwp); // level of effort earns as planned
        Assert.Equal(0m, wp["1001.01.D"].Cumulative.Sv);
        Assert.Equal(39_000m, status.Total.Acwp);
        Assert.Equal(33_000m + pvD, status.Total.Bcwp);
        Assert.Equal(EvmCalculator.Metrics(72_000, status.Total.Bcws, status.Total.Bcwp, 39_000, status.ControlAccounts.Sum(c => c.Cumulative.EacManager)), status.Total);
        Assert.Equal(77_000m, status.PerformanceMeasurementBaseline);

        var a = status.ControlAccounts.Single(c => c.Code == "1001.01.A");
        Assert.True(a.NeedsVar); // CV -$2,000 (-20%)
        Assert.False(a.VarComplete);
    }

    [Fact]
    public async Task Entered_progress_overrides_the_plan_sheet()
    {
        var (kit, pmb) = await BaselineAsync();
        await kit.Evm.SetProgressAsync(pmb.WorkPackages.Single(w => w.WbsCode == "1001.01.B").Id, new SetWorkPackageProgressRequest(75));

        var status = await kit.Evm.GetStatusAsync(kit.Project.Id);

        var b = status.ControlAccounts.SelectMany(c => c.WorkPackages).Single(w => w.Code == "1001.01.B");
        Assert.Equal(15_000m, b.Cumulative.Bcwp);
        Assert.Equal("Entered 75%", b.ProgressSource);
    }

    [Fact]
    public async Task A_closed_period_is_frozen()
    {
        var (kit, pmb) = await BaselineAsync();
        var oct = kit.Periods["2026-10"];
        var before = await kit.Evm.GetStatusAsync(kit.Project.Id, oct.Id);

        var closed = await kit.Evm.ClosePeriodAsync(kit.Project.Id, oct.Id);
        Assert.Equal(EvmPeriodState.Closed, closed.State);

        // Progress, cost and the baseline all move on; October doesn't.
        await kit.SetPlanAsync("B", percent: 90);
        await kit.CostAsync("B", new(2026, 10, 28), 2_500);
        await kit.Changes.ApproveAsync((await kit.Changes.CreateAsync(kit.Project.Id, new CreateChangeRequestRequest("More B", "Scope",
            [new SaveChangeLineRequest(kit.Wbs["B"].Id, EvmBudgetSourceKind.ManagementReserve, 1_000)]))).Id,
            new DecideChangeRequestRequest(null));

        var after = await kit.Evm.GetStatusAsync(kit.Project.Id, oct.Id);
        Assert.True(after.IsFrozen);
        Assert.Equal(before.Total.Bcws, after.Total.Bcws);
        Assert.Equal(before.Total.Bcwp, after.Total.Bcwp);
        Assert.Equal(before.Total.Acwp, after.Total.Acwp);
        Assert.Equal(before.Total.Bac, after.Total.Bac);
        Assert.Equal(0, after.BaselineRevision);

        // November's period amounts are measured from October's close (including the late October cost).
        var nov = await kit.Evm.GetStatusAsync(kit.Project.Id);
        Assert.Equal(nov.Total.Acwp - after.Total.Acwp, nov.PeriodTotal.Acwp);
        Assert.Equal(41_500m, nov.Total.Acwp);
        Assert.Equal(1, nov.BaselineRevision);

        var ca = after.ControlAccounts[0].WbsElementId;
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.SaveVarianceAsync(kit.Project.Id, oct.Id, ca, new SaveVarianceRequest("x", "y", "z", null)));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.ClosePeriodAsync(kit.Project.Id, oct.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.ClosePeriodAsync(kit.Project.Id, kit.Periods["2026-12"].Id)); // not started
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.UpdateWorkPackageAsync(pmb.WorkPackages[0].Id, new UpdateWorkPackageRequest(EvTechnique.FiftyFifty)));
    }

    [Fact]
    public async Task Periods_close_in_order_and_only_the_latest_reopens()
    {
        var (kit, _) = await BaselineAsync();
        var oct = kit.Periods["2026-10"];
        var nov = kit.Periods["2026-11"];
        await kit.Evm.ClosePeriodAsync(kit.Project.Id, nov.Id);

        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.ClosePeriodAsync(kit.Project.Id, oct.Id));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Evm.ReopenPeriodAsync(kit.Project.Id, oct.Id, new ReopenEvmPeriodRequest("typo")));

        var reopened = await kit.Evm.ReopenPeriodAsync(kit.Project.Id, nov.Id, new ReopenEvmPeriodRequest("Late timesheets"));
        Assert.Equal(EvmPeriodState.Reporting, reopened.State);
        Assert.False((await kit.Evm.GetStatusAsync(kit.Project.Id, nov.Id)).IsFrozen);
        await kit.Evm.ClosePeriodAsync(kit.Project.Id, oct.Id);
    }

    [Fact]
    public async Task Closing_records_when_plan_milestones_were_met()
    {
        var (kit, _) = await BaselineAsync();
        await kit.Evm.ClosePeriodAsync(kit.Project.Id, kit.Periods["2026-11"].Id);

        var pmb = await kit.Evm.GetBaselineAsync(kit.Project.Id);

        var c = pmb!.WorkPackages.Single(w => w.WbsCode == "1001.01.C");
        Assert.Equal(EvmKit.Today, c.Milestones[0].CompletedOn);
        Assert.Null(c.Milestones[1].CompletedOn);
    }

    [Fact]
    public async Task Variance_analyses_and_the_managers_eac()
    {
        var (kit, _) = await BaselineAsync();
        var nov = kit.Periods["2026-11"];
        var a = kit.Wbs["A"].Id;

        var saved = await kit.Evm.SaveVarianceAsync(kit.Project.Id, nov.Id, a, new SaveVarianceRequest("Travel overran", "None on schedule", "Rebid travel", 12_500));

        Assert.True(saved.NeedsVar);
        Assert.True(saved.IsComplete);
        Assert.Equal(12_500m, saved.Eac);
        Assert.Equal(-2_500m, saved.Vac);
        var status = await kit.Evm.GetStatusAsync(kit.Project.Id);
        Assert.True(status.ControlAccounts.Single(c => c.WbsElementId == a).VarComplete);
        await Assert.ThrowsAsync<Contracts.WorkbenchNotFoundException>(() =>
            kit.Evm.SaveVarianceAsync(kit.Project.Id, nov.Id, kit.Wbs["root"].Id, new SaveVarianceRequest("x", null, null, null)));
    }

    [Fact]
    public async Task Approving_a_change_moves_reserve_and_keeps_before_and_after()
    {
        var (kit, _) = await BaselineAsync();
        var request = await kit.Changes.CreateAsync(kit.Project.Id, new CreateChangeRequestRequest("Add interface work", "Customer direction",
        [
            new SaveChangeLineRequest(kit.Wbs["B"].Id, EvmBudgetSourceKind.ManagementReserve, 2_000, NewFinish: new DateOnly(2027, 1, 29)),
            new SaveChangeLineRequest(null, EvmBudgetSourceKind.ManagementReserve, 1_000),
        ]));
        Assert.Equal(EvmChangeStatus.Pending, request.Status);
        Assert.Equal("BCR-001", request.DisplayNumber);

        var approved = await kit.Changes.ApproveAsync(request.Id, new DecideChangeRequestRequest("OK"));

        Assert.Equal(EvmChangeStatus.Approved, approved.Status);
        Assert.Equal(1, approved.AppliedRevision);
        Assert.Equal(20_000m, approved.Before!.WorkPackages.Single().Budget);
        Assert.Equal(22_000m, approved.After!.WorkPackages.Single().Budget);
        Assert.Equal((3_000m, 0m), (approved.Before.ManagementReserve, approved.After.ManagementReserve));
        var pmb = (await kit.Evm.GetBaselineAsync(kit.Project.Id))!;
        Assert.Equal(1, pmb.Revision);
        Assert.Equal(6_000m, pmb.UndistributedBudget);
        Assert.Equal(80_000m, pmb.ContractBudgetBase); // reserve moved, the CBB didn't change
        Assert.Equal(new DateOnly(2027, 1, 29), pmb.WorkPackages.Single(w => w.WbsCode == "1001.01.B").PlannedFinish);

        var tooMuch = await kit.Changes.CreateAsync(kit.Project.Id, new CreateChangeRequestRequest("More", "More", [new SaveChangeLineRequest(kit.Wbs["A"].Id, EvmBudgetSourceKind.ManagementReserve, 1)]));
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Changes.ApproveAsync(tooMuch.Id, new DecideChangeRequestRequest(null)));
        var rejected = await kit.Changes.RejectAsync(tooMuch.Id, new DecideChangeRequestRequest("No reserve left"));
        Assert.Equal(EvmChangeStatus.Rejected, rejected.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => kit.Changes.ApproveAsync(tooMuch.Id, new DecideChangeRequestRequest(null)));
        Assert.Equal(2, (await kit.Changes.ListAsync(kit.Project.Id)).Count);
    }

    [Fact]
    public async Task The_series_has_bcws_for_every_period_and_actuals_for_statused_ones()
    {
        var (kit, _) = await BaselineAsync();
        await kit.Evm.ClosePeriodAsync(kit.Project.Id, kit.Periods["2026-10"].Id);

        var series = await kit.Evm.GetSeriesAsync(kit.Project.Id);

        Assert.Equal(["2026-10", "2026-11", "2026-12", "2027-01", "2027-02", "2027-03"], series.Points.Select(p => p.Name));
        Assert.Equal([EvmPeriodState.Closed, EvmPeriodState.Reporting, EvmPeriodState.Future], series.Points.Take(3).Select(p => p.State));
        Assert.Equal(72_000m, series.Points[^1].Bcws);
        Assert.NotNull(series.Points[1].Cpi);
        Assert.Null(series.Points[2].Bcwp);
    }

    [Fact]
    public async Task Exports_are_ipmdar_style_workbooks()
    {
        var (kit, _) = await BaselineAsync();

        var f1 = await kit.Exports.ExportFormat1Async(kit.Project.Id);
        var f5 = await kit.Exports.ExportFormat5Async(kit.Project.Id);

        Assert.Equal("1001-IPMDAR-Format1-2026-11.xlsx", f1.FileName);
        using (var book = new XLWorkbook(new MemoryStream(f1.Content)))
        {
            var ws = book.Worksheet(1);
            Assert.Contains(ws.CellsUsed(), c => c.GetString() == "1001.01.A");
            Assert.Contains(ws.CellsUsed(), c => c.GetString() == "Management reserve");
        }

        using (var book = new XLWorkbook(new MemoryStream(f5.Content)))
        {
            Assert.True(book.TryGetWorksheet("Variance analysis", out var vars));
            Assert.Equal("1001.01.A", vars.Cell(2, 1).GetString());
        }
    }
}

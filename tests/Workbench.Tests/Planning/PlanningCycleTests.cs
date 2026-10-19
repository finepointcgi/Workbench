using Workbench.Application;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Approvals;
using Workbench.Contracts.Planning;
using Workbench.Domain.ProjectAccounting;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Planning;

/// <summary>The real registrations: planning permissions, the "Org unit budget" workflow (Finance by default) and the roll-up.</summary>
public sealed class PlanningCycleTests
{
    private sealed record Setup(SeededHost Host, Guid Ops, Guid Eng, Guid Val, Guid Pat, Dictionary<string, Guid> Accounts, List<FiscalPeriod> Periods)
    {
        public T Get<T>(string email) where T : notnull => Host.As(email).Host.Get<T>();
    }

    private static async Task<Setup> CreateAsync()
    {
        var seeded = await SeededHost.CreateAsync();
        var chart = seeded.As(SeededHost.Finance).Host.Get<IChartOfAccountsService>();
        await chart.InstallDefaultChartAsync();
        var accounts = (await chart.ListAccountsAsync()).ToDictionary(a => a.Number, a => a.Id);
        await using var db = await seeded.Host.Get<IWorkbenchDbContextFactory>().CreateAsync();
        var year = new FiscalYear { Year = 2028, Start = new DateOnly(2027, 10, 1), End = new DateOnly(2028, 9, 30) };
        db.Set<FiscalYear>().Add(year);
        var periods = Enumerable.Range(0, 12).Select(i => new FiscalPeriod
        {
            FiscalYearId = year.Id, Number = i + 1, Name = $"{year.Start.AddMonths(i):yyyy-MM}", Start = year.Start.AddMonths(i), End = year.Start.AddMonths(i + 1).AddDays(-1),
        }).ToList();
        db.Set<FiscalPeriod>().AddRange(periods);
        var ops = new OrgUnit { Code = "OPS", Name = "Operations" };
        var eng = new OrgUnit { Code = "ENG", Name = "Engineering" };
        var val = new Employee { EmployeeNumber = "E1", Email = SeededHost.Viewer, DisplayName = "Val", HireDate = new DateOnly(2020, 1, 1) };
        var pat = new Employee { EmployeeNumber = "E2", Email = SeededHost.Owner, DisplayName = "Pat", HireDate = new DateOnly(2020, 1, 1) };
        db.Set<OrgUnit>().AddRange(ops, eng);
        db.Set<Employee>().AddRange(val, pat);
        await db.SaveChangesAsync();
        return new Setup(seeded, ops.Id, eng.Id, val.Id, pat.Id, accounts, periods);
    }

    private static Task<PlanningCycleDto> OpenAsync(Setup s) =>
        s.Get<IPlanningCycleService>(SeededHost.Finance).OpenCycleAsync(new OpenPlanningCycleRequest(2028, "Annual plan",
            [new CycleAssignmentInput(s.Ops, s.Val), new CycleAssignmentInput(s.Eng, s.Pat)], "Plan for growth.", new DateOnly(2027, 8, 1)));

    [Fact]
    public async Task Approved_submissions_roll_up_and_adopting_locks_the_version()
    {
        var s = await CreateAsync();
        await using var _ = s.Host;
        var cycle = await OpenAsync(s);
        Assert.Equal("FY2028 Annual plan", cycle.VersionName);
        Assert.All(cycle.Submissions, x => Assert.Equal(SubmissionState.NotStarted, x.Status));
        var ops = cycle.Submissions.Single(x => x.OrgUnitCode == "OPS");

        // Val owns OPS: sees it in "my budgets", fills it in and submits it.
        var val = s.Get<IPlanningCycleService>(SeededHost.Viewer);
        Assert.Equal(ops.Id, Assert.Single(await val.ListMineAsync()).Id);
        var mine = await val.SpreadSubmissionAsync(ops.Id, new SpreadSubmissionRequest(s.Accounts["6120"], 12_000m, SpreadMethod.Evenly));
        Assert.True(mine.CanEdit);
        Assert.Equal(SubmissionState.Drafting, mine.Summary.Status);
        mine = await val.SaveSubmissionCellsAsync(ops.Id, new SaveSubmissionCellsRequest([new SubmissionCellInput(s.Accounts["4010"], s.Periods[0].Id, 5000m)]));
        Assert.Equal(17_000m, mine.Summary.Total);
        mine = await val.SubmitAsync(ops.Id);
        Assert.Equal(SubmissionState.Submitted, mine.Summary.Status);
        Assert.False(mine.CanEdit);
        await Assert.ThrowsAsync<InvalidOperationException>(() => val.SaveSubmissionCellsAsync(ops.Id, new SaveSubmissionCellsRequest([])));

        // It waits on Finance in the approvals inbox; the owner can't approve it.
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => val.ApproveAsync(ops.Id, new DecideSubmissionRequest()));
        var inbox = await s.Get<IApprovalInboxService>(SeededHost.Finance).ListMineAsync();
        var item = Assert.Single(inbox, i => i.DocumentType == ApprovalDocumentTypes.BudgetSubmission);
        Assert.Equal(17_000m, item.Amount);
        await s.Get<IApprovalInboxService>(SeededHost.Finance).DecideAsync(ApprovalDocumentTypes.BudgetSubmission, ops.Id, new ApprovalDecisionRequest(ApprovalDecisionKind.Approve, "Looks right"));

        var finance = s.Get<IPlanningCycleService>(SeededHost.Finance);
        var approved = await finance.GetSubmissionAsync(ops.Id);
        Assert.Equal((SubmissionState.Approved, "Looks right"), (approved.Summary.Status, approved.Summary.DecisionComment));
        var grid = await s.Get<IGlBudgetService>(SeededHost.Finance).GetGridAsync(cycle.VersionId);
        Assert.Equal(17_000m, grid.Rows.Sum(r => r.Total));
        Assert.All(grid.Rows, r => Assert.Equal("OPS", r.OrgUnitCode));

        // Not every unit is approved yet.
        await Assert.ThrowsAsync<InvalidOperationException>(() => finance.AdoptAsync(cycle.Id));
        var pat = s.Get<IPlanningCycleService>(SeededHost.Owner);
        var eng = cycle.Submissions.Single(x => x.OrgUnitCode == "ENG");
        await pat.SpreadSubmissionAsync(eng.Id, new SpreadSubmissionRequest(s.Accounts["5010"], 120_000m, SpreadMethod.Evenly));
        await pat.SubmitAsync(eng.Id);
        await s.Get<IPlanningCycleService>(SeededHost.Finance).ApproveAsync(eng.Id, new DecideSubmissionRequest());

        var dashboard = await finance.GetCycleAsync(cycle.Id);
        Assert.True(dashboard.CanAdopt);
        Assert.Equal(2, dashboard.Count(SubmissionState.Approved));
        var adopted = await finance.AdoptAsync(cycle.Id);
        Assert.Equal(PlanningCycleState.Adopted, adopted.Status);

        var budgets = s.Get<IGlBudgetService>(SeededHost.Finance);
        var version = (await budgets.ListVersionsAsync(2028)).Single();
        Assert.Equal((BudgetVersionState.Adopted, 137_000m), (version.Status, version.Expense + version.Revenue));
        await Assert.ThrowsAsync<InvalidOperationException>(() => budgets.SaveCellsAsync(version.Id,
            new SaveBudgetCellsRequest([new BudgetCellInput(s.Accounts["6120"], s.Ops, s.Periods[0].Id, 1m)])));
        await Assert.ThrowsAsync<InvalidOperationException>(() => val.SaveSubmissionCellsAsync(ops.Id, new SaveSubmissionCellsRequest([])));
    }

    [Fact]
    public async Task Returned_budgets_go_back_to_the_owner_and_resubmit()
    {
        var s = await CreateAsync();
        await using var _ = s.Host;
        var cycle = await OpenAsync(s);
        var ops = cycle.Submissions.Single(x => x.OrgUnitCode == "OPS");
        var val = s.Get<IPlanningCycleService>(SeededHost.Viewer);
        await val.SpreadSubmissionAsync(ops.Id, new SpreadSubmissionRequest(s.Accounts["6120"], 12_000m, SpreadMethod.Evenly));
        await val.SubmitAsync(ops.Id);

        var finance = s.Get<IPlanningCycleService>(SeededHost.Finance);
        await Assert.ThrowsAsync<ArgumentException>(() => finance.ReturnAsync(ops.Id, new DecideSubmissionRequest()));
        var returned = await finance.ReturnAsync(ops.Id, new DecideSubmissionRequest("Rent goes up 5% in April."));
        Assert.Equal(SubmissionState.Returned, returned.Summary.Status);

        var back = await s.Get<IPlanningCycleService>(SeededHost.Viewer).GetSubmissionAsync(ops.Id);
        Assert.True(back.CanEdit);
        Assert.Equal("Rent goes up 5% in April.", back.Summary.DecisionComment);
        await val.SaveSubmissionCellsAsync(ops.Id, new SaveSubmissionCellsRequest([new SubmissionCellInput(s.Accounts["6120"], s.Periods[6].Id, 1050m)]));
        var again = await val.SubmitAsync(ops.Id);
        Assert.Equal(SubmissionState.Submitted, again.Summary.Status);

        // Recall pulls it back to drafting.
        Assert.Equal(SubmissionState.Drafting, (await val.RecallAsync(ops.Id)).Summary.Status);
        Assert.DoesNotContain(await s.Get<IApprovalInboxService>(SeededHost.Finance).ListMineAsync(), i => i.DocumentType == ApprovalDocumentTypes.BudgetSubmission);
    }

    [Fact]
    public async Task Owners_see_only_their_own_budget_and_planning_setup_is_for_finance()
    {
        var s = await CreateAsync();
        await using var _ = s.Host;
        var cycle = await OpenAsync(s);
        var ops = cycle.Submissions.Single(x => x.OrgUnitCode == "OPS");

        // Pat owns ENG, not OPS.
        var pat = s.Get<IPlanningCycleService>(SeededHost.Owner);
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pat.GetSubmissionAsync(ops.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pat.SaveSubmissionCellsAsync(ops.Id, new SaveSubmissionCellsRequest([])));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pat.SubmitAsync(ops.Id));
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pat.ListCyclesAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => pat.OpenCycleAsync(new OpenPlanningCycleRequest(2028, "Mine", [])));

        foreach (var email in new[] { SeededHost.Viewer, SeededHost.Owner, SeededHost.Timekeeper })
        {
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => s.Get<IGlBudgetService>(email).ListVersionsAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => s.Get<IRecurringJournalService>(email).ListTemplatesAsync());
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => s.Get<IRecurringJournalService>(email).GenerateDueAsync(new GenerateRecurringRequest()));
            await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => s.Get<IBudgetReportService>(email).GetBudgetedRatesAsync(2028));
        }

        // Admins and Finance can.
        Assert.NotEmpty(await s.Get<IGlBudgetService>(SeededHost.Admin).ListVersionsAsync());
        Assert.Empty(await s.Get<IRecurringJournalService>(SeededHost.Finance).ListTemplatesAsync());
        Assert.NotNull(await s.Get<IPlanningCycleService>(SeededHost.Admin).GetSubmissionAsync(ops.Id));

        // Opening a second cycle for the same year is refused.
        await Assert.ThrowsAsync<ArgumentException>(() => OpenAsync(s));
    }
}

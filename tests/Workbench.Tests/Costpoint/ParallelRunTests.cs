using System.Text;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Application.Migration;
using Workbench.Tests.Imports;
using Workbench.Tests.Migration;

namespace Workbench.Tests.Costpoint;

/// <summary>The Costpoint parallel run: upload the two reports, see differences with a likely cause in plain words.</summary>
public sealed class ParallelRunTests
{
    private static async Task<(ImportHost Host, FiscalPeriodDto Period)> CutoverAsync()
    {
        var host = await ImportHost.CreateAsync();
        await MigrationImportTests.ImportCutoverSamplesAsync(host);
        var period = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= MigrationImportTests.OpeningDate && MigrationImportTests.OpeningDate <= p.End);
        return (host, period);
    }

    private static byte[] Sample(string file, Func<string, string>? edit = null)
    {
        var text = File.ReadAllText(Samples.PathOf($"samples/reconcile/{file}"));
        return Encoding.UTF8.GetBytes(edit is null ? text : edit(text));
    }

    [Fact]
    public async Task Waiting_then_partly_uploaded_then_matched_trial_balance_in_plain_words()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var service = host.Get<IParallelRunService>();

        var empty = await service.GetAsync(period.Id);
        Assert.Equal(ParallelState.WaitingForCostpoint, empty.State);
        Assert.Contains("Waiting for Costpoint", empty.Verdict);
        Assert.Equal(2, empty.Reports.Count);

        var uploaded = await service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.TrialBalance, "tb.csv", Sample("trial-balance.csv")));
        Assert.Equal(ParallelState.PartlyUploaded, uploaded.State);
        Assert.Contains("Project cost by element is still to upload", uploaded.Verdict);
        var tb = uploaded.Reports.Single(r => r.Kind == ReconReportKind.TrialBalance);
        Assert.Equal(ReconRunOutcome.Matched, tb.Latest!.Outcome);
        Assert.Empty(tb.Causes);
        Assert.Single(await host.Get<IReconciliationService>().ListMappingsAsync(ReconReportKind.TrialBalance));

        // Next month the saved mapping is reused for an upload with the same columns.
        await service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.TrialBalance, "tb2.csv", Sample("trial-balance.csv")));
        Assert.Single(await host.Get<IReconciliationService>().ListMappingsAsync(ReconReportKind.TrialBalance));
    }

    [Fact]
    public async Task Differences_get_a_likely_cause_rounding_draft_entry_and_one_entry_the_size_of_the_gap()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var accounts = (await host.Get<IChartOfAccountsService>().ListAccountsAsync()).ToDictionary(a => a.Number);
        var journal = host.Get<IJournalService>();
        var date = new DateOnly(2026, 10, 20);

        // A draft that, once posted, closes Costpoint's extra 1,500 of direct labor; a posted 700 entry Costpoint doesn't have.
        await journal.CreateDraftAsync(new SaveJournalEntryRequest(date, "Accrue contractor labor",
            [new JournalLineInput(accounts["5010"].Id, 1500m, 0m), new JournalLineInput(accounts["2100"].Id, 0m, 1500m)]));
        await journal.CreateAndPostAsync(new SaveJournalEntryRequest(date, "Office supplies order",
            [new JournalLineInput(accounts["6130"].Id, 700m, 0m), new JournalLineInput(accounts["1010"].Id, 0m, 700m)]));

        var costpoint = Sample("trial-balance.csv", t => t
            .Replace("5010,Direct labor,73040.00,", "5010,Direct labor,74540.00,")
            .Replace("2100,Accrued payroll,,61880.00", "2100,Accrued payroll,,63380.00")
            .Replace("1590,Accumulated depreciation,,12225.00", "1590,Accumulated depreciation,,12225.40"));
        var run = await host.Get<IParallelRunService>().UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.TrialBalance, "tb.csv", costpoint));

        Assert.Equal(ParallelState.PartlyUploaded, run.State);
        var causes = run.Reports.Single(r => r.Kind == ReconReportKind.TrialBalance).Causes.ToDictionary(c => c.Key);
        Assert.Equal(ParallelCauseKind.Rounding, causes["1590"].Kind);
        Assert.Equal(ParallelCauseKind.Unposted, causes["5010"].Kind);
        Assert.Contains("draft entry", causes["5010"].Summary);
        Assert.Contains("$1,500.00", causes["5010"].Summary);
        Assert.Equal(ParallelCauseKind.SingleEntry, causes["6130"].Kind);
        Assert.Contains("Office supplies order", causes["6130"].Summary);
        Assert.Equal(1, run.Health.DraftJournalEntries);
        Assert.False(run.Health.IsClean);

        // The same causes are available for any saved run.
        var runId = run.Reports.Single(r => r.Kind == ReconReportKind.TrialBalance).Latest!.Id;
        Assert.Equal(causes.Count, (await host.Get<IParallelRunService>().ExplainAsync(runId)).Count);
    }

    [Fact]
    public async Task Project_cost_only_in_costpoint_is_explained_and_months_are_listed()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var service = host.Get<IParallelRunService>();
        await service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.TrialBalance, "tb.csv", Sample("trial-balance.csv")));
        var run = await service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.ProjectCost, "pc.csv", Sample("project-cost.csv")));

        Assert.Equal(ParallelState.Differences, run.State);
        var pc = run.Reports.Single(r => r.Kind == ReconReportKind.ProjectCost);
        Assert.NotEmpty(pc.Causes);
        Assert.All(pc.Causes, c => Assert.NotEqual(ParallelCauseKind.Other, c.Kind));
        Assert.Contains("difference", run.Verdict);
        var month = Assert.Single(run.Months);
        Assert.Equal((period.Id, ParallelState.Differences), (month.PeriodId, month.State));
    }

    [Fact]
    public async Task A_report_with_unreadable_columns_says_so_and_points_to_manual_mapping_and_employees_are_refused()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var service = host.Get<IParallelRunService>();
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.TrialBalance, "x.csv",
            Encoding.UTF8.GetBytes("Foo,Bar\n1,2\n"))));
        Assert.Contains("match its columns by hand", ex.Message);
        await Assert.ThrowsAsync<ArgumentException>(() => service.UploadAsync(new UploadParallelReportRequest(period.Id, ReconReportKind.ArAging, "x.csv", Sample("ar-aging.csv"))));

        host.User.Email = ImportHost.Employee;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => service.GetAsync(period.Id));
    }

    // ---- The rules, without a database ----

    private static ReconRowDto Row(string key, ReconRowStatus status, params (string Field, decimal Costpoint, decimal Workbench)[] values) =>
        new(key, [key], null, status, values.Select(v => new ReconValueDto(v.Field, v.Costpoint, v.Workbench, Math.Abs(v.Workbench - v.Costpoint) < 0.005m)).ToList(), []);

    private static readonly ParallelHealthDto Clean = new(0, 0, 0, false);

    [Fact]
    public void A_difference_that_was_there_at_the_start_of_the_month_is_carried_over()
    {
        var row = Row("1010", ReconRowStatus.Different, ("Ending", 1000m, 1250m), ("Opening", 500m, 750m));
        var cause = ParallelRunExplainer.ExplainTrialBalance(row, new AccountFacts(true, 3, 0, 0, null, 0, 0, null, 0, 0, null, []));
        Assert.Equal(ParallelCauseKind.CarriedOver, cause.Kind);
        Assert.Contains("already differed by $250.00 at the start of the month", cause.Summary);
    }

    [Fact]
    public void Entries_posted_after_the_month_end_or_dated_just_after_it_are_timing()
    {
        var late = ParallelRunExplainer.ExplainTrialBalance(Row("6120", ReconRowStatus.Different, ("Ending", 12_500m, 12_950m)),
            new AccountFacts(true, 2, 0, 0, null, 1, 450m, "JE-000007 Rent true-up", 0, 0, null, []));
        Assert.Equal(ParallelCauseKind.Timing, late.Kind);
        Assert.Contains("posted in Workbench after the month ended", late.Summary);

        var early = ParallelRunExplainer.ExplainTrialBalance(Row("6120", ReconRowStatus.Different, ("Ending", 12_500m, 12_050m)),
            new AccountFacts(true, 2, 0, 0, null, 0, 0, null, 1, 450m, "JE-000008 Rent", []));
        Assert.Equal(ParallelCauseKind.Timing, early.Kind);
        Assert.Contains("dated just after the month end", early.Summary);
    }

    [Fact]
    public void Accounts_missing_on_either_side_and_unexplained_differences_say_what_to_check()
    {
        var missingAccount = ParallelRunExplainer.ExplainTrialBalance(Row("9999", ReconRowStatus.OnlyInCostpoint, ("Ending", 50m, 0m)), AccountFacts.None);
        Assert.Equal(ParallelCauseKind.MissingInWorkbench, missingAccount.Kind);
        Assert.Contains("not in Workbench's chart of accounts", missingAccount.Summary);

        var onlyHere = ParallelRunExplainer.ExplainTrialBalance(Row("1020", ReconRowStatus.OnlyInWorkbench, ("Ending", 0m, 50m)), AccountFacts.None);
        Assert.Equal(ParallelCauseKind.MissingInCostpoint, onlyHere.Kind);

        var unknown = ParallelRunExplainer.ExplainTrialBalance(Row("5110", ReconRowStatus.Different, ("Ending", 100m, 137.13m)),
            new AccountFacts(true, 1, 0, 0, null, 0, 0, null, 0, 0, null, [(100m, "JE-1", "Opening")]));
        Assert.Equal(ParallelCauseKind.Other, unknown.Kind);
    }

    [Fact]
    public void Burden_applied_at_a_different_rate_shows_both_rates()
    {
        var row = Row("2001", ReconRowStatus.Different, ("DirectLabor", 10_000m, 10_000m), ("Fringe", 3_120m, 3_100m), ("Overhead", 5_000m, 5_000m));
        var cause = ParallelRunExplainer.ExplainProjectCost(row, Clean);
        Assert.Equal(ParallelCauseKind.RateDifference, cause.Kind);
        Assert.Contains("fringe: Costpoint works out to 31.20%, Workbench to 31.00%", cause.Summary);
        Assert.Equal(-20m, cause.Difference);
    }

    [Fact]
    public void Lower_labor_with_unapproved_timesheets_points_at_them_and_otherwise_at_hours_and_rates()
    {
        var row = Row("2001", ReconRowStatus.Different, ("DirectLabor", 10_000m, 9_200m));
        var pending = ParallelRunExplainer.ExplainProjectCost(row, new ParallelHealthDto(0, 3, 0, false));
        Assert.Equal(ParallelCauseKind.Unposted, pending.Kind);
        Assert.Contains("3 timesheets in this month that aren't approved", pending.Summary);
        var hours = ParallelRunExplainer.ExplainProjectCost(row, Clean);
        Assert.Equal(ParallelCauseKind.Hours, hours.Kind);

        var odc = ParallelRunExplainer.ExplainProjectCost(Row("2001", ReconRowStatus.Different, ("DirectOdc", 3_000m, 2_000m)), new ParallelHealthDto(0, 0, 2, false));
        Assert.Equal(ParallelCauseKind.Unposted, odc.Kind);
        Assert.Contains("2 vendor bills dated in this month that aren't posted yet", odc.Summary);
    }
}

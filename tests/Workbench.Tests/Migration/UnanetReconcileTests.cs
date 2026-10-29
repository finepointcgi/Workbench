using Workbench.Contracts.Imports;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Imports;

namespace Workbench.Tests.Migration;

/// <summary>
/// The Unanet parallel-run reconcile: the sample Unanet month reports against Workbench after the sample Unanet import,
/// with the planted differences explained in plain words, and the Costpoint evidence board left alone.
/// </summary>
public sealed class UnanetReconcileTests
{
    private static async Task<(ImportHost Host, FiscalPeriodDto Period)> AfterImportAsync()
    {
        var host = await ImportHost.CreateAsync();
        await UnanetImportTests.ImportAllAsync(host);
        var calendar = host.Get<IFiscalCalendarService>();
        await calendar.CreateFiscalYearAsync(2026, new DateOnly(2025, 10, 1));
        var september = (await calendar.ListPeriodsAsync()).Single(p => p.Start == new DateOnly(2026, 9, 1));
        return (host, september);
    }

    private static async Task<ReconRunDetailDto> RunAsync(ImportHost host, FiscalPeriodDto period, ReconReportKind kind, string file)
    {
        var service = host.Get<IReconciliationService>();
        var bytes = File.ReadAllBytes(Samples.PathOf($"samples/reconcile/unanet/{file}"));
        var preview = await service.PreviewFileAsync(new PreviewReconFileRequest(kind, file, bytes));
        return await service.RunAsync(new RunReconciliationRequest(kind, period.Id, file, bytes, null, preview.SuggestedMapping, SaveMappingAs: preview.SuggestedMapping.Name));
    }

    [Fact]
    public async Task Timesheet_totals_are_compared_by_person_and_project_with_a_plain_words_cause_for_each_difference()
    {
        var (host, period) = await AfterImportAsync();
        await using var _ = host;

        var run = await RunAsync(host, period, ReconReportKind.UnanetTimesheets, "timesheet-totals.csv");
        Assert.Equal(0, run.Run.Problems);
        Assert.Equal(ReconRunOutcome.Differences, run.Run.Outcome);

        ReconRowDto Row(string employee, string project) => run.Rows.Single(r => r.KeyParts[0] == employee && r.KeyParts[1] == project);
        string Cause(string employee, string project) => Row(employee, project).Cause ?? "";

        Assert.Equal(ReconRowStatus.Matched, Row("H1002", "HV-2201").Status);
        Assert.Equal(ReconRowStatus.Matched, Row("H1001", "HV-3305").Status);
        Assert.All(run.Rows.Where(r => r.Status == ReconRowStatus.Matched), r => Assert.Null(r.Cause));

        var extraDay = Row("H1003", "HV-2201");
        Assert.Equal(ReconRowStatus.Different, extraDay.Status);
        Assert.Contains("Unanet has 8 more hours", extraDay.Cause);

        var rate = Row("H1004", "HV-2201");
        Assert.Equal(ReconRowStatus.Different, rate.Status);
        Assert.Contains("Hours match", rate.Cause);
        Assert.Contains("cost rate", rate.Cause);

        Assert.Equal(ReconRowStatus.OnlyInCostpoint, Row("H1007", "HV-2201").Status);
        Assert.Contains("isn't in Workbench", Cause("H1007", "HV-2201"));
        Assert.Contains("Project HV-9999 isn't in Workbench", Cause("H1005", "HV-9999"));
        Assert.Equal(ReconRowStatus.OnlyInWorkbench, Row("H1006", "HV-LV").Status);
        Assert.Contains("Workbench has 8", Cause("H1006", "HV-LV"));

        var drill = await host.Get<IReconciliationService>().DrillDownAsync(run.Run.Id, extraDay.Key);
        Assert.Equal(168m, drill.WorkbenchRows.Sum(r => decimal.Parse(r[4], System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Single(drill.CostpointRows);
    }

    [Fact]
    public async Task Project_cost_is_compared_by_project_and_the_run_can_be_signed_off()
    {
        var (host, period) = await AfterImportAsync();
        await using var _ = host;

        var run = await RunAsync(host, period, ReconReportKind.UnanetProjectCost, "project-cost.csv");
        Assert.Equal(ReconRunOutcome.Differences, run.Run.Outcome);
        var fleet = run.Rows.Single(r => r.KeyParts[0] == "HV-2201");
        Assert.Equal(ReconRowStatus.Different, fleet.Status);
        Assert.Contains("hours", fleet.Cause);
        Assert.Equal(ReconRowStatus.Matched, run.Rows.Single(r => r.KeyParts[0] == "HV-3305").Status);
        Assert.Contains("isn't in Workbench", run.Rows.Single(r => r.KeyParts[0] == "HV-9999").Cause);

        var service = host.Get<IReconciliationService>();
        await Assert.ThrowsAsync<ArgumentException>(() => service.SignOffAsync(run.Run.Id, new SignOffReconciliationRequest(null)));
        var signed = await service.SignOffAsync(run.Run.Id, new SignOffReconciliationRequest("Planted sample differences, explained."));
        Assert.True(signed.IsSignedOff);
        var export = await service.ExportAsync(run.Run.Id);
        Assert.True(export.Content.Length > 1000);
    }

    [Fact]
    public async Task Unanet_reconciliations_do_not_change_the_costpoint_evidence_board()
    {
        var (host, period) = await AfterImportAsync();
        await using var _ = host;

        await RunAsync(host, period, ReconReportKind.UnanetTimesheets, "timesheet-totals.csv");
        var service = host.Get<IReconciliationService>();
        Assert.Equal(7, (await service.GetStatusAsync(period.Id)).Count);
        Assert.Equal(2, (await service.ListReportsAsync()).Count(r => r.Source == "Unanet"));
        Assert.Equal(7, (await service.ListReportsAsync()).Count(r => r.Source == "Costpoint"));
    }

    [Fact]
    public async Task A_timesheet_period_that_crosses_month_end_is_named_as_the_likely_cause()
    {
        await using var host = await ImportHost.CreateAsync();
        foreach (var (kind, file) in new[]
        {
            (ImportKind.OrgUnits, "organizations.csv"), (ImportKind.LaborCategories, "labor-categories.csv"), (ImportKind.Employees, "people.csv"),
            (ImportKind.Customers, "customers.csv"), (ImportKind.Contracts, "projects.csv"), (ImportKind.Projects, "projects.csv"),
        })
        {
            await host.ImportAsync(ImportSource.Unanet, kind, [Samples.File($"samples/import/unanet/{file}")]);
        }

        // Monday 31 August 4h and Tuesday 1 September 8h are one weekly timesheet: Unanet's September report counts all 12 hours, Workbench 8.
        await host.ImportAsync(ImportSource.Unanet, ImportKind.Timesheets, [Samples.Csv("time.csv",
            "Employee ID,Project Code,Task Code,Work Date,Hours,Timesheet Status\nH1003,HV-2201,220,08/31/2026,4,Approved\nH1003,HV-2201,220,09/01/2026,8,Approved\n")]);
        var calendar = host.Get<IFiscalCalendarService>();
        await calendar.CreateFiscalYearAsync(2026, new DateOnly(2025, 10, 1));
        var september = (await calendar.ListPeriodsAsync()).Single(p => p.Start == new DateOnly(2026, 9, 1));

        var report = "Employee ID,Person,Project Code,Total Hours\nH1003,Priya Nair,HV-2201,12\n";
        var service = host.Get<IReconciliationService>();
        var bytes = System.Text.Encoding.UTF8.GetBytes(report);
        var preview = await service.PreviewFileAsync(new PreviewReconFileRequest(ReconReportKind.UnanetTimesheets, "t.csv", bytes));
        var run = await service.RunAsync(new RunReconciliationRequest(ReconReportKind.UnanetTimesheets, september.Id, "t.csv", bytes, null, preview.SuggestedMapping));

        var row = Assert.Single(run.Rows);
        Assert.Equal(ReconRowStatus.Different, row.Status);
        Assert.Contains("crosses the month end", row.Cause);
        Assert.Contains("Unanet has 4 more hours", row.Cause);
    }
}

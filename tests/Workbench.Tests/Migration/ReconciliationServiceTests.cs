using System.Text;
using ClosedXML.Excel;
using Workbench.Contracts;
using Workbench.Contracts.Imports;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Imports;

namespace Workbench.Tests.Migration;

/// <summary>
/// Reconciling the sample Costpoint reports against Workbench after the sample cut-over imports: runs are saved with
/// their mapping, differences drill down to ledger lines and file rows, sign-off is evidence, and the export opens.
/// </summary>
public sealed class ReconciliationServiceTests
{
    private static async Task<(ImportHost Host, FiscalPeriodDto Period)> CutoverAsync()
    {
        var host = await ImportHost.CreateAsync();
        await MigrationImportTests.ImportCutoverSamplesAsync(host);
        var period = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= MigrationImportTests.OpeningDate && MigrationImportTests.OpeningDate <= p.End);
        return (host, period);
    }

    private static async Task<ReconRunDetailDto> RunSampleAsync(ImportHost host, FiscalPeriodDto period, ReconReportKind kind, string file, byte[]? content = null)
    {
        var service = host.Get<IReconciliationService>();
        var bytes = content ?? File.ReadAllBytes(Samples.PathOf($"samples/reconcile/{file}"));
        var preview = await service.PreviewFileAsync(new PreviewReconFileRequest(kind, file, bytes));
        return await service.RunAsync(new RunReconciliationRequest(kind, period.Id, file, bytes, preview.SuggestedMapping.Id,
            preview.SuggestedMapping.Id is null ? preview.SuggestedMapping : null, SaveMappingAs: preview.SuggestedMapping.Id is null ? preview.SuggestedMapping.Name : null));
    }

    [Fact]
    public async Task Sample_reports_match_workbench_after_the_cutover_imports()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;

        foreach (var (kind, file) in new[]
        {
            (ReconReportKind.TrialBalance, "trial-balance.csv"),
            (ReconReportKind.ArAging, "ar-aging.csv"),
            (ReconReportKind.ApAging, "ap-aging.csv"),
            (ReconReportKind.RevenueByProject, "revenue-by-project.csv"),
        })
        {
            var run = await RunSampleAsync(host, period, kind, file);
            Assert.True(run.Run.Outcome == ReconRunOutcome.Matched,
                $"{kind}: {string.Join("; ", run.Rows.Where(r => r.Status != ReconRowStatus.Matched).Select(r => $"{r.Key} {r.Status} {string.Join(",", r.Values.Select(v => $"{v.Field} {v.Costpoint}/{v.Workbench}"))}"))} {string.Join("; ", run.ProblemRows.Select(p => p.Message))}");
            Assert.Equal(0, run.Run.Problems);
        }

        var status = await host.Get<IReconciliationService>().GetStatusAsync(period.Id);
        Assert.Equal(7, status.Count);
        Assert.Equal(4, status.Count(s => s.LatestRun is not null));
        Assert.Equal(186_420.55m, status.Single(s => s.Kind == ReconReportKind.ArAging).LatestRun!.WorkbenchTotal);
        Assert.Equal(4, (await host.Get<IReconciliationService>().ListMappingsAsync()).Count);
    }

    [Fact]
    public async Task Differences_drill_down_sign_off_with_a_note_and_export_to_xlsx()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var service = host.Get<IReconciliationService>();

        // Costpoint shows a cent more cash and a payroll clearing account Workbench doesn't have.
        var sample = await File.ReadAllTextAsync(Samples.PathOf("samples/reconcile/trial-balance.csv"));
        var changed = sample.Replace("1010,Cash - operating,412350.17,", "1010,Cash - operating,412350.18,").Replace("Total,", "1020,Cash - payroll,50.00,\nTotal,");
        var run = await RunSampleAsync(host, period, ReconReportKind.TrialBalance, "trial-balance.csv", Encoding.UTF8.GetBytes(changed));
        Assert.Equal(ReconRunOutcome.Differences, run.Run.Outcome);
        Assert.Equal((1, 1, 0), (run.Run.Different, run.Run.OnlyInCostpoint, run.Run.OnlyInWorkbench));
        var cash = run.Rows.Single(r => r.Key == "1010");
        Assert.Equal((-0.01m, false), (cash.Values.Single().Difference, cash.Values.Single().WithinTolerance));

        var drill = await service.DrillDownAsync(run.Run.Id, "1010");
        var line = Assert.Single(drill.WorkbenchRows);
        Assert.Contains("412350.17", line);
        Assert.True(line[7].Contains("Opening balances 2026-10-31 from Costpoint"), string.Join(" | ", line));
        Assert.Equal(["Row", "Account", "Account Description", "Debit Balance", "Credit Balance"], drill.CostpointColumns);
        Assert.Equal("412350.18", Assert.Single(drill.CostpointRows)[3]);
        var payroll = await service.DrillDownAsync(run.Run.Id, "1020");
        Assert.Empty(payroll.WorkbenchRows);

        // A run with differences needs a note; once signed it's evidence and can't be signed again.
        await Assert.ThrowsAsync<ArgumentException>(() => service.SignOffAsync(run.Run.Id, new SignOffReconciliationRequest(" ")));
        var signed = await service.SignOffAsync(run.Run.Id, new SignOffReconciliationRequest("Cash cent is a Costpoint rounding entry; 1020 opens in November."));
        Assert.Equal(ImportHost.Finance, signed.SignedOffBy);
        Assert.NotNull(signed.SignedOffAtUtc);
        Assert.Contains("was signed off by", (await Assert.ThrowsAsync<ArgumentException>(() => service.SignOffAsync(run.Run.Id, new SignOffReconciliationRequest("again")))).Message);
        var status = (await service.GetStatusAsync(period.Id)).Single(s => s.Kind == ReconReportKind.TrialBalance);
        Assert.Equal(run.Run.Id, status.LatestSignedOff!.Id);
        var audit = await host.Get<IAuditLogService>().ListAsync("ReconciliationRun", run.Run.Id);
        Assert.Contains(audit, a => a.Action == "SignedOff");

        var export = await service.ExportAsync(run.Run.Id);
        Assert.EndsWith(".xlsx", export.FileName);
        using var workbook = new XLWorkbook(new MemoryStream(export.Content));
        var differences = workbook.Worksheet("Differences");
        Assert.Equal("Account", differences.Cell(1, 1).GetString());
        Assert.Equal(["1010", "1020"], new[] { differences.Cell(2, 1).GetString(), differences.Cell(3, 1).GetString() });
        Assert.Equal("Only in Costpoint", differences.Cell(3, 3).GetString());
        Assert.Equal(-0.01m, (decimal)differences.Cell(2, 6).GetDouble(), 2);
        Assert.Equal(24, workbook.Worksheet("All rows").LastRowUsed()!.RowNumber());
        Assert.Contains(workbook.Worksheet("Summary").CellsUsed(), c => c.GetString().Contains("Cash cent is a Costpoint rounding entry"));
    }

    [Fact]
    public async Task A_saved_mapping_is_reused_for_the_next_file_and_runs_are_kept_per_period()
    {
        var (host, period) = await CutoverAsync();
        await using var _ = host;
        var service = host.Get<IReconciliationService>();
        var bytes = File.ReadAllBytes(Samples.PathOf("samples/reconcile/revenue-by-project.csv"));
        var first = await service.PreviewFileAsync(new PreviewReconFileRequest(ReconReportKind.RevenueByProject, "rev.csv", bytes));
        Assert.Null(first.SuggestedMapping.Id);
        var saved = await service.SaveMappingAsync(first.SuggestedMapping with { Name = "Costpoint revenue (monthly)", Tolerances = [new ReconToleranceDto("Revenue", 1m)] });

        var second = await service.PreviewFileAsync(new PreviewReconFileRequest(ReconReportKind.RevenueByProject, "rev-nov.csv", bytes));
        Assert.Equal(saved.Id, second.SuggestedMapping.Id);
        Assert.Equal(1m, second.SuggestedMapping.Tolerances.Single().Absolute);

        var run = await service.RunAsync(new RunReconciliationRequest(ReconReportKind.RevenueByProject, period.Id, "rev-nov.csv", bytes, saved.Id));
        Assert.Equal(("Costpoint revenue (monthly)", ReconRunOutcome.Matched), (run.Run.MappingName, run.Run.Outcome));
        Assert.Single(await service.ListRunsAsync(period.Id));
        var other = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).First(p => p.Id != period.Id);
        Assert.Empty(await service.ListRunsAsync(other.Id));

        await service.DeleteMappingAsync(saved.Id!.Value);
        Assert.Empty(await service.ListMappingsAsync(ReconReportKind.RevenueByProject));
        Assert.Equal("Costpoint revenue (monthly)", (await service.GetRunAsync(run.Run.Id)).Run.MappingName);
    }

    [Fact]
    public async Task The_checklist_follows_the_import_history_and_prerequisites()
    {
        await using var host = await ImportHost.CreateAsync();
        var migration = host.Get<IMigrationService>();
        var empty = await migration.GetChecklistAsync();
        Assert.Equal(["accounts", "orgs", "people", "customers", "contracts", "projects", "indirect", "open-items", "opening-balances", "fixed-assets", "smartsheet", "parallel"],
            empty.Steps.Select(s => s.Key));
        Assert.Equal(MigrationStepStatus.NotStarted, empty.Steps.Single(s => s.Key == "accounts").Status);
        var people = empty.Steps.Single(s => s.Key == "people");
        Assert.Equal(MigrationStepStatus.Blocked, people.Status);
        Assert.Contains("Organizations", people.BlockedBy);
        Assert.Equal("setup/import?source=Costpoint&kind=Employees", people.Imports.Single(i => i.Kind == "Employees").WizardLink);
        Assert.Contains(empty.Steps.Single(s => s.Key == "opening-balances").BlockedBy, b => b == "Cut-over date set");
        Assert.Contains(empty.Readiness, c => c.Title == "Cut-over date" && !c.Passed);

        await MigrationImportTests.ImportCutoverSamplesAsync(host);
        var november = new DateOnly(2026, 11, 15);
        var period = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= november && november <= p.End);
        await migration.SaveSettingsAsync(new SaveMigrationSettingsRequest(MigrationImportTests.OpeningDate.AddDays(1), period.Id));
        var done = await migration.GetChecklistAsync();
        foreach (var key in new[] { "accounts", "orgs", "people", "customers", "contracts", "projects", "open-items", "opening-balances" })
        {
            Assert.True(done.Steps.Single(s => s.Key == key).Status == MigrationStepStatus.Done, $"{key}: {done.Steps.Single(s => s.Key == key).Status}");
        }

        Assert.Equal(ImportJobStatus.Imported, done.Steps.Single(s => s.Key == "accounts").Imports.Single().LastJobStatus);
        Assert.All(done.Readiness, c => Assert.True(c.Passed, $"{c.Title}: {c.Detail}"));
        Assert.Equal(MigrationStepStatus.NotStarted, done.Steps.Single(s => s.Key == "parallel").Status);

        // Steps with nothing to import here can be marked by hand (skipping needs a reason).
        await Assert.ThrowsAsync<ArgumentException>(() => migration.SetStepAsync("smartsheet", new SetMigrationStepRequest(MigrationStepState.Skipped, null)));
        var skipped = await migration.SetStepAsync("smartsheet", new SetMigrationStepRequest(MigrationStepState.Skipped, "Plans stay in Smartsheet until December."));
        var smartsheet = skipped.Steps.Single(s => s.Key == "smartsheet");
        Assert.Equal((MigrationStepStatus.Skipped, ImportHost.Finance), (smartsheet.Status, smartsheet.ManualBy));
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => migration.SetStepAsync("nope", new SetMigrationStepRequest(MigrationStepState.Done, null)));

        // A parallel month ending before the cut-over is refused.
        var october = (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Single(p => p.Start <= MigrationImportTests.OpeningDate && MigrationImportTests.OpeningDate <= p.End);
        await Assert.ThrowsAsync<ArgumentException>(() => migration.SaveSettingsAsync(new SaveMigrationSettingsRequest(MigrationImportTests.OpeningDate.AddDays(1), october.Id)));

        // A run starts the parallel month (November's trial balance carries the opening balances); signing off every report finishes it.
        var run = await RunSampleAsync(host, period, ReconReportKind.TrialBalance, "trial-balance.csv");
        Assert.Equal(MigrationStepStatus.InProgress, (await migration.GetChecklistAsync()).Steps.Single(s => s.Key == "parallel").Status);
        await host.Get<IReconciliationService>().SignOffAsync(run.Run.Id, new SignOffReconciliationRequest(null));
        Assert.Equal(MigrationStepStatus.InProgress, (await migration.GetChecklistAsync()).Steps.Single(s => s.Key == "parallel").Status);
    }

    [Fact]
    public async Task Only_finance_and_admins_reconcile_or_change_the_checklist()
    {
        await using var host = await ImportHost.CreateAsync();
        host.User.Email = ImportHost.Employee;
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IMigrationService>().GetChecklistAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<IReconciliationService>().ListRunsAsync());
        await Assert.ThrowsAsync<WorkbenchForbiddenException>(() => host.Get<Workbench.Contracts.Payroll.IPayrollLayoutService>().GetAsync("paylocity"));
    }
}

using Workbench.Contracts.Accounting;
using Workbench.Contracts.Billing;
using Workbench.Contracts.IndirectRates;
using Workbench.Contracts.Imports;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Tests.Imports;
using ImportJobStatus = Workbench.Contracts.Imports.ImportJobStatus;
using ImportKind = Workbench.Contracts.Imports.ImportKind;
using ImportSource = Workbench.Contracts.Imports.ImportSource;

namespace Workbench.Tests.Costpoint;

/// <summary>The Costpoint conversion gaps: fiscal calendar, indirect rates and indirect pool structure.</summary>
public sealed class CostpointGapImportTests
{
    private const string Calendar28 = """
        FY_CD,PD_NO,PD_START_DT,PD_END_DT
        2028,1,10/01/2027,10/31/2027
        2028,2,11/01/2027,11/30/2027
        2028,3,12/01/2027,12/31/2027
        2028,4,01/01/2028,01/31/2028
        2028,5,02/01/2028,02/29/2028
        2028,6,03/01/2028,03/31/2028
        2028,7,04/01/2028,04/30/2028
        2028,8,05/01/2028,05/31/2028
        2028,9,06/01/2028,06/30/2028
        2028,10,07/01/2028,07/31/2028
        2028,11,08/01/2028,08/31/2028
        2028,12,09/01/2028,09/15/2028
        2028,13,09/16/2028,09/30/2028
        """;

    private static Task<ImportJobDetailDto> Prepare(ImportHost host, ImportKind kind, string name, string csv) =>
        host.PrepareAsync(ImportSource.Costpoint, kind, [Samples.Csv(name, csv)]);

    [Fact]
    public async Task Fiscal_calendar_sample_matches_the_year_workbench_already_has_and_changes_nothing()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportCostpointAsync(ImportKind.FiscalCalendar, "fiscal-calendar.csv");
        Assert.Equal(ImportJobStatus.Imported, job.Job.Status);
        Assert.Equal((0, 0, 1), (job.Job.Counts.Creates, job.Job.Counts.Errors, job.Job.Counts.Skips));
    }

    [Fact]
    public async Task Fiscal_calendar_creates_a_year_with_an_adjusting_period_and_rolls_back()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await Prepare(host, ImportKind.FiscalCalendar, "cal.csv", Calendar28);
        Assert.Equal((14, 0), (dry.Job.Counts.Creates, dry.Job.Counts.Errors));
        Assert.Equal(12, (await host.Get<IFiscalCalendarService>().ListPeriodsAsync()).Count);

        var done = await host.Imports.ImportAsync(dry.Job.Id);
        Assert.Equal(ImportJobStatus.Imported, done.Job.Status);
        var periods = await host.Get<IFiscalCalendarService>().ListPeriodsAsync(2028);
        Assert.Equal(13, periods.Count);
        Assert.Equal(new DateOnly(2028, 9, 30), periods[^1].End);
        Assert.Equal(periods.Count, periods.Select(p => p.Name).Distinct().Count());
        Assert.Equal("2028-09-P13", periods[^1].Name);

        await host.Imports.RollBackAsync(done.Job.Id);
        Assert.Empty(await host.Get<IFiscalCalendarService>().ListPeriodsAsync(2028));
    }

    [Fact]
    public async Task Fiscal_calendar_rejects_gaps_and_a_year_that_disagrees_with_workbench_in_plain_words()
    {
        await using var host = await ImportHost.CreateAsync();
        var gap = await Prepare(host, ImportKind.FiscalCalendar, "gap.csv", "FY_CD,PD_NO,PD_START_DT,PD_END_DT\n2028,1,10/01/2027,10/31/2027\n2028,2,11/05/2027,11/30/2027\n");
        Assert.Contains(gap.Errors, e => e.Message.Contains("gap between period 1", StringComparison.Ordinal));

        var clash = await Prepare(host, ImportKind.FiscalCalendar, "clash.csv", "FY_CD,PD_NO,PD_START_DT,PD_END_DT\n2027,1,10/01/2026,10/31/2026\n2027,2,11/01/2026,11/30/2026\n");
        Assert.Contains(clash.Errors, e => e.Message.Contains("already in Workbench with different periods", StringComparison.Ordinal));

        var overlap = await Prepare(host, ImportKind.FiscalCalendar, "ov.csv", "FY_CD,PD_NO,PD_START_DT,PD_END_DT\n2030,1,03/01/2027,03/31/2027\n");
        Assert.Contains(overlap.Errors, e => e.Message.Contains("overlaps period", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Indirect_rates_import_as_percentages_supersede_changes_and_roll_back()
    {
        await using var host = await ImportHost.CreateAsync();
        var job = await host.ImportCostpointAsync(ImportKind.IndirectRates, "indirect-rates.csv");
        Assert.True(job.Job.Counts.Errors == 0, string.Join("; ", job.Errors.Select(e => e.Message)));
        Assert.Equal(6, job.Job.Counts.Creates);
        var rates = host.Get<IIndirectRateService>();
        var current = await rates.ListRatesAsync(2027);
        Assert.Equal(0.312m, current.Single(r => r.Pool == CostPool.Fringe && r.Basis == RateBasis.Provisional).Rate);
        Assert.Equal(0.121m, current.Single(r => r.Pool == CostPool.GeneralAndAdministrative && r.Basis == RateBasis.Provisional).Rate);
        Assert.Equal(0.55m, current.Single(r => r.Pool == CostPool.Overhead && r.Basis == RateBasis.ForwardPricing).Rate);

        var again = await host.ImportCostpointAsync(ImportKind.IndirectRates, "indirect-rates.csv");
        Assert.Equal((0, 0, 6), (again.Job.Counts.Creates, again.Job.Counts.Updates, again.Job.Counts.Skips));

        var change = await host.ImportAsync(ImportSource.Costpoint, ImportKind.IndirectRates,
            [Samples.Csv("r.csv", "Pool,Fiscal Year,Rate Type,Rate %\nOverhead,2027,Provisional,56.0\n")]);
        Assert.Equal(1, change.Job.Counts.Updates);
        Assert.Equal(0.56m, (await rates.ListRatesAsync(2027)).Single(r => r.Pool == CostPool.Overhead && r.Basis == RateBasis.Provisional).Rate);
        await host.Imports.RollBackAsync(change.Job.Id);
        Assert.Equal(0.545m, (await rates.ListRatesAsync(2027)).Single(r => r.Pool == CostPool.Overhead && r.Basis == RateBasis.Provisional).Rate);

        await host.Imports.RollBackAsync(job.Job.Id);
        Assert.Empty(await rates.ListRatesAsync(2027));
    }

    [Fact]
    public async Task Indirect_rates_explain_bad_pools_kinds_and_percentages()
    {
        await using var host = await ImportHost.CreateAsync();
        var dry = await Prepare(host, ImportKind.IndirectRates, "bad.csv", """
            Pool,Fiscal Year,Rate Type,Rate
            Facilities,2027,Provisional,0.3
            Fringe,2027,Whatever,0.3
            Fringe,2027,Actual,31.2
            Fringe,2027,Target,31.2%
            """);
        Assert.Equal(1, dry.Job.Counts.Creates);
        Assert.Contains(dry.Errors, e => e.Message.Contains("isn't one of Workbench's pools", StringComparison.Ordinal));
        Assert.Contains(dry.Errors, e => e.Message.Contains("isn't a rate kind", StringComparison.Ordinal));
        Assert.Contains(dry.Errors, e => e.Message.Contains("outside 0–500%", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Indirect_pools_set_accounts_and_base_and_roll_back_to_the_previous_accounts()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        var rates = host.Get<IIndirectRateService>();
        await rates.InstallDefaultPoolsAsync();
        var before = (await rates.ListPoolSetupsAsync()).ToDictionary(p => p.Pool);

        var job = await host.ImportCostpointAsync(ImportKind.IndirectPools, "indirect-pools.csv");
        Assert.True(job.Job.Status == ImportJobStatus.Imported && job.Job.Counts.Errors == 0, $"{job.Job.Message} {string.Join("; ", job.Errors.Select(e => e.Message))}");
        var after = (await rates.ListPoolSetupsAsync()).ToDictionary(p => p.Pool);
        Assert.Equal(3, after[CostPool.Fringe].AccountIds.Count);
        Assert.Equal(AllocationBaseKind.DirectLaborPlusFringe, after[CostPool.Overhead].Base);
        Assert.Equal(3, after[CostPool.GeneralAndAdministrative].CascadeOrder);

        var again = await host.ImportCostpointAsync(ImportKind.IndirectPools, "indirect-pools.csv");
        Assert.Equal((0, 0), (again.Job.Counts.Creates, again.Job.Counts.Updates));

        await host.Imports.RollBackAsync(again.Job.Id);
        await host.Imports.RollBackAsync(job.Job.Id);
        var restored = (await rates.ListPoolSetupsAsync()).ToDictionary(p => p.Pool);
        foreach (var pool in before.Keys)
        {
            Assert.Equal(before[pool].AccountIds.Order(), restored[pool].AccountIds.Order());
            Assert.Equal(before[pool].Base, restored[pool].Base);
        }
    }

    [Fact]
    public async Task Indirect_pools_name_the_account_that_is_missing_or_not_an_expense()
    {
        await using var host = await ImportHost.CreateAsync();
        await host.ImportCostpointAsync(ImportKind.Accounts, "accounts.csv");
        var dry = await Prepare(host, ImportKind.IndirectPools, "p.csv", "Pool,Account\nFringe,9999\nFringe,1010\nOverhead,6120\n");
        Assert.Contains(dry.Errors, e => e.Message.Contains("9999 isn't in Workbench's chart", StringComparison.Ordinal));
        Assert.Contains(dry.Errors, e => e.Message.Contains("isn't an expense account", StringComparison.Ordinal));
        // Overhead has no setup yet and the file gives no burden accounts.
        Assert.Contains(dry.Errors, e => e.Message.Contains("isn't set up in Workbench yet", StringComparison.Ordinal));
    }
}

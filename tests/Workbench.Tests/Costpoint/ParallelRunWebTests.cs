using System.Globalization;
using System.Net;
using System.Text;
using Workbench.ApiClient;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Migration;
using Workbench.Tests.Identity;

namespace Workbench.Tests.Costpoint;

public sealed class ParallelRunWebTests
{
    /// <summary>Workbench's own trial balance written as a Costpoint-style export (account, description, debit and credit balances).</summary>
    private static byte[] CostpointStyleTrialBalance(TrialBalanceDto tb, decimal bump = 0m)
    {
        var csv = new StringBuilder("Trial Balance Report,,,\nAccount,Account Description,Debit Balance,Credit Balance\n");
        var first = true;
        foreach (var row in tb.Rows.Where(r => r.Ending != 0m))
        {
            var ending = row.Ending + (first ? bump : 0m);
            first = false;
            var debit = ending > 0 ? ending.ToString("0.00", CultureInfo.InvariantCulture) : "";
            var credit = ending < 0 ? (-ending).ToString("0.00", CultureInfo.InvariantCulture) : "";
            csv.Append($"{row.AccountNumber},\"{row.AccountName}\",{debit},{credit}\n");
        }

        return Encoding.UTF8.GetBytes(csv.ToString());
    }

    [Fact]
    public async Task Finance_uploads_costpoints_trial_balance_over_the_real_host_and_only_finance_can()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        using var finance = web.CreateClientAs("pm@example.com");
        using var employee = web.CreateClientAs("analyst@example.com");
        var parallel = new HttpParallelRunService(finance);

        var start = await parallel.GetAsync();
        Assert.Equal(ParallelState.WaitingForCostpoint, start.State);

        // A period with postings in the demo data.
        var periods = await new HttpFiscalCalendarService(finance).ListPeriodsAsync();
        var report = new HttpAccountingReportService(finance);
        TrialBalanceDto? tb = null;
        foreach (var p in periods)
        {
            var candidate = await report.GetTrialBalanceAsync(p.Id);
            if (candidate.Rows.Any(r => r.Ending != 0m) && (tb is null || candidate.Rows.Count > tb.Rows.Count))
            {
                tb = candidate;
            }
        }

        Assert.NotNull(tb);
        var matched = await parallel.UploadAsync(new UploadParallelReportRequest(tb.FiscalPeriodId, ReconReportKind.TrialBalance, "tb.csv", CostpointStyleTrialBalance(tb)));
        var latest = matched.Reports.Single(r => r.Kind == ReconReportKind.TrialBalance).Latest;
        Assert.True(latest is { Outcome: ReconRunOutcome.Matched }, matched.Verdict);
        Assert.Equal(ParallelState.PartlyUploaded, matched.State);

        var off = await parallel.UploadAsync(new UploadParallelReportRequest(tb.FiscalPeriodId, ReconReportKind.TrialBalance, "tb2.csv", CostpointStyleTrialBalance(tb, 0.50m)));
        var cause = Assert.Single(off.Reports.Single(r => r.Kind == ReconReportKind.TrialBalance).Causes);
        Assert.Equal(ParallelCauseKind.Rounding, cause.Kind);
        Assert.Contains("under a dollar", cause.Summary);
        Assert.Contains(off.Months, m => m.PeriodId == tb.FiscalPeriodId);

        var forbidden = await employee.GetAsync("/api/v1/migration/parallel-run");
        Assert.True(forbidden.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized, forbidden.StatusCode.ToString());
    }
}

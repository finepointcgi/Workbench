using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Migration;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Domain.Migration;

namespace Workbench.Application.Migration;

/// <summary>
/// The Costpoint parallel run: a thin, plain-words layer over the reconciliation runs. Two reports matter for the books
/// (the trial balance and the project cost report); each upload is compared with the ledger by the reconciliation engine and
/// every difference is explained by <see cref="ParallelRunExplainer"/>. Sign-off, mappings, drill-down and the .xlsx
/// export stay on the reconciliation screens, so there is one set of evidence.
/// </summary>
public sealed class ParallelRunService(
    IReconciliationService reconciliation,
    IFiscalCalendarService calendar,
    ParallelRunExplainer explainer,
    IWorkbenchDbContextFactory dbFactory,
    TimeProvider clock) : IParallelRunService
{
    private static readonly ReconReportKind[] Kinds = [ReconReportKind.TrialBalance, ReconReportKind.ProjectCost];

    public async Task<ParallelRunDto> GetAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        var periods = await calendar.ListPeriodsAsync(null, cancellationToken);
        if (periods.Count == 0)
        {
            throw new ArgumentException("Set up the fiscal calendar first (Setup › Fiscal calendar, or import it from Costpoint).");
        }

        Guid? parallelId;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            parallelId = (await db.Set<MigrationSettings>().AsNoTracking().FirstOrDefaultAsync(cancellationToken))?.ParallelPeriodId;
        }

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var period = periods.FirstOrDefault(p => p.Id == fiscalPeriodId)
            ?? periods.FirstOrDefault(p => p.Id == parallelId)
            ?? periods.Where(p => p.End < today).OrderByDescending(p => p.End).FirstOrDefault()
            ?? periods.OrderBy(p => p.Start).First();
        if (fiscalPeriodId is { } asked && period.Id != asked)
        {
            throw WorkbenchNotFoundException.For("Fiscal period", asked);
        }

        var status = (await reconciliation.GetStatusAsync(period.Id, cancellationToken)).ToDictionary(s => s.Kind);
        var reports = new List<ParallelReportDto>();
        foreach (var kind in Kinds)
        {
            var report = ReconciliationReports.Get(kind);
            var latest = status[kind].LatestRun;
            var causes = latest is { Outcome: ReconRunOutcome.Differences }
                ? await explainer.ExplainAsync(await reconciliation.GetRunAsync(latest.Id, cancellationToken), cancellationToken)
                : [];
            reports.Add(new ParallelReportDto(kind, report.Title, report.CostpointReport, latest, causes));
        }

        var health = await explainer.HealthAsync(period.Id, period.Start, period.End, cancellationToken);
        var (state, verdict) = Verdict(reports, health);
        return new ParallelRunDto(period, period.Id == parallelId, state, verdict, health, reports, await MonthsAsync(cancellationToken));
    }

    public async Task<ParallelRunDto> UploadAsync(UploadParallelReportRequest request, CancellationToken cancellationToken = default)
    {
        if (!Kinds.Contains(request.Kind))
        {
            throw new ArgumentException("The parallel run compares the trial balance and the project cost report; use Reconcile for the other reports.");
        }

        var preview = await reconciliation.PreviewFileAsync(new PreviewReconFileRequest(request.Kind, request.FileName, request.Content), cancellationToken);
        var mapping = preview.SuggestedMapping;
        try
        {
            await reconciliation.RunAsync(new RunReconciliationRequest(request.Kind, request.FiscalPeriodId, request.FileName, request.Content,
                MappingId: mapping.Id, Mapping: mapping.Id is null ? mapping : null, SaveMappingAs: mapping.Id is null ? mapping.Name : null), cancellationToken);
        }
        catch (ArgumentException ex) when (!ex.Message.Contains("fiscal period", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException($"{ex.Message} If this is the right report, match its columns by hand under Reconcile (Setup › Go-live migration); the mapping is remembered for next month.");
        }

        return await GetAsync(request.FiscalPeriodId, cancellationToken);
    }

    public async Task<IReadOnlyList<ParallelCauseDto>> ExplainAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await explainer.ExplainAsync(await reconciliation.GetRunAsync(runId, cancellationToken), cancellationToken);

    private async Task<IReadOnlyList<ParallelMonthDto>> MonthsAsync(CancellationToken cancellationToken)
    {
        var runs = await reconciliation.ListRunsAsync(null, cancellationToken);
        var months = new List<ParallelMonthDto>();
        foreach (var group in runs.Where(r => Kinds.Contains(r.Kind)).GroupBy(r => r.FiscalPeriodId))
        {
            var latest = Kinds.Select(k => group.Where(r => r.Kind == k).MaxBy(r => r.RunAtUtc)).ToList();
            var differences = latest.Sum(r => r is null ? 0 : r.Different + r.OnlyInCostpoint + r.OnlyInWorkbench);
            var state = latest.Any(r => r is null) ? ParallelState.PartlyUploaded
                : latest.All(r => r!.IsSignedOff) ? ParallelState.SignedOff
                : differences > 0 ? ParallelState.Differences : ParallelState.Matched;
            months.Add(new ParallelMonthDto(group.Key, group.First().PeriodName, state, differences));
        }

        return months.OrderByDescending(m => m.PeriodName, StringComparer.Ordinal).ToList();
    }

    private static (ParallelState, string) Verdict(IReadOnlyList<ParallelReportDto> reports, ParallelHealthDto health)
    {
        var runs = reports.Select(r => r.Latest).ToList();
        if (runs.All(r => r is null))
        {
            return (ParallelState.WaitingForCostpoint, "Waiting for Costpoint. Once Costpoint has closed the month, upload its trial balance and project cost report here.");
        }

        if (runs.Any(r => r is null))
        {
            var missing = reports.First(r => r.Latest is null);
            return (ParallelState.PartlyUploaded, $"{missing.Title} is still to upload ({missing.CostpointReport}).");
        }

        var differences = reports.Sum(r => r.Latest!.Different + r.Latest.OnlyInCostpoint + r.Latest.OnlyInWorkbench);
        if (reports.All(r => r.IsSignedOff))
        {
            return (ParallelState.SignedOff, differences == 0 ? "Both reports match to the penny and are signed off." : "Both reports are signed off, with the differences explained in the sign-off notes.");
        }

        if (differences == 0)
        {
            return (ParallelState.Matched, "Both reports match Costpoint. Sign them off as the evidence for this month.");
        }

        var unexplained = reports.Sum(r => r.Unexplained);
        var pending = health.IsClean ? "" : $" Workbench still has {Plural(health.DraftJournalEntries, "draft entry", "draft entries")}, {Plural(health.UnapprovedTimesheets, "unapproved timesheet")} and {Plural(health.UnpostedBills, "unposted vendor bill")} in this month, which alone can cause differences.";
        return (ParallelState.Differences, $"{Plural(differences, "difference")} to look at; {(unexplained == 0 ? "each has a likely cause below" : $"{unexplained} still need a look in the detail")}.{pending}");
    }

    private static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
}

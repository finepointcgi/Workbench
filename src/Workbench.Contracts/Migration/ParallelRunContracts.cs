using Workbench.Contracts.ProjectAccounting;

namespace Workbench.Contracts.Migration;

// The Costpoint parallel run: during the parallel month Finance uploads Costpoint's trial balance and project cost report
// once the month closes, and Workbench shows where its ledger differs and the likely cause in plain words. It is built on
// the reconciliation runs (IReconciliationService); nothing here is stored separately.

/// <summary>What most likely explains a difference. Only a hint: the drill-down shows the entries behind it.</summary>
public enum ParallelCauseKind
{
    /// <summary>Under a dollar.</summary>
    Rounding,

    /// <summary>The balance already differed at the start of the month: an earlier month didn't reconcile.</summary>
    CarriedOver,

    /// <summary>Workbench has draft entries, bills or timesheets that aren't posted or approved yet.</summary>
    Unposted,

    /// <summary>Entries on either side dated or posted just outside the month.</summary>
    Timing,

    /// <summary>One entry is exactly the size of the difference.</summary>
    SingleEntry,

    /// <summary>Costs agree but the burden (fringe, overhead, G&amp;A) was applied at a different rate.</summary>
    RateDifference,

    /// <summary>Labor hours or cost rates differ.</summary>
    Hours,

    MissingInWorkbench,
    MissingInCostpoint,

    /// <summary>No automatic explanation: open the detail.</summary>
    Other,
}

/// <summary>One difference with its likely cause. <see cref="Difference"/> is Workbench minus Costpoint (the report's first compared amount).</summary>
public sealed record ParallelCauseDto(string Key, string? Label, ReconReportKind Report, ParallelCauseKind Kind, string Summary, decimal Difference, string? NextStep);

/// <summary>One report of the parallel run for a period: the latest comparison and why its differences exist.</summary>
public sealed record ParallelReportDto(
    ReconReportKind Kind,
    string Title,
    string CostpointReport,
    ReconRunSummaryDto? Latest,
    IReadOnlyList<ParallelCauseDto> Causes)
{
    public bool IsSignedOff => Latest?.IsSignedOff == true;
    public int Unexplained => Causes.Count(c => c.Kind == ParallelCauseKind.Other);
}

/// <summary>What is still unfinished on Workbench's side of the period, which by itself makes the books differ from Costpoint's.</summary>
public sealed record ParallelHealthDto(int DraftJournalEntries, int UnapprovedTimesheets, int UnpostedBills, bool PeriodClosed)
{
    public bool IsClean => DraftJournalEntries == 0 && UnapprovedTimesheets == 0 && UnpostedBills == 0;
}

public enum ParallelState
{
    /// <summary>Nothing uploaded yet.</summary>
    WaitingForCostpoint,

    /// <summary>One of the two reports is still to upload.</summary>
    PartlyUploaded,

    Differences,
    Matched,
    SignedOff,
}

/// <summary>A month already compared, for the history list.</summary>
public sealed record ParallelMonthDto(Guid PeriodId, string PeriodName, ParallelState State, int Differences);

public sealed record ParallelRunDto(
    FiscalPeriodDto Period,
    bool IsParallelMonth,
    ParallelState State,
    string Verdict,
    ParallelHealthDto Health,
    IReadOnlyList<ParallelReportDto> Reports,
    IReadOnlyList<ParallelMonthDto> Months);

/// <summary>Uploads one of the two Costpoint reports for a period and compares it, using the saved mapping or one suggested from the headers.</summary>
public sealed record UploadParallelReportRequest(Guid FiscalPeriodId, ReconReportKind Kind, string FileName, byte[] Content);

/// <summary>Finance or Admin only (see the migration permission decorators).</summary>
public interface IParallelRunService
{
    /// <summary>The parallel run for a period (default: the parallel month on the checklist, else the latest closed or current period).</summary>
    Task<ParallelRunDto> GetAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares Costpoint's trial balance or project cost report with the ledger and explains the differences. Fails with
    /// a plain message when the file's balance columns can't be found (the full reconcile screen maps them by hand).
    /// </summary>
    Task<ParallelRunDto> UploadAsync(UploadParallelReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>The likely causes behind any saved reconciliation run's differences.</summary>
    Task<IReadOnlyList<ParallelCauseDto>> ExplainAsync(Guid runId, CancellationToken cancellationToken = default);
}

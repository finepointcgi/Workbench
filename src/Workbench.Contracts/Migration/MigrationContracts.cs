using Workbench.Contracts.Imports;

namespace Workbench.Contracts.Migration;

// Go-live tools: the cut-over checklist (/setup/migration) and the parallel-run reconciliation against Costpoint
// reports (/setup/migration/reconcile). Finance and administrators only.

// ---- Cut-over checklist ----

public enum MigrationStepState { Pending, Done, Skipped }

/// <summary>Computed status of a checklist step.</summary>
public enum MigrationStepStatus { NotStarted, Blocked, InProgress, Failed, Done, Skipped }

/// <summary>
/// The cut-over: <see cref="CutoverDate"/> is the first day Workbench is the system of record (opening balances are
/// posted the day before); <see cref="ParallelPeriodId"/> is the month run in both systems and reconciled.
/// </summary>
public sealed record MigrationSettingsDto(DateOnly? CutoverDate, Guid? ParallelPeriodId, string? ParallelPeriodName, string? UpdatedBy, DateTime? UpdatedAtUtc)
{
    public DateOnly? OpeningBalanceDate => CutoverDate?.AddDays(-1);
}

public sealed record SaveMigrationSettingsRequest(DateOnly? CutoverDate, Guid? ParallelPeriodId);

/// <summary>One import a step uses. <see cref="Available"/> is false when this build has no importer for it yet.</summary>
public sealed record MigrationImportDto(
    ImportSource Source,
    string Kind,
    string Title,
    bool Required,
    bool Available,
    string? WizardLink,
    Guid? LastJobId,
    ImportJobStatus? LastJobStatus,
    DateTime? LastJobAtUtc,
    string? LastJobBy,
    int? LastJobCreates,
    int? LastJobErrors);

/// <summary>A data check shown on a step (count of records, a tie-out, a setup requirement).</summary>
public sealed record MigrationCheckDto(string Title, bool Passed, string Detail, bool Blocking = false);

public sealed record MigrationStepDto(
    string Key,
    int Order,
    string Title,
    string Description,
    MigrationStepStatus Status,
    IReadOnlyList<string> Prerequisites,
    IReadOnlyList<string> BlockedBy,
    IReadOnlyList<MigrationImportDto> Imports,
    IReadOnlyList<MigrationCheckDto> Checks,
    MigrationStepState? ManualState,
    string? ManualNote,
    string? ManualBy,
    DateTime? ManualAtUtc,
    string? Link = null,
    string? LinkText = null);

public sealed record MigrationChecklistDto(MigrationSettingsDto Settings, IReadOnlyList<MigrationStepDto> Steps, IReadOnlyList<MigrationCheckDto> Readiness)
{
    public int DoneCount => Steps.Count(s => s.Status is MigrationStepStatus.Done or MigrationStepStatus.Skipped);
}

/// <summary>Marks a step done or not applicable (for data entered by hand), or back to pending. A note is required to skip.</summary>
public sealed record SetMigrationStepRequest(MigrationStepState State, string? Note);

public interface IMigrationService
{
    Task<MigrationChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default);

    Task<MigrationSettingsDto> SaveSettingsAsync(SaveMigrationSettingsRequest request, CancellationToken cancellationToken = default);

    Task<MigrationChecklistDto> SetStepAsync(string stepKey, SetMigrationStepRequest request, CancellationToken cancellationToken = default);
}

// ---- Parallel-run reconciliation ----

public enum ReconReportKind { TrialBalance, ProjectCost, LaborHours, ArAging, ApAging, RevenueByProject, IndirectRates }

public enum ReconValueFormat { Money, Hours, Rate }

/// <summary>A key column (rows are matched on it), a compared amount, or a label shown alongside (never compared).</summary>
public enum ReconFieldRole { Key, Measure, Label }

/// <summary>How far back amounts go: the period, the fiscal year through the period, or inception through the period.</summary>
public enum ReconRange { Period, YearToDate, InceptionToDate }

public enum ReconRowStatus { Matched, Different, OnlyInCostpoint, OnlyInWorkbench }

public enum ReconRunOutcome { Matched, Differences }

/// <summary>A key column, a compared amount or a label column of a report.</summary>
public sealed record ReconFieldDto(
    string Name,
    string Label,
    ReconFieldRole Role,
    bool Required,
    string Description,
    IReadOnlyList<string> Aliases,
    ReconValueFormat Format = ReconValueFormat.Money,
    decimal DefaultTolerance = 0m);

public sealed record ReconReportDto(
    ReconReportKind Kind,
    string Title,
    string Description,
    string CostpointReport,
    IReadOnlyList<ReconFieldDto> Fields,
    string? SampleFile,
    bool UsesRange,
    bool UsesAsOf,
    ReconRange DefaultRange);

/// <summary>
/// Where a field comes from in the Costpoint file. A measure is <see cref="Column"/> − <see cref="MinusColumn"/>
/// (debit and credit columns), negated when <see cref="Negate"/>, times <see cref="Scale"/> (0.01 for percentages).
/// </summary>
public sealed record ReconColumnMapDto(string Field, string? Column, string? MinusColumn = null, bool Negate = false, decimal Scale = 1m);

/// <summary>A difference is accepted when it is within <see cref="Absolute"/> or within <see cref="Percent"/> % of the Costpoint amount.</summary>
public sealed record ReconToleranceDto(string Field, decimal Absolute, decimal Percent = 0m);

/// <summary>
/// How keys are compared. Keys are trimmed and compared case-insensitively. <see cref="IgnorePunctuation"/> compares
/// only letters and digits; <see cref="TrimLeadingZeros"/> treats 00123 as 123. Rows whose first key starts with one
/// of <see cref="SkipKeyPrefixes"/> (totals) are ignored. <see cref="KeyRewrites"/> translate Costpoint keys to
/// Workbench's (FRINGE BENEFITS → Fringe). <see cref="ProjectLevel"/>: compare projects (2001) or WBS elements (2001.02.01).
/// </summary>
public sealed record ReconOptionsDto(
    bool IgnorePunctuation = false,
    bool TrimLeadingZeros = false,
    IReadOnlyList<string>? SkipKeyPrefixes = null,
    IReadOnlyDictionary<string, string>? KeyRewrites = null,
    ReconRange? Range = null,
    bool ProjectLevel = true);

public sealed record ReconMappingDto(
    Guid? Id,
    ReconReportKind Kind,
    string Name,
    IReadOnlyList<ReconColumnMapDto> Columns,
    IReadOnlyList<ReconToleranceDto> Tolerances,
    ReconOptionsDto Options,
    string? UpdatedBy = null,
    DateTime? UpdatedAtUtc = null);

/// <summary>Headers and sample rows of an uploaded Costpoint report, with the suggested (or last saved) mapping.</summary>
public sealed record ReconFilePreviewDto(
    ReconReportKind Kind,
    string FileName,
    IReadOnlyList<string> Headers,
    IReadOnlyList<IReadOnlyList<string>> SampleRows,
    ReconMappingDto SuggestedMapping);

public sealed record PreviewReconFileRequest(ReconReportKind Kind, string FileName, byte[] Content);

/// <summary>
/// Runs a comparison and saves it. Use a saved mapping (<see cref="MappingId"/>) or pass one; <see cref="SaveMappingAs"/>
/// saves the passed mapping under that name first. <see cref="AsOf"/> (aging) defaults to the period end.
/// </summary>
public sealed record RunReconciliationRequest(
    ReconReportKind Kind,
    Guid FiscalPeriodId,
    string FileName,
    byte[] Content,
    Guid? MappingId = null,
    ReconMappingDto? Mapping = null,
    DateOnly? AsOf = null,
    string? SaveMappingAs = null);

public sealed record ReconValueDto(string Field, decimal Costpoint, decimal Workbench, bool WithinTolerance)
{
    public decimal Difference => Workbench - Costpoint;
}

/// <summary>One compared key. <see cref="SourceRows"/> are the Costpoint file rows added into it.</summary>
public sealed record ReconRowDto(string Key, IReadOnlyList<string> KeyParts, string? Label, ReconRowStatus Status, IReadOnlyList<ReconValueDto> Values, IReadOnlyList<int> SourceRows);

/// <summary>A file row that couldn't be read (bad number, missing key).</summary>
public sealed record ReconProblemDto(int Row, string Message);

public sealed record ReconRunSummaryDto(
    Guid Id,
    ReconReportKind Kind,
    Guid FiscalPeriodId,
    string PeriodName,
    DateOnly From,
    DateOnly To,
    string FileName,
    string MappingName,
    ReconRunOutcome Outcome,
    int Keys,
    int Matched,
    int Different,
    int OnlyInCostpoint,
    int OnlyInWorkbench,
    int Problems,
    string PrimaryField,
    decimal CostpointTotal,
    decimal WorkbenchTotal,
    string RunBy,
    DateTime RunAtUtc,
    string? SignedOffBy,
    DateTime? SignedOffAtUtc,
    string? SignOffNote)
{
    public bool IsSignedOff => SignedOffAtUtc is not null;
}

public sealed record ReconRunDetailDto(
    ReconRunSummaryDto Run,
    IReadOnlyList<string> KeyLabels,
    IReadOnlyList<ReconFieldDto> Measures,
    IReadOnlyList<ReconToleranceDto> Tolerances,
    IReadOnlyList<ReconRowDto> Rows,
    IReadOnlyList<ReconProblemDto> ProblemRows);

/// <summary>What makes up one compared key on each side: Workbench's detail (ledger lines, time entries, invoices) and the Costpoint file rows.</summary>
public sealed record ReconDrillDownDto(
    string Key,
    IReadOnlyList<string> WorkbenchColumns,
    IReadOnlyList<IReadOnlyList<string>> WorkbenchRows,
    IReadOnlyList<string> CostpointColumns,
    IReadOnlyList<IReadOnlyList<string>> CostpointRows,
    string? Note);

public sealed record SignOffReconciliationRequest(string? Note);

/// <summary>The latest run and the latest sign-off of each report for a period (the evidence board).</summary>
public sealed record ReconStatusDto(ReconReportKind Kind, string Title, ReconRunSummaryDto? LatestRun, ReconRunSummaryDto? LatestSignedOff);

public sealed record ReconExportFile(string FileName, string ContentType, byte[] Content);

public interface IReconciliationService
{
    Task<IReadOnlyList<ReconReportDto>> ListReportsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReconMappingDto>> ListMappingsAsync(ReconReportKind? kind = null, CancellationToken cancellationToken = default);

    /// <summary>Creates or replaces the mapping with this kind and name. Audited.</summary>
    Task<ReconMappingDto> SaveMappingAsync(ReconMappingDto mapping, CancellationToken cancellationToken = default);

    Task DeleteMappingAsync(Guid mappingId, CancellationToken cancellationToken = default);

    Task<ReconFilePreviewDto> PreviewFileAsync(PreviewReconFileRequest request, CancellationToken cancellationToken = default);

    Task<ReconRunDetailDto> RunAsync(RunReconciliationRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs, newest first, optionally for one period.</summary>
    Task<IReadOnlyList<ReconRunSummaryDto>> ListRunsAsync(Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default);

    Task<ReconRunDetailDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default);

    Task<ReconDrillDownDto> DrillDownAsync(Guid runId, string key, CancellationToken cancellationToken = default);

    /// <summary>Records who signed the run off and when. A run with differences needs a note explaining them. Once signed, a run can't change.</summary>
    Task<ReconRunSummaryDto> SignOffAsync(Guid runId, SignOffReconciliationRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReconStatusDto>> GetStatusAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default);

    /// <summary>The differences report as .xlsx (summary, differences, every row, unreadable rows). A security event.</summary>
    Task<ReconExportFile> ExportAsync(Guid runId, CancellationToken cancellationToken = default);
}

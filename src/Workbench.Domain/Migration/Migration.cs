namespace Workbench.Domain.Migration;

/// <summary>The cut-over plan: one row per tenant (the first day Workbench is the system of record, and the parallel month).</summary>
public sealed class MigrationSettings : Entity
{
    public DateOnly? CutoverDate { get; set; }
    public Guid? ParallelPeriodId { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum MigrationStepState
{
    Pending,
    Done,
    Skipped,
}

/// <summary>A checklist step marked done or not applicable by hand (data keyed in rather than imported).</summary>
public sealed class MigrationStepRecord : Entity
{
    public required string StepKey { get; set; }
    public MigrationStepState State { get; set; }
    public string? Note { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ReconciliationReport
{
    TrialBalance,
    ProjectCost,
    LaborHours,
    ArAging,
    ApAging,
    RevenueByProject,
    IndirectRates,
    UnanetTimesheets,
    UnanetProjectCost,
}

/// <summary>A saved column mapping and tolerances for one kind of Costpoint report (JSON), reused every parallel month.</summary>
public sealed class ReconciliationMapping : Entity
{
    public ReconciliationReport Report { get; set; }
    public required string Name { get; set; }
    public required string MappingJson { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

public enum ReconciliationOutcome
{
    Matched,
    Differences,
}

/// <summary>
/// One comparison of a Costpoint report with Workbench's figures for a period: the uploaded file, the mapping used, every
/// compared row (JSON) and, once reviewed, who signed it off. Kept as go-live evidence; a signed-off run never changes.
/// </summary>
public sealed class ReconciliationRun : Entity
{
    public ReconciliationReport Report { get; set; }
    public Guid FiscalPeriodId { get; set; }
    public required string PeriodName { get; set; }
    public DateOnly FromDate { get; set; }
    public DateOnly ToDate { get; set; }
    public Guid? MappingId { get; set; }
    public required string MappingName { get; set; }
    public required string MappingJson { get; set; }
    public required string FileName { get; set; }
    public byte[] FileContent { get; set; } = [];
    public ReconciliationOutcome Outcome { get; set; }
    public int KeyCount { get; set; }
    public int MatchedCount { get; set; }
    public int DifferentCount { get; set; }
    public int OnlyCostpointCount { get; set; }
    public int OnlyWorkbenchCount { get; set; }
    public int ProblemCount { get; set; }
    public required string PrimaryField { get; set; }
    public decimal CostpointTotal { get; set; }
    public decimal WorkbenchTotal { get; set; }

    /// <summary>The compared rows and unreadable file rows (JSON).</summary>
    public required string ResultJson { get; set; }

    public required string RunBy { get; set; }
    public DateTime RunAtUtc { get; set; }
    public string? SignedOffBy { get; set; }
    public DateTime? SignedOffAtUtc { get; set; }
    public string? SignOffNote { get; set; }
}

public static class ReconciliationLimits
{
    /// <summary>Largest stored result (PostgreSQL varchar allows up to 10,485,760 characters).</summary>
    public const int MaxResultJson = 10_000_000;
}

namespace Workbench.Domain.IncurredCost;

/// <summary>Where an incurred cost submission is in its workflow. Submitted packages are locked for good.</summary>
public enum IncurredCostStatus
{
    Draft,
    Reviewed,
    Certified,
    Submitted,
}

/// <summary>
/// The annual incurred cost submission (FAR 52.216-7(d)(2), the DCAA ICE model) for one fiscal year. The schedules are
/// generated from the general ledger, the indirect pools, contracts, billing and AP, and frozen in
/// <see cref="SchedulesJson"/>: a draft can be regenerated (the change in totals is kept in <see cref="LastDiffJson"/>);
/// from review on, the package only changes through the workflow, and once submitted it never changes.
/// </summary>
public sealed class IncurredCostSubmission : Entity
{
    public int FiscalYear { get; set; }
    public IncurredCostStatus Status { get; set; } = IncurredCostStatus.Draft;

    /// <summary>Increments on every generation.</summary>
    public int Revision { get; set; }

    /// <summary>The generated schedules (serialized contract DTOs), without the saved inputs applied.</summary>
    public string SchedulesJson { get; set; } = "";

    /// <summary>The totals that changed at the latest regeneration, or null when nothing has been regenerated.</summary>
    public string? LastDiffJson { get; set; }

    public required string GeneratedBy { get; set; }
    public DateTime GeneratedAtUtc { get; set; }

    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }

    public string? CertifiedBy { get; set; }
    public DateTime? CertifiedAtUtc { get; set; }

    /// <summary>The name of the certifying official, as signed.</summary>
    public string? CertifierName { get; set; }

    public string? CertifierTitle { get; set; }

    /// <summary>The Schedule N certificate exactly as certified.</summary>
    public string? CertificateText { get; set; }

    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }

    /// <summary>For example the DCAA receipt or transmittal number.</summary>
    public string? SubmissionReference { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public bool IsLocked => Status == IncurredCostStatus.Submitted;
}

/// <summary>
/// A value typed into the package where Workbench has no source field (an official's name, a pool adjustment, prior-year
/// cumulative cost...). Keyed by schedule, row and column of the generated tables; saved with the package.
/// </summary>
public sealed class IncurredCostInput : Entity
{
    public Guid SubmissionId { get; set; }
    public required string Schedule { get; set; }
    public required string RowKey { get; set; }
    public required string Column { get; set; }
    public string? Value { get; set; }
    public required string UpdatedBy { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}

namespace Workbench.Domain.ProjectAccounting;

/// <summary>Where a project's plan stands with Finance.</summary>
public enum ProjectPlanStatus
{
    /// <summary>The PM is building it.</summary>
    Draft,

    /// <summary>Sent to Finance to approve.</summary>
    Submitted,

    /// <summary>Approved: it became the baseline budget; later changes are the working version.</summary>
    Approved,

    /// <summary>Finance sent it back with comments.</summary>
    Returned,
}

/// <summary>
/// A project's plan terms and approval. The money terms are overrides only: left null, they come from the contract and
/// its CLINs (ceiling, funding, type, fee), so nothing the contract already says is stored twice. One row per project,
/// added when Finance sets terms or the PM first submits.
/// </summary>
public sealed class ProjectPlan : Entity
{
    public Guid ProjectId { get; set; }

    /// <summary>The project's ceiling (total value) when it isn't the contract's.</summary>
    public decimal? CeilingValue { get; set; }

    /// <summary>The project's funding when it isn't the contract's.</summary>
    public decimal? FundedValue { get; set; }

    /// <summary>How the work is priced when it isn't the contract's type (or there's no contract).</summary>
    public ContractType? Pricing { get; set; }

    /// <summary>Fee (0.08 = 8%) on cost when it isn't the contract's CLINs'.</summary>
    public decimal? FeeRate { get; set; }

    public ProjectPlanStatus Status { get; set; } = ProjectPlanStatus.Draft;
    public string? SubmittedBy { get; set; }
    public DateTime? SubmittedAtUtc { get; set; }
    public string? SubmitNote { get; set; }
    public string? ReviewedBy { get; set; }
    public DateTime? ReviewedAtUtc { get; set; }
    public string? ReviewComment { get; set; }

    /// <summary>How many times the plan has been approved (the first one set the baseline).</summary>
    public int Approvals { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? UpdatedBy { get; set; }
    public DateTime? UpdatedAtUtc { get; set; }
}

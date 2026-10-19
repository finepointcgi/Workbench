namespace Workbench.Domain.Security;

/// <summary>
/// A reviewer's answer to one item of an in-app compliance checklist (e.g. the DCAA SF 1408 accounting-system
/// self-assessment). The latest row per checklist and item is the current answer; earlier rows stay as history.
/// </summary>
public sealed class ComplianceChecklistEntry : Entity
{
    /// <summary>The checklist, e.g. <c>dcaa-sf1408</c>.</summary>
    public required string ChecklistKey { get; set; }

    /// <summary>The item within it, e.g. <c>timekeeping</c>.</summary>
    public required string ItemKey { get; set; }

    /// <summary><c>NotReviewed</c>, <c>Meets</c>, <c>Partial</c>, <c>DoesNotMeet</c> or <c>NotApplicable</c>.</summary>
    public required string Status { get; set; }

    public string? Notes { get; set; }

    public required string ReviewedBy { get; set; }

    public DateTime ReviewedAtUtc { get; set; }
}

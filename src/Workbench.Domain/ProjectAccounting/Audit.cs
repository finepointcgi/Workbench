namespace Workbench.Domain.ProjectAccounting;

/// <summary>
/// Append-only audit record for project-accounting changes (rates, setup, budgets, approvals).
/// Never updated or deleted by application code.
/// </summary>
public sealed class AuditEvent : Entity
{
    public required string EntityType { get; set; }
    public Guid EntityId { get; set; }

    /// <summary>For example Created, Updated, Deleted, Submitted, Approved, Rejected, Reopened.</summary>
    public required string Action { get; set; }

    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    public required string Actor { get; set; }
    public DateTime OccurredAtUtc { get; set; }
    public string? Reason { get; set; }
}

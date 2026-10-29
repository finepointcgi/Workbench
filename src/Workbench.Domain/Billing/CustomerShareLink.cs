namespace Workbench.Domain.Billing;

/// <summary>
/// A read-only link a contracting officer or customer can open without signing in: one contract's funding and invoice status.
/// Only the SHA-256 hash of the token is stored, so a leaked database doesn't leak working links; the person who creates the
/// link sees the token once. A link expires, and Finance can revoke it at any time.
/// </summary>
public sealed class CustomerShareLink : Entity
{
    public Guid ContractId { get; set; }

    /// <summary>Hex SHA-256 of the token in the link.</summary>
    public required string TokenHash { get; set; }

    /// <summary>Who it was made for, for example "Army CO, J. Rivera".</summary>
    public required string Label { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public DateTime? LastViewedAtUtc { get; set; }
    public int ViewCount { get; set; }
}

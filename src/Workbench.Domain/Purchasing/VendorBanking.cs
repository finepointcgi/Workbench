namespace Workbench.Domain.Purchasing;

public enum BankAccountKind
{
    Checking,
    Savings,
}

/// <summary>
/// A vendor's bank account for ACH payments. The routing and account numbers are ASP.NET Data Protection ciphertext; only
/// the last four digits are kept in clear. It changes only through an approved <see cref="VendorBankChange"/> (a changed
/// vendor bank account is a common payment-fraud vector), so whoever asks for the change can't make it take effect alone.
/// One per vendor.
/// </summary>
public sealed class VendorBankAccount : Entity
{
    public Guid VendorId { get; set; }

    /// <summary>The name on the account (the NACHA receiving company name).</summary>
    public required string AccountHolderName { get; set; }

    public BankAccountKind Kind { get; set; } = BankAccountKind.Checking;
    public required string ProtectedRoutingNumber { get; set; }
    public required string ProtectedAccountNumber { get; set; }
    public required string RoutingLast4 { get; set; }
    public required string AccountLast4 { get; set; }

    /// <summary>Where remittance advice is emailed; null falls back to the vendor's contact email.</summary>
    public string? RemittanceEmail { get; set; }

    /// <summary>The approved change that set these details.</summary>
    public Guid? ChangeId { get; set; }

    public required string ApprovedBy { get; set; }
    public DateTime ApprovedAtUtc { get; set; }
}

public enum VendorBankChangeStatus
{
    Pending,
    Approved,
    Rejected,
    Cancelled,
}

/// <summary>
/// A requested change to a vendor's ACH bank details, routed through the <c>vendor-bank-change</c> approval workflow.
/// The proposed numbers are encrypted like the live ones. Append-only apart from the decision.
/// </summary>
public sealed class VendorBankChange : Entity
{
    public Guid VendorId { get; set; }
    public VendorBankChangeStatus Status { get; set; } = VendorBankChangeStatus.Pending;

    public required string AccountHolderName { get; set; }
    public BankAccountKind Kind { get; set; } = BankAccountKind.Checking;
    public required string ProtectedRoutingNumber { get; set; }
    public required string ProtectedAccountNumber { get; set; }
    public required string RoutingLast4 { get; set; }
    public required string AccountLast4 { get; set; }
    public string? RemittanceEmail { get; set; }

    /// <summary>Why the details are changing and how they were confirmed (for example "Called the vendor at the number on file").</summary>
    public required string Reason { get; set; }

    public required string RequestedBy { get; set; }
    public DateTime RequestedAtUtc { get; set; }
    public string? DecidedBy { get; set; }
    public DateTime? DecidedAtUtc { get; set; }
    public string? DecisionComment { get; set; }
}

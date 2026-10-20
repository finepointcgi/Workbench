namespace Workbench.Domain.Purchasing;

/// <summary>
/// A person at a vendor who may use the supplier portal. Not an <c>AppUser</c>: contacts sign in with their own scheme
/// (an emailed link or a password), see only their vendor's records and never anything internal.
/// </summary>
public sealed class SupplierContact : Entity
{
    public Guid VendorId { get; set; }

    /// <summary>Lower case; unique across vendors (one portal account per email).</summary>
    public required string Email { get; set; }

    public required string Name { get; set; }
    public bool IsActive { get; set; } = true;

    /// <summary>ASP.NET Core Identity password hash (PBKDF2); null until the contact sets a password.</summary>
    public string? PasswordHash { get; set; }

    public int FailedSignIns { get; set; }
    public DateTime? LockedUntilUtc { get; set; }
    public DateTime? LastSignInUtc { get; set; }

    /// <summary>Bumped on deactivation and password change: older portal sessions end.</summary>
    public int SessionVersion { get; set; }

    public required string InvitedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? DeactivatedAtUtc { get; set; }
    public string? DeactivatedBy { get; set; }
}

/// <summary>A single-use sign-in link. Only the SHA-256 hash of the token is stored.</summary>
public sealed class SupplierSignInToken : Entity
{
    public Guid ContactId { get; set; }

    /// <summary>Hex SHA-256 of the token in the link.</summary>
    public required string TokenHash { get; set; }

    public DateTime CreatedAtUtc { get; set; }
    public DateTime ExpiresAtUtc { get; set; }
    public DateTime? UsedAtUtc { get; set; }
}

/// <summary>Everything a contact does on the portal, including reads and sign-ins. Append-only.</summary>
public sealed class SupplierPortalEvent : Entity
{
    public Guid? ContactId { get; set; }
    public Guid? VendorId { get; set; }

    /// <summary>The contact's email (or the email typed at a failed sign-in).</summary>
    public required string Actor { get; set; }

    /// <summary>For example "signin.link", "rfq.viewed", "quote.submitted", "invoice.submitted".</summary>
    public required string Action { get; set; }

    /// <summary>The record acted on, for example "RFQ-000004" or "PO-000012".</summary>
    public string? Target { get; set; }

    public Guid? TargetId { get; set; }
    public string Outcome { get; set; } = "Success";
    public string? Detail { get; set; }
    public string? IpAddress { get; set; }
    public DateTime OccurredAtUtc { get; set; }
}

public enum PurchaseOrderAcknowledgementStatus
{
    /// <summary>Accepted as ordered.</summary>
    Accepted,

    /// <summary>Accepted with changes (dates, quantities or prices) the buyer should review.</summary>
    AcceptedWithChanges,

    Rejected,
}

public enum SupplierDocumentSource
{
    Portal,
    Edi,
}

/// <summary>A vendor's acknowledgement of a purchase order revision (portal or EDI 855). Append-only; the latest counts.</summary>
public sealed class PurchaseOrderAcknowledgement : Entity
{
    public Guid PurchaseOrderId { get; set; }
    public Guid VendorId { get; set; }
    public int Revision { get; set; }
    public PurchaseOrderAcknowledgementStatus Status { get; set; }
    public string? Note { get; set; }
    public SupplierDocumentSource Source { get; set; }
    public required string AcknowledgedBy { get; set; }
    public DateTime AcknowledgedAtUtc { get; set; }

    /// <summary>The buyer has looked at the changes (only meaningful for <see cref="PurchaseOrderAcknowledgementStatus.AcceptedWithChanges"/>).</summary>
    public string? ReviewedBy { get; set; }

    public DateTime? ReviewedAtUtc { get; set; }

    public List<PurchaseOrderAcknowledgementLine> Lines { get; } = [];
}

public sealed class PurchaseOrderAcknowledgementLine : Entity
{
    public Guid AcknowledgementId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }

    /// <summary>The date the vendor commits to deliver.</summary>
    public DateOnly? PromisedDate { get; set; }

    /// <summary>Set when the vendor can't supply the ordered quantity or price.</summary>
    public decimal? Quantity { get; set; }

    public decimal? UnitPrice { get; set; }
    public string? Note { get; set; }
}

public enum ShipNoticeStatus
{
    /// <summary>On its way.</summary>
    InTransit,

    /// <summary>Received against the purchase order.</summary>
    Received,

    Cancelled,
}

/// <summary>An advance ship notice (portal or EDI 856): what the vendor shipped against a purchase order.</summary>
public sealed class ShipNotice : Entity
{
    /// <summary>Our number, for example "ASN-000003".</summary>
    public required string Number { get; set; }

    public Guid PurchaseOrderId { get; set; }
    public Guid VendorId { get; set; }

    /// <summary>The vendor's shipment id (BSN02 in an 856).</summary>
    public string? ShipmentReference { get; set; }

    public DateOnly ShipDate { get; set; }
    public DateOnly? ExpectedDelivery { get; set; }
    public string? Carrier { get; set; }
    public string? TrackingNumber { get; set; }
    public ShipNoticeStatus Status { get; set; } = ShipNoticeStatus.InTransit;
    public SupplierDocumentSource Source { get; set; }
    public required string SubmittedBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; }

    /// <summary>The receipt it was received on.</summary>
    public Guid? ReceiptId { get; set; }

    public List<ShipNoticeLine> Lines { get; } = [];
}

public sealed class ShipNoticeLine : Entity
{
    public Guid ShipNoticeId { get; set; }
    public Guid PurchaseOrderLineId { get; set; }
    public decimal Quantity { get; set; }
}

/// <summary>
/// An invoice a vendor sent through the portal or EDI 810. It lands as a draft AP bill (<see cref="BillId"/>) for
/// accounts payable to check and submit; this row keeps where it came from.
/// </summary>
public sealed class SupplierInvoice : Entity
{
    public Guid VendorId { get; set; }
    public Guid PurchaseOrderId { get; set; }
    public Guid BillId { get; set; }
    public required string InvoiceNumber { get; set; }
    public DateOnly InvoiceDate { get; set; }

    /// <summary>The total the vendor stated (the bill's lines are the PO quantities × PO prices they billed).</summary>
    public decimal StatedTotal { get; set; }

    public SupplierDocumentSource Source { get; set; }
    public required string SubmittedBy { get; set; }
    public DateTime SubmittedAtUtc { get; set; }
}

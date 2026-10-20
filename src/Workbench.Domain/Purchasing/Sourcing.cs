using Workbench.Domain.ProjectAccounting;

namespace Workbench.Domain.Purchasing;

public enum RfqStatus
{
    Draft,

    /// <summary>Sent to the invited vendors; quotes are accepted until it's awarded or cancelled.</summary>
    Open,

    /// <summary>Some lines are awarded; the rest are still open for award.</summary>
    PartiallyAwarded,

    Awarded,
    Cancelled,
}

/// <summary>A request for quotation: lines to buy (from requisitions or typed in), the vendors invited and a due date.</summary>
public sealed class Rfq : Entity
{
    /// <summary>For example "RFQ-000004".</summary>
    public required string Number { get; set; }

    public required string Title { get; set; }
    public RfqStatus Status { get; set; } = RfqStatus.Draft;

    /// <summary>Quotes are due by the end of this day.</summary>
    public DateOnly DueDate { get; set; }

    /// <summary>Payment and delivery terms asked for, for example "Net 30, FOB destination".</summary>
    public string? Terms { get; set; }

    public string? ShipTo { get; set; }

    /// <summary>Instructions to the vendors (shown on the supplier portal).</summary>
    public string? Instructions { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string? IssuedBy { get; set; }
    public DateTime? IssuedAtUtc { get; set; }
    public string? CancelReason { get; set; }

    public List<RfqLine> Lines { get; } = [];
}

public sealed class RfqLine : Entity
{
    public Guid RfqId { get; set; }
    public int LineNumber { get; set; }
    public required string Description { get; set; }

    /// <summary>The part from the part master, when the line buys one.</summary>
    public Guid? PartId { get; set; }

    public decimal Quantity { get; set; }

    /// <summary>For example "EA".</summary>
    public string UnitOfMeasure { get; set; } = "EA";

    public DateOnly? NeededBy { get; set; }

    /// <summary>What we expect to pay per unit (the requisition's cost, or a buyer's estimate); used in the price analysis.</summary>
    public decimal? EstimatedUnitCost { get; set; }

    // Charging, copied from the requisition line or entered with the line; the purchase order uses it.
    public Guid? ProjectId { get; set; }
    public Guid? WbsElementId { get; set; }
    public OdcType? OdcType { get; set; }
    public Guid? AccountId { get; set; }
    public Guid? OrgUnitId { get; set; }

    /// <summary>The approved requisition line it sources, when it came from one.</summary>
    public Guid? RequisitionLineId { get; set; }

    /// <summary>Set at award: the winning quote line and the PO line it became.</summary>
    public Guid? AwardedQuoteLineId { get; set; }

    public Guid? AwardId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
}

public enum RfqInvitationStatus
{
    Invited,
    Quoted,
    Declined,
}

/// <summary>A vendor invited to quote.</summary>
public sealed class RfqVendor : Entity
{
    public Guid RfqId { get; set; }
    public Guid VendorId { get; set; }
    public RfqInvitationStatus Status { get; set; } = RfqInvitationStatus.Invited;
    public DateTime InvitedAtUtc { get; set; }
    public string? DeclineReason { get; set; }
}

public enum VendorQuoteSource
{
    /// <summary>Typed in by a buyer from an emailed or phoned quote.</summary>
    Buyer,

    /// <summary>Submitted by the vendor on the supplier portal.</summary>
    Portal,
}

/// <summary>One vendor's quote on an RFQ (the latest submission replaces the earlier one; history is in the audit log).</summary>
public sealed class VendorQuote : Entity
{
    public Guid RfqId { get; set; }
    public Guid VendorId { get; set; }

    /// <summary>The vendor's quote reference, if any.</summary>
    public string? Reference { get; set; }

    public DateOnly QuoteDate { get; set; }

    /// <summary>The prices hold through this date.</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Lead time in calendar days from order, unless a line says otherwise.</summary>
    public int LeadTimeDays { get; set; }

    public string? Terms { get; set; }
    public string? Notes { get; set; }
    public VendorQuoteSource Source { get; set; }

    /// <summary>The buyer's email, or the portal contact's.</summary>
    public required string SubmittedBy { get; set; }

    public DateTime SubmittedAtUtc { get; set; }

    /// <summary>Kept for the file once the RFQ is awarded.</summary>
    public Guid? AttachmentFileId { get; set; }

    public List<VendorQuoteLine> Lines { get; } = [];
}

public sealed class VendorQuoteLine : Entity
{
    public Guid QuoteId { get; set; }
    public Guid RfqLineId { get; set; }

    /// <summary>The vendor didn't quote this line.</summary>
    public bool NoBid { get; set; }

    /// <summary>The price for the RFQ quantity when there are no price breaks (or below the first break).</summary>
    public decimal UnitPrice { get; set; }

    /// <summary>Overrides the quote's lead time.</summary>
    public int? LeadTimeDays { get; set; }

    public string? Note { get; set; }

    public List<VendorQuotePriceBreak> PriceBreaks { get; } = [];
}

/// <summary>A quantity discount: at or above <see cref="MinimumQuantity"/>, the unit price is <see cref="UnitPrice"/>.</summary>
public sealed class VendorQuotePriceBreak : Entity
{
    public Guid QuoteLineId { get; set; }
    public decimal MinimumQuantity { get; set; }
    public decimal UnitPrice { get; set; }
}

/// <summary>
/// One award decision on an RFQ: which lines went to which quotes, and the competition record kept for the purchasing
/// file (CPSR): how many vendors were asked and quoted, the price analysis, the sole-source justification when only
/// one quote came in, and the comparison as it stood. Append-only.
/// </summary>
public sealed class RfqAward : Entity
{
    public Guid RfqId { get; set; }
    public required string AwardedBy { get; set; }
    public DateTime AwardedAtUtc { get; set; }
    public int VendorsInvited { get; set; }

    /// <summary>The fewest responsive quotes on any awarded line.</summary>
    public int QuotesReceived { get; set; }

    public bool SoleSource { get; set; }
    public string? SoleSourceJustification { get; set; }

    /// <summary>Why the prices are fair and reasonable (FAR 15.404-1 / 13.106-3), for example "Adequate price competition".</summary>
    public required string PriceReasonableness { get; set; }

    /// <summary>Why the award went to the vendor(s) it did, required when a line isn't awarded to its lowest price.</summary>
    public string? Rationale { get; set; }

    public decimal AwardedTotal { get; set; }

    /// <summary>The comparison grid at award, as JSON (frozen evidence).</summary>
    public required string ComparisonJson { get; set; }

    /// <summary>Comma-separated purchase order numbers created by the award.</summary>
    public required string PurchaseOrderNumbers { get; set; }
}

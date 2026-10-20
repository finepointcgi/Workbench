using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Purchasing;

// Sourcing: requests for quotation (RFQs) built from approved requisition lines or typed in, sent to invited vendors, the
// quotes they return (price breaks, lead time, validity), a comparison that scores price, lead time and each vendor's
// on-time delivery, and the award by line, which creates the purchase orders and keeps the competition record for the
// purchasing file (CPSR): how many quotes, the price analysis, and the sole-source justification when only one came in.
// Finance and Admin (the buyers) do all of it.

public enum RfqState { Draft, Open, PartiallyAwarded, Awarded, Cancelled }

public enum RfqInvitationState { Invited, Quoted, Declined }

public enum QuoteSource { Buyer, Portal }

public sealed record RfqSummaryDto(
    Guid Id,
    string Number,
    string Title,
    RfqState Status,
    DateOnly DueDate,
    int LineCount,
    int VendorsInvited,
    int QuotesReceived,
    DateTime CreatedAtUtc,
    IReadOnlyList<string> PurchaseOrderNumbers);

public sealed record RfqLineDto(
    Guid Id,
    int LineNumber,
    string Description,
    Guid? PartId,
    string? PartNumber,
    decimal Quantity,
    string UnitOfMeasure,
    DateOnly? NeededBy,
    decimal? EstimatedUnitCost,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    OdcCostType? OdcType,
    Guid? AccountId,
    string? AccountNumber,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    Guid? RequisitionLineId,
    string? RequisitionNumber,
    Guid? AwardedQuoteLineId,
    string? AwardedVendorName,
    decimal? AwardedUnitPrice,
    Guid? PurchaseOrderId,
    string? PurchaseOrderNumber);

public sealed record RfqInvitationDto(
    Guid VendorId,
    string VendorCode,
    string VendorName,
    RfqInvitationState Status,
    string? DeclineReason,
    int PortalContacts,
    bool HasEdi);

/// <summary>At or above <see cref="MinimumQuantity"/>, the unit price is <see cref="UnitPrice"/>.</summary>
public sealed record PriceBreakDto(decimal MinimumQuantity, decimal UnitPrice);

public sealed record QuoteLineDto(
    Guid Id,
    Guid RfqLineId,
    int LineNumber,
    bool NoBid,
    decimal UnitPrice,
    IReadOnlyList<PriceBreakDto> PriceBreaks,
    int LeadTimeDays,
    string? Note,

    /// <summary>The unit price at the RFQ quantity (after price breaks).</summary>
    decimal EffectiveUnitPrice,
    decimal ExtendedPrice);

public sealed record VendorQuoteDto(
    Guid Id,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    string? Reference,
    DateOnly QuoteDate,
    DateOnly? ValidUntil,
    int LeadTimeDays,
    string? Terms,
    string? Notes,
    QuoteSource Source,
    string SubmittedBy,
    DateTime SubmittedAtUtc,
    bool IsExpired,
    decimal Total,
    IReadOnlyList<QuoteLineDto> Lines);

public sealed record RfqAwardLineDto(int LineNumber, string Description, string VendorName, decimal Quantity, decimal UnitPrice, decimal Amount, bool WasLowestPrice, int QuotesOnLine);

/// <summary>The competition record kept for the purchasing file.</summary>
public sealed record RfqAwardDto(
    Guid Id,
    string AwardedBy,
    DateTime AwardedAtUtc,
    int VendorsInvited,
    int QuotesReceived,
    bool SoleSource,
    string? SoleSourceJustification,
    string PriceReasonableness,
    string? Rationale,
    decimal AwardedTotal,
    IReadOnlyList<string> PurchaseOrderNumbers,
    IReadOnlyList<RfqAwardLineDto> Lines);

public sealed record RfqDto(
    RfqSummaryDto Rfq,
    string? Terms,
    string? ShipTo,
    string? Instructions,
    string CreatedBy,
    string? IssuedBy,
    DateTime? IssuedAtUtc,
    string? CancelReason,
    IReadOnlyList<RfqLineDto> Lines,
    IReadOnlyList<RfqInvitationDto> Vendors,
    IReadOnlyList<VendorQuoteDto> Quotes,
    IReadOnlyList<RfqAwardDto> Awards);

/// <summary>
/// One RFQ line. With <see cref="RequisitionLineId"/> (an approved, unordered requisition line) the description,
/// quantity, need date, charging and estimate come from the requisition and the other fields are ignored. Otherwise the
/// charging is a WBS element (and ODC type) or a GL account (and organization), as on a purchase order.
/// </summary>
public sealed record SaveRfqLineRequest(
    string Description,
    decimal Quantity,
    string? UnitOfMeasure = null,
    DateOnly? NeededBy = null,
    decimal? EstimatedUnitCost = null,
    Guid? WbsElementId = null,
    OdcCostType? OdcType = null,
    Guid? AccountId = null,
    Guid? OrgUnitId = null,
    Guid? PartId = null,
    Guid? RequisitionLineId = null);

public sealed record SaveRfqRequest(
    string Title,
    DateOnly DueDate,
    IReadOnlyList<SaveRfqLineRequest> Lines,
    IReadOnlyList<Guid> VendorIds,
    string? Terms = null,
    string? ShipTo = null,
    string? Instructions = null);

public sealed record SaveQuoteLineRequest(
    Guid RfqLineId,
    decimal UnitPrice,
    IReadOnlyList<PriceBreakDto>? PriceBreaks = null,
    int? LeadTimeDays = null,
    bool NoBid = false,
    string? Note = null);

/// <summary>A quote a buyer types in for a vendor (a phoned or emailed quote). It replaces that vendor's earlier quote.</summary>
public sealed record SaveQuoteRequest(
    Guid VendorId,
    DateOnly QuoteDate,
    int LeadTimeDays,
    IReadOnlyList<SaveQuoteLineRequest> Lines,
    DateOnly? ValidUntil = null,
    string? Reference = null,
    string? Terms = null,
    string? Notes = null);

public sealed record RfqReasonRequest(string Reason);

public sealed record RfqQuery(RfqState? Status = null, string? Search = null);

// ---- Comparison ----

/// <summary>How the comparison weighs each factor (percent; they add up to 100).</summary>
public sealed record ScoringWeightsDto(int Price, int LeadTime, int Delivery);

public sealed record ComparisonCellDto(
    Guid VendorId,
    Guid? QuoteLineId,
    bool Quoted,
    bool NoBid,
    decimal? UnitPrice,
    decimal? Extended,
    int? LeadTimeDays,

    /// <summary>0–100: weighted price, lead time and delivery scores.</summary>
    decimal? Score,
    bool IsLowestPrice,
    bool IsRecommended,
    bool IsExpired,

    /// <summary>The lead time puts delivery after the line's need date (counted from today).</summary>
    bool LateForNeedDate);

public sealed record ComparisonLineDto(
    Guid RfqLineId,
    int LineNumber,
    string Description,
    decimal Quantity,
    string UnitOfMeasure,
    DateOnly? NeededBy,
    decimal? EstimatedUnitCost,
    int ResponsiveQuotes,
    IReadOnlyList<ComparisonCellDto> Cells,
    Guid? AwardedQuoteLineId);

/// <summary>A vendor's delivery record: receipts in the last two years against PO lines with a need date, received on or before it.</summary>
public sealed record VendorPerformanceDto(
    Guid VendorId,
    string VendorCode,
    string VendorName,
    bool IsOnHold,
    int ReceiptsMeasured,
    int OnTimeReceipts,
    decimal? OnTimePercent,
    bool HasQuote,
    decimal? QuoteTotal,
    DateOnly? ValidUntil,
    bool IsExpired);

public sealed record RfqComparisonDto(
    Guid RfqId,
    string Number,
    string Title,
    RfqState Status,
    ScoringWeightsDto Weights,
    IReadOnlyList<VendorPerformanceDto> Vendors,
    IReadOnlyList<ComparisonLineDto> Lines);

public sealed record AwardLineRequest(Guid RfqLineId, Guid QuoteLineId);

/// <summary>
/// Awards lines to quote lines and creates one purchase order per vendor. <see cref="PriceReasonableness"/> is always
/// required; <see cref="SoleSourceJustification"/> when any awarded line had only one responsive quote; <see cref="Rationale"/>
/// when any line goes to a quote that isn't its lowest price.
/// </summary>
public sealed record AwardRfqRequest(
    IReadOnlyList<AwardLineRequest> Lines,
    string PriceReasonableness,
    string? SoleSourceJustification = null,
    string? Rationale = null,
    DateOnly? OrderDate = null,
    IReadOnlyList<string>? Receivers = null,

    /// <summary>Issue the new orders right away (otherwise they're drafts).</summary>
    bool IssueOrders = false);

public interface IRfqService
{
    Task<IReadOnlyList<RfqSummaryDto>> ListAsync(RfqQuery query, CancellationToken cancellationToken = default);

    Task<RfqDto> GetAsync(Guid rfqId, CancellationToken cancellationToken = default);

    /// <summary>Approved requisition lines that aren't ordered and aren't on an open RFQ.</summary>
    Task<IReadOnlyList<OrderableRequisitionLineDto>> ListSourceableRequisitionLinesAsync(CancellationToken cancellationToken = default);

    Task<RfqDto> CreateAsync(SaveRfqRequest request, CancellationToken cancellationToken = default);

    /// <summary>Drafts only.</summary>
    Task<RfqDto> UpdateAsync(Guid rfqId, SaveRfqRequest request, CancellationToken cancellationToken = default);

    /// <summary>Opens it for quotes and tells the invited vendors' portal contacts.</summary>
    Task<RfqDto> IssueAsync(Guid rfqId, CancellationToken cancellationToken = default);

    /// <summary>Invites another vendor to an open RFQ.</summary>
    Task<RfqDto> InviteVendorAsync(Guid rfqId, Guid vendorId, CancellationToken cancellationToken = default);

    Task<RfqDto> RecordQuoteAsync(Guid rfqId, SaveQuoteRequest request, CancellationToken cancellationToken = default);

    Task<RfqComparisonDto> GetComparisonAsync(Guid rfqId, CancellationToken cancellationToken = default);

    Task<RfqDto> AwardAsync(Guid rfqId, AwardRfqRequest request, CancellationToken cancellationToken = default);

    /// <summary>Drafts and open RFQs with nothing awarded.</summary>
    Task<RfqDto> CancelAsync(Guid rfqId, RfqReasonRequest request, CancellationToken cancellationToken = default);
}

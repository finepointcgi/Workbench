namespace Workbench.Contracts.Purchasing;

// The supplier portal: invited vendor contacts sign in with their own sign-in (an emailed link or a password) and see only
// their own vendor's RFQs, purchase orders, ship notices and invoices. ISupplierPortalService is what they use; it never
// takes a vendor id: the vendor is the signed-in contact's, and anything else is "not found". ISupplierPortalAdminService
// is the buyers' side (Finance/Admin): contacts, the portal activity log, acknowledgements and ship notices.

public enum AcknowledgementState { Accepted, AcceptedWithChanges, Rejected }

public enum ShipNoticeState { InTransit, Received, Cancelled }

/// <summary>Where a vendor document came from.</summary>
public enum SupplierChannel { Portal, Edi }

// ---- Vendor-facing ----

public sealed record SupplierSessionDto(Guid ContactId, string ContactName, string Email, Guid VendorId, string VendorCode, string VendorName, bool HasPassword);

public sealed record SupplierHomeDto(SupplierSessionDto Me, int OpenRfqs, int OrdersToAcknowledge, int OpenOrders, int InvoicesInReview, decimal AmountDue);

public sealed record SupplierRfqSummaryDto(
    Guid Id,
    string Number,
    string Title,
    DateOnly DueDate,

    /// <summary>Plain words: "Open", "Closed", "Cancelled".</summary>
    string Status,
    bool CanQuote,
    bool Quoted,
    bool Declined,

    /// <summary>After award: "Awarded to you", "Not awarded", "Partly awarded to you".</summary>
    string? Outcome);

public sealed record SupplierRfqLineDto(Guid Id, int LineNumber, string Description, string? PartNumber, decimal Quantity, string UnitOfMeasure, DateOnly? NeededBy);

public sealed record SupplierQuoteLineDto(Guid RfqLineId, bool NoBid, decimal UnitPrice, IReadOnlyList<PriceBreakDto> PriceBreaks, int? LeadTimeDays, string? Note);

public sealed record SupplierQuoteDto(string? Reference, DateOnly QuoteDate, DateOnly? ValidUntil, int LeadTimeDays, string? Terms, string? Notes, DateTime SubmittedAtUtc, IReadOnlyList<SupplierQuoteLineDto> Lines);

public sealed record SupplierRfqDto(SupplierRfqSummaryDto Rfq, string? Terms, string? ShipTo, string? Instructions, IReadOnlyList<SupplierRfqLineDto> Lines, SupplierQuoteDto? MyQuote);

/// <summary>A quote from the portal; it replaces the vendor's earlier quote until the due date.</summary>
public sealed record SupplierQuoteRequest(
    int LeadTimeDays,
    IReadOnlyList<SaveQuoteLineRequest> Lines,
    DateOnly? ValidUntil = null,
    string? Reference = null,
    string? Terms = null,
    string? Notes = null);

public sealed record SupplierOrderSummaryDto(
    Guid Id,
    string Number,
    DateOnly OrderDate,
    int Revision,
    decimal Total,

    /// <summary>Plain words: "Open", "Partly received", "Received", "Closed", "Cancelled".</summary>
    string Status,
    AcknowledgementState? Acknowledgement,
    bool NeedsAcknowledgement);

public sealed record SupplierOrderLineDto(
    Guid Id,
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    DateOnly? NeededBy,
    DateOnly? PromisedDate,
    decimal QuantityReceived,

    /// <summary>On ship notices that aren't cancelled.</summary>
    decimal QuantityShipped,

    /// <summary>On invoices (bills) that aren't rejected or void.</summary>
    decimal QuantityInvoiced);

public sealed record SupplierAcknowledgementDto(int Revision, AcknowledgementState Status, string? Note, SupplierChannel Channel, string AcknowledgedBy, DateTime AcknowledgedAtUtc);

public sealed record SupplierShipNoticeDto(Guid Id, string Number, string? ShipmentReference, DateOnly ShipDate, DateOnly? ExpectedDelivery, string? Carrier, string? TrackingNumber, ShipNoticeState Status, SupplierChannel Channel);

public sealed record SupplierOrderDto(
    SupplierOrderSummaryDto Order,
    string? Terms,
    string? ShipTo,
    IReadOnlyList<SupplierOrderLineDto> Lines,
    IReadOnlyList<SupplierAcknowledgementDto> Acknowledgements,
    IReadOnlyList<SupplierShipNoticeDto> ShipNotices,
    IReadOnlyList<SupplierInvoiceDto> Invoices,
    bool CanShip,
    bool CanInvoice);

public sealed record AcknowledgeLineRequest(Guid PurchaseOrderLineId, DateOnly? PromisedDate, decimal? Quantity = null, decimal? UnitPrice = null, string? Note = null);

/// <summary>Accept (with promised dates), accept with changes (quantity or price per line, and a note), or reject (a note).</summary>
public sealed record AcknowledgeOrderRequest(AcknowledgementState Status, IReadOnlyList<AcknowledgeLineRequest>? Lines = null, string? Note = null);

public sealed record ShipNoticeLineRequest(Guid PurchaseOrderLineId, decimal Quantity);

public sealed record ShipNoticeRequest(
    DateOnly ShipDate,
    IReadOnlyList<ShipNoticeLineRequest> Lines,
    DateOnly? ExpectedDelivery = null,
    string? Carrier = null,
    string? TrackingNumber = null,
    string? ShipmentReference = null);

public sealed record SupplierInvoiceLineRequest(Guid PurchaseOrderLineId, decimal Quantity);

/// <summary>An invoice against one purchase order: header fields, the quantities billed per PO line, and the PDF.</summary>
public sealed record SupplierInvoiceRequest(
    Guid PurchaseOrderId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    decimal StatedTotal,
    IReadOnlyList<SupplierInvoiceLineRequest> Lines);

public sealed record SupplierInvoiceDto(
    Guid BillId,
    string InvoiceNumber,
    DateOnly InvoiceDate,
    string? PurchaseOrderNumber,
    decimal Amount,
    decimal Paid,
    decimal Balance,
    DateOnly DueDate,

    /// <summary>Plain words: "Received", "In review", "Approved", "Scheduled for payment", "Paid", "Partly paid", "Not accepted", "Void".</summary>
    string Status,
    DateOnly? LastPaidOn,
    string? PaymentReference,

    /// <summary>Why it wasn't accepted, when it wasn't.</summary>
    string? Note);

public interface ISupplierPortalService
{
    Task<SupplierSessionDto> GetSessionAsync(CancellationToken cancellationToken = default);

    Task<SupplierHomeDto> GetHomeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierRfqSummaryDto>> ListRfqsAsync(CancellationToken cancellationToken = default);

    Task<SupplierRfqDto> GetRfqAsync(Guid rfqId, CancellationToken cancellationToken = default);

    Task<SupplierRfqDto> SubmitQuoteAsync(Guid rfqId, SupplierQuoteRequest request, CancellationToken cancellationToken = default);

    Task<SupplierRfqDto> DeclineRfqAsync(Guid rfqId, string? reason, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierOrderSummaryDto>> ListOrdersAsync(CancellationToken cancellationToken = default);

    Task<SupplierOrderDto> GetOrderAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    Task<SupplierOrderDto> AcknowledgeOrderAsync(Guid purchaseOrderId, AcknowledgeOrderRequest request, CancellationToken cancellationToken = default);

    Task<SupplierOrderDto> SubmitShipNoticeAsync(Guid purchaseOrderId, ShipNoticeRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierInvoiceDto>> ListInvoicesAsync(CancellationToken cancellationToken = default);

    /// <summary>Lands as a draft AP bill for accounts payable to check (three-way match) and submit. The PDF is required.</summary>
    Task<SupplierInvoiceDto> SubmitInvoiceAsync(SupplierInvoiceRequest request, string fileName, Stream content, CancellationToken cancellationToken = default);
}

// ---- Buyers' side ----

public sealed record SupplierContactDto(
    Guid Id,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    string Email,
    string Name,
    bool IsActive,
    bool HasPassword,
    bool IsLockedOut,
    DateTime? LastSignInUtc,
    string InvitedBy,
    DateTime CreatedAtUtc);

public sealed record SaveSupplierContactRequest(Guid VendorId, string Email, string Name, bool SendSignInLink = true);

public sealed record SupplierPortalEventDto(DateTime OccurredAtUtc, string Actor, string? VendorName, string Action, string? Target, string Outcome, string? Detail);

public sealed record AcknowledgementLineDto(int LineNumber, string Description, decimal OrderedQuantity, decimal OrderedUnitPrice, DateOnly? NeededBy, DateOnly? PromisedDate, decimal? Quantity, decimal? UnitPrice, string? Note);

public sealed record AcknowledgementDto(
    Guid Id,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string VendorName,
    int Revision,
    AcknowledgementState Status,
    string? Note,
    SupplierChannel Channel,
    string AcknowledgedBy,
    DateTime AcknowledgedAtUtc,
    string? ReviewedBy,
    IReadOnlyList<AcknowledgementLineDto> Lines);

public sealed record ShipNoticeLineDto(Guid PurchaseOrderLineId, int LineNumber, string Description, decimal Quantity);

public sealed record ShipNoticeDto(
    Guid Id,
    string Number,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string VendorName,
    string? ShipmentReference,
    DateOnly ShipDate,
    DateOnly? ExpectedDelivery,
    string? Carrier,
    string? TrackingNumber,
    ShipNoticeState Status,
    SupplierChannel Channel,
    string SubmittedBy,
    DateTime SubmittedAtUtc,
    string? ReceiptNumber,
    IReadOnlyList<ShipNoticeLineDto> Lines);

public interface ISupplierPortalAdminService
{
    Task<IReadOnlyList<SupplierContactDto>> ListContactsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default);

    /// <summary>Adds a portal contact (an email can belong to one vendor only) and, by default, emails a sign-in link.</summary>
    Task<SupplierContactDto> AddContactAsync(SaveSupplierContactRequest request, CancellationToken cancellationToken = default);

    Task<SupplierContactDto> SendSignInLinkAsync(Guid contactId, CancellationToken cancellationToken = default);

    /// <summary>Ends the contact's portal sessions at once and stops them signing in.</summary>
    Task<SupplierContactDto> DeactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<SupplierContactDto> ReactivateContactAsync(Guid contactId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SupplierPortalEventDto>> ListActivityAsync(Guid? vendorId = null, int take = 200, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AcknowledgementDto>> ListAcknowledgementsAsync(Guid? purchaseOrderId = null, bool needsReviewOnly = false, CancellationToken cancellationToken = default);

    Task<AcknowledgementDto> MarkAcknowledgementReviewedAsync(Guid acknowledgementId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShipNoticeDto>> ListShipNoticesAsync(Guid? purchaseOrderId = null, bool inTransitOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Receives the shipment's quantities against the purchase order (a normal receipt) and marks the notice received.</summary>
    Task<ShipNoticeDto> ReceiveShipNoticeAsync(Guid shipNoticeId, DateOnly receivedOn, CancellationToken cancellationToken = default);
}

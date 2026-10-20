namespace Workbench.Contracts.Materials;

// Sales orders and shipping (Costpoint Sales Order Entry and Shipping). Enums mirror Workbench.Domain.Materials by value.
// Orders: Materials, Finance and Admin change them; Business development reads them. Fulfilment (pick, pack, ship) is
// Materials, Finance and Admin; creating the delivery invoice is Finance and Admin.

public enum SalesOrderState { Draft, Released, PartlyShipped, Shipped, Closed, Cancelled }

public enum GovernmentInspectionPoint { Origin, Destination }

public enum PickListState { Open, Packed, Cancelled }

public enum ShipmentState { Packing, Confirmed, Cancelled }

public enum WawfDocument { ReceivingReport, Combo, TwoInOne }

public enum WawfReportState { Exported, Submitted, Accepted, Rejected }

// ---- Sales orders ----

public sealed record SalesOrderSummaryDto(
    Guid Id,
    string Number,
    Guid CustomerId,
    string CustomerName,
    Guid? ContractId,
    string? ContractNumber,
    Guid? ProjectId,
    string? ProjectCode,
    string? CustomerReference,
    DateOnly OrderDate,
    DateOnly? NextNeedDate,
    SalesOrderState Status,
    bool OnHold,
    bool OnCreditHold,
    decimal Total,
    decimal ShippedValue,
    int LineCount);

public sealed record SalesOrderLineDto(
    Guid Id,
    int LineNumber,
    Guid PartId,
    string PartNumber,
    string PartDescription,
    string UnitOfMeasure,
    PartTracking Tracking,
    Guid? ClinId,
    string? ItemNumber,
    string Description,
    decimal QuantityOrdered,
    decimal QuantityShipped,
    decimal QuantityOnPickLists,
    decimal QuantityOpen,
    decimal UnitPrice,
    decimal Amount,
    DateOnly NeedDate,
    string? ShipToName,
    string? ShipToAddress,
    string? ShipToCode,
    string? StockNumber,
    bool UidRequired,
    bool IsClosed,

    /// <summary>Cost-type work: the shipment is charged to the order's WBS element instead of cost of goods sold.</summary>
    bool ChargesWbs,
    decimal Available);

public sealed record SalesOrderDto(
    SalesOrderSummaryDto Summary,
    Guid? WbsElementId,
    string? WbsCode,
    string? HoldReason,
    string? ShipToName,
    string? ShipToAddress,
    string? ShipToCode,
    string? MarkForCode,
    string? IssuedByCode,
    string? InspectByCode,
    string? AcceptByCode,
    GovernmentInspectionPoint InspectionPoint,
    GovernmentInspectionPoint AcceptancePoint,
    string? Notes,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? ReleasedBy,
    DateTime? ReleasedAtUtc,
    string? CancelReason,
    IReadOnlyList<SalesOrderLineDto> Lines,
    IReadOnlyList<PickListSummaryDto> PickLists,
    IReadOnlyList<ShipmentSummaryDto> Shipments);

public sealed record SaveSalesOrderLineRequest(
    Guid PartId,
    decimal Quantity,
    decimal UnitPrice,
    DateOnly NeedDate,
    string? Description = null,
    Guid? ClinId = null,
    string? ItemNumber = null,
    string? ShipToName = null,
    string? ShipToAddress = null,
    string? ShipToCode = null,
    string? StockNumber = null,

    /// <summary>Null: UID-marked when the part is serial tracked and the unit price is $5,000 or more (DFARS 252.211-7003).</summary>
    bool? UidRequired = null);

/// <summary>Header fields can change until the order ships in full; lines only while it's a draft.</summary>
public sealed record SaveSalesOrderRequest(
    Guid CustomerId,
    DateOnly OrderDate,
    IReadOnlyList<SaveSalesOrderLineRequest> Lines,
    Guid? ContractId = null,
    Guid? ProjectId = null,
    Guid? WbsElementId = null,
    string? CustomerReference = null,
    string? ShipToName = null,
    string? ShipToAddress = null,
    string? ShipToCode = null,
    string? MarkForCode = null,
    string? IssuedByCode = null,
    string? InspectByCode = null,
    string? AcceptByCode = null,
    GovernmentInspectionPoint InspectionPoint = GovernmentInspectionPoint.Origin,
    GovernmentInspectionPoint AcceptancePoint = GovernmentInspectionPoint.Origin,
    string? Notes = null);

/// <summary>One CLIN to add as a line: its number and description carry over; the unit price defaults to the CLIN value ÷ quantity.</summary>
public sealed record ClinLineRequest(Guid ClinId, Guid PartId, decimal Quantity, decimal? UnitPrice = null, DateOnly? NeedDate = null);

public sealed record AddClinLinesRequest(IReadOnlyList<ClinLineRequest> Lines);

public sealed record SetHoldRequest(bool OnHold, bool OnCreditHold, string? Reason = null);

public sealed record CancelSalesOrderRequest(string Reason);

public sealed record SalesOrderQuery(SalesOrderState? Status = null, Guid? CustomerId = null, Guid? ContractId = null, bool OpenOnly = false, string? Search = null);

public sealed record SalesClinOption(Guid Id, Guid ContractId, string Number, string Description, string Pricing, bool IsCostType, decimal CeilingValue, DateOnly PeriodEnd);

public sealed record SalesContractOption(Guid Id, string Number, string Name, Guid CustomerId, string Type, IReadOnlyList<SalesClinOption> Clins);

public sealed record SalesPartOption(Guid Id, string Number, string Description, string UnitOfMeasure, PartTracking Tracking, decimal StandardCost);

/// <summary>Pickers for the sales order pages.</summary>
public sealed record SalesOptionsDto(
    IReadOnlyList<MaterialOption> Customers,
    IReadOnlyList<SalesContractOption> Contracts,
    IReadOnlyList<MaterialOption> Projects,
    IReadOnlyList<MaterialChargeOption> ChargeCodes,
    IReadOnlyList<SalesPartOption> Parts);

public interface ISalesOrderService
{
    Task<IReadOnlyList<SalesOrderSummaryDto>> ListAsync(SalesOrderQuery query, CancellationToken cancellationToken = default);

    Task<SalesOrderDto> GetAsync(Guid salesOrderId, CancellationToken cancellationToken = default);

    Task<SalesOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default);

    Task<SalesOrderDto> CreateAsync(SaveSalesOrderRequest request, CancellationToken cancellationToken = default);

    Task<SalesOrderDto> UpdateAsync(Guid salesOrderId, SaveSalesOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Adds a line per CLIN of the order's contract (draft orders).</summary>
    Task<SalesOrderDto> AddLinesFromClinsAsync(Guid salesOrderId, AddClinLinesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Releases a draft: its open lines become MRP demand and can be picked. Refused while on hold.</summary>
    Task<SalesOrderDto> ReleaseAsync(Guid salesOrderId, CancellationToken cancellationToken = default);

    Task<SalesOrderDto> SetHoldAsync(Guid salesOrderId, SetHoldRequest request, CancellationToken cancellationToken = default);

    /// <summary>Closes the order (short-closing any open quantity); open pick lists are cancelled.</summary>
    Task<SalesOrderDto> CloseAsync(Guid salesOrderId, CancellationToken cancellationToken = default);

    /// <summary>Cancels an order nothing has shipped on.</summary>
    Task<SalesOrderDto> CancelAsync(Guid salesOrderId, CancelSalesOrderRequest request, CancellationToken cancellationToken = default);
}

// ---- Pick lists ----

public sealed record PickListLineDto(
    Guid Id,
    Guid SalesOrderLineId,
    int OrderLineNumber,
    Guid PartId,
    string PartNumber,
    string PartDescription,
    string UnitOfMeasure,
    Guid WarehouseId,
    string Warehouse,
    Guid BinId,
    string Bin,
    string Lot,
    string Serial,
    StockOwnership Ownership,
    string? OwnerProjectCode,
    decimal Quantity,
    decimal QuantityPicked,
    string? PickedBy,
    DateTime? PickedAtUtc)
{
    public bool IsPicked => PickedAtUtc is not null;
}

public sealed record PickListSummaryDto(Guid Id, string Number, Guid SalesOrderId, string SalesOrderNumber, string CustomerName, PickListState Status, int LineCount, int PickedCount, DateTime CreatedAtUtc);

public sealed record PickListDto(PickListSummaryDto Summary, IReadOnlyList<PickListLineDto> Lines);

/// <summary>Stock eligible for an order line (its project's contract stock and GFM, and general stock), less what open pick lists hold.</summary>
public sealed record PickableStockDto(
    Guid BalanceId,
    Guid WarehouseId,
    string Warehouse,
    Guid BinId,
    string Bin,
    string Lot,
    string Serial,
    StockOwnership Ownership,
    string? OwnerProjectCode,
    decimal OnHand,
    decimal Reserved,
    decimal Available,
    DateOnly ReceivedOn);

/// <summary>Pick a quantity for an order line, from one location when <see cref="BalanceId"/> is given (otherwise allocated oldest first).</summary>
public sealed record PickLineRequest(Guid SalesOrderLineId, decimal Quantity, Guid? BalanceId = null);

/// <summary>Lines to pick; null picks every open line in full from what's available.</summary>
public sealed record CreatePickListRequest(IReadOnlyList<PickLineRequest>? Lines = null);

/// <summary>The picker's count. A different lot or serial from the same bin can be picked instead.</summary>
public sealed record ConfirmPickRequest(decimal QuantityPicked, string? Lot = null, string? Serial = null);

// ---- Shipments ----

public sealed record ShipmentLineDto(
    Guid Id,
    Guid SalesOrderLineId,
    int OrderLineNumber,
    string? ItemNumber,
    Guid PartId,
    string PartNumber,
    string PartDescription,
    string UnitOfMeasure,
    string Location,
    string Lot,
    string Serial,
    StockOwnership Ownership,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    int? PackageNumber,
    decimal UnitCost,
    decimal CostAmount,
    bool ChargedToWbs,
    string? InventoryTransactionNumber);

public sealed record ShipmentPackageDto(Guid Id, int Number, decimal Weight, decimal? Length, decimal? Width, decimal? Height, string? TrackingNumber);

public sealed record ShipmentSummaryDto(
    Guid Id,
    string Number,
    string? ShipmentNumber,
    Guid SalesOrderId,
    string SalesOrderNumber,
    string CustomerName,
    string? ContractNumber,
    ShipmentState Status,
    DateOnly? ShipDate,
    string? Carrier,
    string? TrackingNumber,
    int PackageCount,
    decimal TotalWeight,
    decimal Value,
    bool IsFinal,
    Guid? InvoiceId,
    string? InvoiceNumber);

public sealed record WawfReportDto(
    Guid Id,
    WawfDocument DocumentType,
    WawfReportState Status,
    string FileName,
    string? Reference,
    string? Message,
    string ExportedBy,
    DateTime ExportedAtUtc,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc);

public sealed record ShipmentDto(
    ShipmentSummaryDto Summary,
    string? BillOfLading,
    string? ShipToName,
    string? ShipToAddress,
    string? ShipToCode,
    string? Notes,
    string? JournalEntryNumber,
    string CreatedBy,
    DateTime CreatedAtUtc,
    string? ConfirmedBy,
    DateTime? ConfirmedAtUtc,
    IReadOnlyList<ShipmentLineDto> Lines,
    IReadOnlyList<ShipmentPackageDto> Packages,
    WawfReportDto? Wawf,

    /// <summary>Lines on firm-fixed-price CLINs, which a delivery invoice can bill.</summary>
    bool CanInvoice);

/// <summary>Packs the picked lines of open pick lists (of one order) into a new shipment.</summary>
public sealed record CreateShipmentRequest(IReadOnlyList<Guid> PickListIds);

public sealed record SavePackageRequest(int Number, decimal Weight, decimal? Length = null, decimal? Width = null, decimal? Height = null, string? TrackingNumber = null);

public sealed record PackLineRequest(Guid ShipmentLineId, int? PackageNumber);

/// <summary>Carrier details, packages and what's in each (replaces the packages).</summary>
public sealed record SaveShipmentRequest(
    DateOnly? ShipDate,
    string? Carrier,
    string? BillOfLading,
    string? TrackingNumber,
    bool IsFinal,
    IReadOnlyList<SavePackageRequest> Packages,
    IReadOnlyList<PackLineRequest> LinePackages,
    string? Notes = null);

public sealed record ConfirmShipmentRequest(DateOnly ShipDate);

public sealed record ShipmentQuery(ShipmentState? Status = null, Guid? SalesOrderId = null, DateOnly? From = null, DateOnly? To = null, string? Search = null, int Take = 500);

/// <summary>A file to download (the bytes go over the API as base64).</summary>
public sealed record ShippingFile(string FileName, string ContentType, byte[] Content);

// ---- DD Form 250 ----

public sealed record Dd250PartyDto(string? Code, string Name, string? Address);

public sealed record Dd250ItemDto(
    string ItemNumber,
    string? StockNumber,
    string PartNumber,
    string Description,
    IReadOnlyList<string> Serials,
    IReadOnlyList<string> Lots,
    decimal Quantity,
    string Unit,
    decimal UnitPrice,
    decimal Amount,
    bool GovernmentFurnished,
    bool UidMarked);

/// <summary>Everything on a DD Form 250 Material Inspection and Receiving Report, by block.</summary>
public sealed record Dd250Dto(
    Guid ShipmentId,
    string ContractNumber,
    string? DeliveryOrder,
    string ShipmentNumber,
    DateOnly? DateShipped,
    string? BillOfLading,
    string? DiscountTerms,
    string? InvoiceNumber,
    DateOnly? InvoiceDate,
    GovernmentInspectionPoint InspectionPoint,
    GovernmentInspectionPoint AcceptancePoint,
    Dd250PartyDto PrimeContractor,
    Dd250PartyDto AdministeredBy,
    Dd250PartyDto ShippedFrom,
    Dd250PartyDto PaymentBy,
    Dd250PartyDto ShippedTo,
    Dd250PartyDto? MarkedFor,
    string? Carrier,
    int PackageCount,
    decimal TotalWeight,
    IReadOnlyList<Dd250ItemDto> Items,
    decimal Total,
    bool IsFinal,
    bool IsConfirmed,
    IReadOnlyList<string> Warnings);

// ---- WAWF ----

public sealed record WawfValidationDto(WawfDocument DocumentType, IReadOnlyList<string> Errors, IReadOnlyList<string> Warnings)
{
    public bool IsValid => Errors.Count == 0;
}

public sealed record SetWawfStatusRequest(WawfReportState Status, string? Reference = null, string? Message = null);

public interface IShippingService
{
    // Picking
    Task<IReadOnlyList<PickableStockDto>> ListPickableStockAsync(Guid salesOrderLineId, CancellationToken cancellationToken = default);

    Task<PickListDto> CreatePickListAsync(Guid salesOrderId, CreatePickListRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<PickListSummaryDto>> ListPickListsAsync(bool openOnly = true, CancellationToken cancellationToken = default);

    Task<PickListDto> GetPickListAsync(Guid pickListId, CancellationToken cancellationToken = default);

    Task<PickListDto> ConfirmPickAsync(Guid pickListLineId, ConfirmPickRequest request, CancellationToken cancellationToken = default);

    Task<PickListDto> CancelPickListAsync(Guid pickListId, CancellationToken cancellationToken = default);

    // Packing and shipping
    Task<ShipmentDto> CreateShipmentAsync(CreateShipmentRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ShipmentSummaryDto>> ListShipmentsAsync(ShipmentQuery query, CancellationToken cancellationToken = default);

    Task<ShipmentDto> GetShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default);

    Task<ShipmentDto> SaveShipmentAsync(Guid shipmentId, SaveShipmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Ships it: issues the inventory (cost of goods sold, or the order's WBS element for cost-type work), updates the order and numbers the DD250.</summary>
    Task<ShipmentDto> ConfirmShipmentAsync(Guid shipmentId, ConfirmShipmentRequest request, CancellationToken cancellationToken = default);

    /// <summary>Cancels a shipment still being packed; its pick lists open again.</summary>
    Task<ShipmentDto> CancelShipmentAsync(Guid shipmentId, CancellationToken cancellationToken = default);

    // Government paperwork and billing
    Task<Dd250Dto> GetDd250Async(Guid shipmentId, CancellationToken cancellationToken = default);

    Task<ShippingFile> GetDd250PdfAsync(Guid shipmentId, CancellationToken cancellationToken = default);

    Task<WawfValidationDto> ValidateWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default);

    /// <summary>The WAWF receiving report (or Combo / 2-in-1) import file. Refused while validation finds errors.</summary>
    Task<ShippingFile> ExportWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default);

    /// <summary>Hands the file to the configured submitter (the built-in one records it as submitted by hand; nothing is sent).</summary>
    Task<ShipmentDto> SubmitWawfAsync(Guid shipmentId, WawfDocument documentType, CancellationToken cancellationToken = default);

    Task<ShipmentDto> SetWawfStatusAsync(Guid shipmentId, SetWawfStatusRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates the draft delivery invoice for a confirmed shipment's firm-fixed-price lines through billing.</summary>
    Task<ShipmentDto> CreateInvoiceAsync(Guid shipmentId, CancellationToken cancellationToken = default);
}

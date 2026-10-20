namespace Workbench.Contracts.Materials;

// Enums mirror Workbench.Domain.Materials by value.
public enum PartSourceKind { Buy, Make }

public enum PartCostMethod { Average, Standard }

public enum PartTracking { None, Lot, Serial }

public enum StockOwnership { General, Contract, GovernmentFurnished }

public enum InventoryMovement
{
    PurchaseReceipt,
    GovernmentReceipt,
    IssueToProject,
    IssueToWorkOrder,
    ReturnFromWorkOrder,
    TransferOut,
    TransferIn,
    Adjustment,
    CycleCount,
    WorkOrderCompletion,
    WorkOrderShipment,
    Scrap,
    Revaluation,
    StatusChange,
    VendorReturn,
    InventoryScrap,
}

/// <summary>Whether stock can be used (mirrors Domain InventoryStockStatus). Only available stock is issued or planned by MRP.</summary>
public enum StockStatus { Available, InspectionHold, Nonconforming }

public enum WorkOrderState { Planned, Released, InProgress, Completed, Closed, Cancelled }

public enum PlannedOrderType { Buy, Make }

public enum PlannedOrderState { Planned, Firmed, Superseded }

// ---- Parts, warehouses, demand ----

public sealed record PartSummaryDto(
    Guid Id,
    string Number,
    string Revision,
    string Description,
    string UnitOfMeasure,
    PartSourceKind Source,
    PartTracking Tracking,
    PartCostMethod CostMethod,
    decimal StandardCost,
    decimal AverageCost,
    decimal OnHand,
    decimal OnHandGovernment,
    decimal Value,
    string? PreferredVendor,
    int LeadTimeDays,
    bool HasBom,
    bool IsActive)
{
    public string Display => Revision.Length == 0 ? Number : $"{Number} rev {Revision}";
}

public sealed record WhereUsedDto(int Level, Guid ParentPartId, string ParentNumber, string ParentRevision, string ParentDescription, string BomRevision, bool BomActive, decimal QuantityPer);

public sealed record PartDto(
    PartSummaryDto Summary,
    Guid? PreferredVendorId,
    decimal SafetyStock,
    decimal MinimumOrderQuantity,
    decimal OrderMultiple,
    Guid? DefaultWarehouseId,
    Guid? DefaultBinId,
    string? DefaultLocation,
    string? Notes,
    decimal OnOrder,
    decimal Allocated,
    IReadOnlyList<InventoryBalanceDto> Balances,
    IReadOnlyList<InventoryTransactionDto> RecentTransactions,
    IReadOnlyList<WhereUsedDto> WhereUsed);

public sealed record SavePartRequest(
    string Number,
    string? Revision,
    string Description,
    string UnitOfMeasure,
    PartSourceKind Source,
    PartTracking Tracking,
    PartCostMethod CostMethod,
    decimal StandardCost,
    Guid? PreferredVendorId = null,
    int LeadTimeDays = 0,
    decimal SafetyStock = 0,
    decimal MinimumOrderQuantity = 0,
    decimal OrderMultiple = 0,
    Guid? DefaultWarehouseId = null,
    Guid? DefaultBinId = null,
    string? Notes = null,
    bool IsActive = true,

    /// <summary>Average-cost parts without stock may set their starting average (otherwise the standard cost).</summary>
    decimal? AverageCost = null);

public sealed record PartQuery(string? Search = null, bool ActiveOnly = true, PartSourceKind? Source = null);

public sealed record BinDto(Guid Id, Guid WarehouseId, string Code, string? Description, bool IsActive);

public sealed record WarehouseDto(Guid Id, string Code, string Name, string? Address, bool IsActive, IReadOnlyList<BinDto> Bins);

public sealed record SaveWarehouseRequest(string Code, string Name, string? Address = null, bool IsActive = true);

public sealed record SaveBinRequest(string Code, string? Description = null, bool IsActive = true);

public sealed record ForecastDto(Guid Id, Guid PartId, string PartNumber, DateOnly Date, decimal Quantity, string? Note);

public sealed record SaveForecastRequest(Guid PartId, DateOnly Date, decimal Quantity, string? Note = null);

public sealed record MaterialRequirementDto(
    Guid Id, Guid ProjectId, string ProjectCode, Guid WbsElementId, string WbsCode, Guid PartId, string PartNumber, string PartDescription,
    decimal Quantity, decimal QuantityIssued, decimal OpenQuantity, DateOnly NeededBy, string? Note, bool IsClosed);

public sealed record SaveMaterialRequirementRequest(Guid WbsElementId, Guid PartId, decimal Quantity, DateOnly NeededBy, string? Note = null);

public sealed record MaterialOption(Guid Id, string Code, string Name);

public sealed record MaterialChargeOption(Guid WbsElementId, Guid ProjectId, string ProjectCode, string WbsCode, string Name);

/// <summary>Pickers for the materials pages.</summary>
public sealed record MaterialsOptionsDto(
    IReadOnlyList<MaterialOption> Vendors,
    IReadOnlyList<MaterialOption> Projects,
    IReadOnlyList<MaterialChargeOption> ChargeCodes,
    IReadOnlyList<MaterialChargeOption> IndirectChargeCodes,
    IReadOnlyList<WarehouseDto> Warehouses,
    IReadOnlyList<MaterialOption> LaborCategories,
    IReadOnlyList<MaterialOption> WorkCenters,
    IReadOnlyList<MaterialOption> Employees,
    IReadOnlyList<MaterialOption> OrgUnits);

/// <summary>Part master, warehouses and bins, forecasts and project material requirements. Reads: Materials, Finance, Admin; changes: Materials, Admin.</summary>
public interface IPartService
{
    Task<IReadOnlyList<PartSummaryDto>> ListAsync(PartQuery query, CancellationToken cancellationToken = default);

    Task<PartDto> GetAsync(Guid partId, CancellationToken cancellationToken = default);

    Task<PartDto> CreateAsync(SavePartRequest request, CancellationToken cancellationToken = default);

    /// <summary>The standard cost of a part with stock changes only through <see cref="IInventoryService.ChangeStandardCostAsync"/>.</summary>
    Task<PartDto> UpdateAsync(Guid partId, SavePartRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WarehouseDto>> ListWarehousesAsync(CancellationToken cancellationToken = default);

    Task<WarehouseDto> SaveWarehouseAsync(Guid? warehouseId, SaveWarehouseRequest request, CancellationToken cancellationToken = default);

    Task<WarehouseDto> SaveBinAsync(Guid warehouseId, Guid? binId, SaveBinRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ForecastDto>> ListForecastsAsync(Guid? partId = null, CancellationToken cancellationToken = default);

    Task<ForecastDto> AddForecastAsync(SaveForecastRequest request, CancellationToken cancellationToken = default);

    Task DeleteForecastAsync(Guid forecastId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<MaterialRequirementDto>> ListRequirementsAsync(Guid? projectId = null, bool openOnly = true, CancellationToken cancellationToken = default);

    Task<MaterialRequirementDto> AddRequirementAsync(SaveMaterialRequirementRequest request, CancellationToken cancellationToken = default);

    Task<MaterialRequirementDto> CloseRequirementAsync(Guid requirementId, CancellationToken cancellationToken = default);

    Task<MaterialsOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default);
}

// ---- Inventory ----

public sealed record InventoryBalanceDto(
    Guid Id,
    Guid PartId,
    string PartNumber,
    string PartRevision,
    string PartDescription,
    string UnitOfMeasure,
    Guid WarehouseId,
    string Warehouse,
    Guid BinId,
    string Bin,
    string Lot,
    string Serial,
    StockOwnership Ownership,
    Guid? OwnerProjectId,
    string? OwnerProjectCode,
    decimal Quantity,
    decimal UnitCost,
    decimal Value,
    DateOnly ReceivedOn,
    string? GovernmentReference,

    /// <summary>Added by quality: held and nonconforming stock is listed but can't be issued.</summary>
    StockStatus Status = StockStatus.Available)
{
    public string Location => $"{Warehouse} / {Bin}";
}

public sealed record BalanceQuery(Guid? PartId = null, Guid? WarehouseId = null, StockOwnership? Ownership = null, Guid? ProjectId = null, string? Search = null);

public sealed record InventoryTransactionDto(
    Guid Id,
    string Number,
    InventoryMovement Type,
    DateOnly Date,
    Guid PartId,
    string PartNumber,
    string PartDescription,
    string? Location,
    string Lot,
    string Serial,
    StockOwnership Ownership,
    string? OwnerProjectCode,
    decimal Quantity,
    decimal UnitCost,
    decimal Amount,
    decimal Variance,
    string? ProjectCode,
    string? WbsCode,
    Guid? WorkOrderId,
    string? WorkOrderNumber,
    string? PurchaseOrderNumber,
    string? Reason,
    string? Reference,
    string? JournalEntryNumber,
    string CreatedBy,
    DateTime CreatedAtUtc);

public sealed record TransactionQuery(
    Guid? PartId = null, Guid? WorkOrderId = null, Guid? ProjectId = null, InventoryMovement? Type = null,
    DateOnly? From = null, DateOnly? To = null, string? Search = null, int Take = 200);

/// <summary>An open purchase order line for a part, waiting to be received into inventory.</summary>
public sealed record ReceivablePartLineDto(
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    string Vendor,
    Guid PurchaseOrderLineId,
    int LineNumber,
    string Description,
    Guid PartId,
    string PartNumber,
    PartTracking Tracking,
    decimal Ordered,
    decimal Received,
    decimal Open,
    decimal UnitPrice,
    StockOwnership Ownership,
    string? OwnerProjectCode,
    Guid? WarehouseId,
    Guid? BinId,
    DateOnly? NeededBy);

/// <summary>An open PO line with no part yet, that could be linked (account lines on the receiving clearing account).</summary>
public sealed record LinkablePurchaseLineDto(Guid PurchaseOrderLineId, string PurchaseOrderNumber, int LineNumber, string Description, decimal Quantity, decimal UnitPrice);

public sealed record LinkPurchaseLineRequest(
    Guid PurchaseOrderLineId, Guid PartId, StockOwnership Ownership = StockOwnership.General, Guid? OwnerProjectId = null, Guid? WarehouseId = null, Guid? BinId = null);

public sealed record ReceiveIntoInventoryLine(Guid PurchaseOrderLineId, decimal Quantity, Guid? WarehouseId = null, Guid? BinId = null, string? Lot = null, IReadOnlyList<string>? Serials = null);

public sealed record ReceiveIntoInventoryRequest(Guid PurchaseOrderId, DateOnly ReceivedOn, IReadOnlyList<ReceiveIntoInventoryLine> Lines, string? Note = null);

public sealed record GovernmentReceiptRequest(
    Guid PartId, Guid ProjectId, decimal Quantity, DateOnly ReceivedOn, Guid WarehouseId, Guid BinId, string Reference,
    string? Lot = null, IReadOnlyList<string>? Serials = null);

/// <summary>Issue to a project WBS element. Without <see cref="BalanceId"/>, stock is picked: the project's government-furnished, then its contract, then general stock, oldest first.</summary>
public sealed record IssueToProjectRequest(Guid PartId, decimal Quantity, DateOnly Date, Guid WbsElementId, Guid? BalanceId = null, string? Note = null);

public sealed record TransferRequest(Guid BalanceId, decimal Quantity, DateOnly Date, Guid ToWarehouseId, Guid ToBinId, string? Note = null);

/// <summary>A quantity correction with a reason. Positive adjustments of costed stock come in at <see cref="UnitCost"/> (average-cost parts) or the current cost.</summary>
public sealed record AdjustInventoryRequest(
    Guid PartId, Guid WarehouseId, Guid BinId, decimal QuantityChange, DateOnly Date, string Reason,
    string? Lot = null, string? Serial = null, StockOwnership Ownership = StockOwnership.General, Guid? OwnerProjectId = null, decimal? UnitCost = null);

public sealed record CycleCountRequest(Guid BalanceId, decimal CountedQuantity, DateOnly Date, string? Note = null);

public sealed record ChangeStandardCostRequest(decimal NewStandardCost, DateOnly Date, string Reason);

public sealed record TraceNodeDto(
    Guid TransactionId, string Number, InventoryMovement Type, DateOnly Date, string PartNumber, string PartDescription, string Lot, string Serial,
    decimal Quantity, string? Document);

/// <summary>Everything that happened to a lot or serial, what went into it and what it went into.</summary>
public sealed record TraceDto(
    string? Lot, string? Serial, IReadOnlyList<InventoryTransactionDto> History, IReadOnlyList<TraceNodeDto> MadeFrom, IReadOnlyList<TraceNodeDto> UsedIn);

/// <summary>Inventory movements. Reads: Materials, Finance, Admin; movements: Materials, Admin; standard cost revaluation: Finance, Admin.</summary>
public interface IInventoryService
{
    Task<IReadOnlyList<InventoryBalanceDto>> ListBalancesAsync(BalanceQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryTransactionDto>> ListTransactionsAsync(TransactionQuery query, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReceivablePartLineDto>> ListReceivableLinesAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LinkablePurchaseLineDto>> ListLinkableLinesAsync(CancellationToken cancellationToken = default);

    Task LinkPurchaseOrderLineAsync(LinkPurchaseLineRequest request, CancellationToken cancellationToken = default);

    /// <summary>Receives through purchasing (the PO receipt and three-way match are unchanged) and puts the goods into stock.</summary>
    Task<IReadOnlyList<InventoryTransactionDto>> ReceivePurchaseOrderAsync(ReceiveIntoInventoryRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryTransactionDto>> ReceiveGovernmentFurnishedAsync(GovernmentReceiptRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryTransactionDto>> IssueToProjectAsync(IssueToProjectRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InventoryTransactionDto>> TransferAsync(TransferRequest request, CancellationToken cancellationToken = default);

    Task<InventoryTransactionDto> AdjustAsync(AdjustInventoryRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records the count; a difference posts as a cycle count adjustment (nothing when it matches).</summary>
    Task<InventoryTransactionDto?> CycleCountAsync(CycleCountRequest request, CancellationToken cancellationToken = default);

    Task<InventoryTransactionDto> ChangeStandardCostAsync(Guid partId, ChangeStandardCostRequest request, CancellationToken cancellationToken = default);

    Task<TraceDto> TraceAsync(string? lot, string? serial, Guid? partId = null, CancellationToken cancellationToken = default);
}

// ---- BOMs, routings, work orders ----

public sealed record BomComponentDto(
    Guid Id, int Sequence, Guid ComponentPartId, string PartNumber, string PartRevision, string PartDescription, string UnitOfMeasure, PartSourceKind Source,
    decimal QuantityPer, decimal ScrapPercent, DateOnly? EffectiveFrom, DateOnly? EffectiveTo, string? ReferenceDesignators, int? OperationSequence, decimal UnitCost);

public sealed record BomSummaryDto(Guid Id, Guid PartId, string PartNumber, string PartRevision, string PartDescription, string Revision, bool IsActive, int ComponentCount, DateTime UpdatedAtUtc);

public sealed record BomDto(BomSummaryDto Summary, string? Notes, IReadOnlyList<BomComponentDto> Components);

public sealed record SaveBomComponentRequest(
    Guid ComponentPartId, decimal QuantityPer, decimal ScrapPercent = 0, DateOnly? EffectiveFrom = null, DateOnly? EffectiveTo = null,
    string? ReferenceDesignators = null, int? OperationSequence = null);

public sealed record SaveBomRequest(Guid PartId, string Revision, IReadOnlyList<SaveBomComponentRequest> Components, string? Notes = null);

/// <summary>One row of an indented (multi-level) bill: level 1 are the top part's components.</summary>
public sealed record BomExplosionRowDto(
    int Level, Guid PartId, string PartNumber, string PartRevision, string PartDescription, string UnitOfMeasure, PartSourceKind Source,
    decimal QuantityPer, decimal ExtendedQuantity, decimal UnitCost, decimal ExtendedCost);

public sealed record WorkCenterDto(Guid Id, string Code, string Name, Guid? LaborCategoryId, string? LaborCategory, decimal HoursPerDay, bool IsActive);

public sealed record SaveWorkCenterRequest(string Code, string Name, Guid? LaborCategoryId = null, decimal HoursPerDay = 8, bool IsActive = true);

public sealed record RoutingOperationDto(Guid Id, int Sequence, string Description, Guid WorkCenterId, string WorkCenter, Guid? LaborCategoryId, string? LaborCategory, decimal SetupHours, decimal RunHoursPerUnit);

public sealed record RoutingDto(Guid Id, Guid PartId, string PartNumber, string Revision, bool IsActive, IReadOnlyList<RoutingOperationDto> Operations);

public sealed record SaveRoutingOperationRequest(int Sequence, string Description, Guid WorkCenterId, decimal SetupHours, decimal RunHoursPerUnit, Guid? LaborCategoryId = null);

public sealed record SaveRoutingRequest(Guid PartId, string Revision, IReadOnlyList<SaveRoutingOperationRequest> Operations, bool Activate = true);

public sealed record WorkOrderSummaryDto(
    Guid Id, string Number, Guid PartId, string PartNumber, string PartDescription, decimal Quantity, decimal QuantityCompleted, decimal QuantityScrapped,
    WorkOrderState Status, string? ProjectCode, string? WbsCode, DateOnly StartDate, DateOnly DueDate, bool IsLate);

public sealed record WorkOrderComponentDto(
    Guid Id, int Sequence, Guid PartId, string PartNumber, string PartDescription, string UnitOfMeasure, PartTracking Tracking,
    decimal QuantityPer, decimal QuantityRequired, decimal QuantityIssued, decimal QuantityOpen, decimal Available, int? OperationSequence);

public sealed record WorkOrderOperationDto(
    Guid Id, int Sequence, string Description, string WorkCenter, string? LaborCategory, decimal PlannedHours, decimal HoursReported, decimal QuantityCompleted);

public sealed record LaborReportDto(Guid Id, string Operation, string Employee, DateOnly WorkDate, decimal Hours, decimal QuantityCompleted, decimal? Cost, decimal? TimesheetHours, string? Note);

/// <summary>Cost collected on the work order. Labor cost is shown to Finance and administrators only (it reveals cost rates).</summary>
public sealed record WorkOrderCostDto(
    decimal MaterialCost, decimal? LaborCost, decimal ScrapCost, decimal CompletedCost, decimal VarianceCost, decimal? WipBalance, decimal StandardCost, decimal? ActualUnitCost);

public sealed record WorkOrderDto(
    WorkOrderSummaryDto Summary,
    Guid? ProjectId,
    Guid? WbsElementId,
    Guid? LaborChargeWbsElementId,
    string? ChargeCode,
    string? BomRevision,
    string? RoutingRevision,
    string? CompletionLocation,
    PartTracking Tracking,
    string? Notes,
    WorkOrderCostDto Cost,
    IReadOnlyList<WorkOrderComponentDto> Components,
    IReadOnlyList<WorkOrderOperationDto> Operations,
    IReadOnlyList<LaborReportDto> Labor,
    IReadOnlyList<InventoryTransactionDto> Transactions);

public sealed record WorkOrderQuery(WorkOrderState? Status = null, Guid? PartId = null, Guid? ProjectId = null, bool OpenOnly = false, string? Search = null);

/// <summary>A work order for a project (<see cref="WbsElementId"/>) or to stock (none).</summary>
public sealed record SaveWorkOrderRequest(
    Guid PartId, decimal Quantity, DateOnly StartDate, DateOnly DueDate, Guid? WbsElementId = null, Guid? LaborChargeWbsElementId = null,
    Guid? WarehouseId = null, Guid? BinId = null, string? Notes = null);

public sealed record IssueComponentLine(Guid ComponentId, decimal Quantity, Guid? BalanceId = null);

/// <summary>Kits components. With no lines: every open requirement, picked automatically.</summary>
public sealed record IssueComponentsRequest(DateOnly Date, IReadOnlyList<IssueComponentLine>? Lines = null);

public sealed record ReportLaborRequest(Guid OperationId, DateOnly WorkDate, decimal Hours, decimal QuantityCompleted = 0, Guid? EmployeeId = null, string? Note = null);

public sealed record ScrapRequest(decimal Quantity, DateOnly Date, string Reason);

public sealed record CompleteWorkOrderRequest(decimal Quantity, DateOnly Date, Guid? WarehouseId = null, Guid? BinId = null, string? Lot = null, IReadOnlyList<string>? Serials = null);

public sealed record CloseWorkOrderRequest(DateOnly Date);

public sealed record CancelWorkOrderRequest(string Reason);

/// <summary>BOMs, routings and work orders. Reads: Materials, Finance, Admin; changes: Materials, Admin; anyone may report their own labor on an open work order.</summary>
public interface IManufacturingService
{
    Task<IReadOnlyList<BomSummaryDto>> ListBomsAsync(Guid? partId = null, CancellationToken cancellationToken = default);

    Task<BomDto> GetBomAsync(Guid bomId, CancellationToken cancellationToken = default);

    /// <summary>Creates (no id) or replaces the components of a BOM revision. A new part's first revision becomes active.</summary>
    Task<BomDto> SaveBomAsync(Guid? bomId, SaveBomRequest request, CancellationToken cancellationToken = default);

    Task<BomDto> ActivateBomAsync(Guid bomId, CancellationToken cancellationToken = default);

    /// <summary>A new revision with the same components (inactive until activated).</summary>
    Task<BomDto> CopyBomAsync(Guid bomId, string newRevision, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BomExplosionRowDto>> ExplodeAsync(Guid partId, decimal quantity, DateOnly asOf, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WhereUsedDto>> WhereUsedAsync(Guid partId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkCenterDto>> ListWorkCentersAsync(CancellationToken cancellationToken = default);

    Task<WorkCenterDto> SaveWorkCenterAsync(Guid? workCenterId, SaveWorkCenterRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<RoutingDto>> ListRoutingsAsync(Guid partId, CancellationToken cancellationToken = default);

    Task<RoutingDto> SaveRoutingAsync(Guid? routingId, SaveRoutingRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkOrderSummaryDto>> ListWorkOrdersAsync(WorkOrderQuery query, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> GetWorkOrderAsync(Guid workOrderId, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> CreateWorkOrderAsync(SaveWorkOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Copies the active BOM (components effective on the start date) and routing onto the order.</summary>
    Task<WorkOrderDto> ReleaseAsync(Guid workOrderId, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> IssueComponentsAsync(Guid workOrderId, IssueComponentsRequest request, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> ReportLaborAsync(Guid workOrderId, ReportLaborRequest request, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> ScrapAsync(Guid workOrderId, ScrapRequest request, CancellationToken cancellationToken = default);

    /// <summary>Stock orders: finished units into stock. Project orders: delivered to the project.</summary>
    Task<WorkOrderDto> CompleteAsync(Guid workOrderId, CompleteWorkOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Relieves what's left in WIP as the production variance and closes the order.</summary>
    Task<WorkOrderDto> CloseAsync(Guid workOrderId, CloseWorkOrderRequest request, CancellationToken cancellationToken = default);

    Task<WorkOrderDto> CancelAsync(Guid workOrderId, CancelWorkOrderRequest request, CancellationToken cancellationToken = default);
}

// ---- MRP ----

public sealed record PlannedOrderDto(
    Guid Id, Guid PartId, string PartNumber, string PartDescription, PlannedOrderType Kind, decimal Quantity, DateOnly NeedDate, DateOnly ReleaseDate,
    bool IsLate, string? Pegging, string? ProjectCode, PlannedOrderState Status, string? PreferredVendor, decimal UnitCost, string? FirmedDocument);

public sealed record MrpRunDto(Guid Id, DateOnly AsOf, int HorizonDays, DateTime RunAtUtc, string RunBy, int LateCount, IReadOnlyList<string> Messages, IReadOnlyList<PlannedOrderDto> PlannedOrders);

public sealed record RunMrpRequest(int HorizonDays = 120, DateOnly? AsOf = null);

/// <summary>
/// Firms planned orders: buys become one draft-then-submitted requisition per preferred vendor (account lines on the
/// receiving clearing account, charged to <see cref="OrgUnitId"/> or the planner's organization); makes become planned work orders.
/// </summary>
public sealed record FirmPlannedOrdersRequest(IReadOnlyList<Guid> PlannedOrderIds, Guid? OrgUnitId = null, bool SubmitRequisitions = true);

public sealed record FirmResultDto(IReadOnlyList<string> Requisitions, IReadOnlyList<string> WorkOrders);

/// <summary>Material requirements planning. Materials and Admin run and firm; Finance reads.</summary>
public interface IMrpService
{
    Task<MrpRunDto> RunAsync(RunMrpRequest request, CancellationToken cancellationToken = default);

    Task<MrpRunDto?> GetLatestAsync(CancellationToken cancellationToken = default);

    Task<FirmResultDto> FirmAsync(FirmPlannedOrdersRequest request, CancellationToken cancellationToken = default);
}

// ---- Reports ----

public sealed record ValuationRowDto(Guid PartId, string PartNumber, string PartDescription, PartCostMethod CostMethod, decimal Quantity, decimal UnitCost, decimal Value, decimal ContractQuantity, decimal ContractValue);

/// <summary>Inventory at cost (government-furnished excluded) against the GL inventory and WIP accounts.</summary>
public sealed record InventoryValuationDto(IReadOnlyList<ValuationRowDto> Rows, decimal TotalValue, decimal WipValue, decimal GlInventory, decimal GlWip, IReadOnlyList<string> Notes);

public sealed record AgingRowDto(Guid PartId, string PartNumber, string PartDescription, decimal Days0To90, decimal Days91To180, decimal Days181To365, decimal Over365, decimal Total);

public sealed record InventoryAgingDto(DateOnly AsOf, IReadOnlyList<AgingRowDto> Rows, decimal Days0To90, decimal Days91To180, decimal Days181To365, decimal Over365, decimal Total);

public sealed record GfmRowDto(
    string ProjectCode, string ProjectName, string PartNumber, string PartDescription, string UnitOfMeasure, string Location, string Lot, string Serial,
    decimal Quantity, DateOnly ReceivedOn, string? Reference);

public sealed record WipRowDto(
    Guid WorkOrderId, string Number, string PartNumber, WorkOrderState Status, string? ProjectCode, decimal Quantity, decimal QuantityCompleted,
    decimal MaterialCost, decimal LaborCost, decimal ScrapCost, decimal CompletedCost, decimal VarianceCost, decimal WipBalance, bool IsForProject);

public sealed record WipReportDto(IReadOnlyList<WipRowDto> Rows, decimal StockWipBalance, decimal ProjectCollectedCost, decimal GlWip);

public sealed record ProjectMaterialCostRowDto(string WbsCode, string PartNumber, string PartDescription, decimal Quantity, decimal Amount, decimal GovernmentQuantity);

public sealed record ProjectMaterialCostDto(Guid ProjectId, string ProjectCode, IReadOnlyList<ProjectMaterialCostRowDto> Rows, decimal Total);

/// <summary>Valuation, aging, GFM listing and project material cost: Materials, Finance, Admin. WIP (labor cost): Finance, Admin.</summary>
public interface IMaterialReportService
{
    Task<InventoryValuationDto> ValuationAsync(CancellationToken cancellationToken = default);

    Task<InventoryAgingDto> AgingAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<GfmRowDto>> GovernmentFurnishedAsync(Guid? projectId = null, CancellationToken cancellationToken = default);

    Task<WipReportDto> WipAsync(CancellationToken cancellationToken = default);

    Task<ProjectMaterialCostDto> ProjectMaterialCostAsync(Guid projectId, CancellationToken cancellationToken = default);
}

namespace Workbench.Contracts.Materials;

// Kitting, backflushing, landed cost, physical inventory and finite capacity scheduling. Enums mirror Workbench.Domain.Materials by value.

public enum KitState { Open, Staged, Issued, Cancelled }

public enum BackflushWhen { Completion, Operation }

public enum LandedChargeType { Freight, Duty, Handling, Other }

public enum LandedCostBasis { Value, Quantity, Weight }

public enum LandedChargeState { Draft, Posted, Cancelled }

public enum PhysicalInventoryState { Counting, Posted, Cancelled }

public enum CountTagState { NotCounted, Counted, RecountNeeded, Recounted, Void }

public enum ScheduleMode { Forward, Backward }

// ---- Kitting ----

public sealed record KitSummaryDto(
    Guid Id, string Number, KitState Status, Guid? WorkOrderId, string? WorkOrderNumber, Guid? ProjectId, string? ProjectCode, string? WbsCode,
    string StagingLocation, DateOnly NeededBy, int LineCount, decimal QuantityShort, int ShortLines, DateTime CreatedAtUtc);

public sealed record KitLineDto(
    Guid Id, int Sequence, Guid PartId, string PartNumber, string PartDescription, string UnitOfMeasure, PartTracking Tracking, string? WbsCode,
    decimal QuantityRequired, decimal QuantityStaged, decimal QuantityIssued, decimal QuantityShort, decimal Available);

/// <summary>Stock staged for a line: from where, which lot and serial, and whether it's been issued.</summary>
public sealed record KitPickDto(Guid Id, Guid KitLineId, string PartNumber, string FromLocation, string Lot, string Serial, StockOwnership Ownership, decimal Quantity, bool IsIssued);

public sealed record KitDto(KitSummaryDto Summary, Guid StagingWarehouseId, Guid StagingBinId, string? Notes, IReadOnlyList<KitLineDto> Lines, IReadOnlyList<KitPickDto> Picks);

public sealed record KitQuery(bool OpenOnly = true, Guid? WorkOrderId = null, Guid? ProjectId = null);

/// <summary>A kit of a released work order's open components (backflushed components left out unless asked for).</summary>
public sealed record CreateWorkOrderKitRequest(Guid WorkOrderId, Guid StagingBinId, DateOnly? NeededBy = null, bool IncludeBackflushed = false, string? Notes = null);

/// <summary>A kit of a project's open material requirements (one WBS element, or all of them).</summary>
public sealed record CreateProjectKitRequest(Guid ProjectId, Guid StagingBinId, Guid? WbsElementId = null, DateOnly? NeededBy = null, string? Notes = null);

public sealed record StageKitLine(Guid KitLineId, decimal Quantity, Guid? BalanceId = null);

/// <summary>Picks stock into the kit bin. With no lines: everything still short, as much as is available (oldest first).</summary>
public sealed record StageKitRequest(DateOnly Date, IReadOnlyList<StageKitLine>? Lines = null);

public sealed record IssueKitRequest(DateOnly Date);

public sealed record CancelKitRequest(DateOnly Date, string Reason);

/// <summary>Kits: Materials and Admin build, stage, issue and cancel; Finance reads.</summary>
public interface IKittingService
{
    Task<IReadOnlyList<KitSummaryDto>> ListAsync(KitQuery query, CancellationToken cancellationToken = default);

    Task<KitDto> GetAsync(Guid kitId, CancellationToken cancellationToken = default);

    Task<KitDto> CreateForWorkOrderAsync(CreateWorkOrderKitRequest request, CancellationToken cancellationToken = default);

    Task<KitDto> CreateForProjectAsync(CreateProjectKitRequest request, CancellationToken cancellationToken = default);

    Task<KitDto> StageAsync(Guid kitId, StageKitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Issues everything staged to the work order (or the project's WBS elements) in one step. Unstaged shortages stay open on the work order.</summary>
    Task<KitDto> IssueAsync(Guid kitId, IssueKitRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns staged stock to where it was picked from and cancels the kit.</summary>
    Task<KitDto> CancelAsync(Guid kitId, CancelKitRequest request, CancellationToken cancellationToken = default);
}

// ---- Backflushing ----

public sealed record BackflushSettingDto(
    Guid Id, Guid PartId, string PartNumber, string PartDescription, Guid? BomId, string? ParentPartNumber, string? BomRevision,
    bool IsBackflushed, Guid? WarehouseId, Guid? BinId, string? Location);

/// <summary>Turns backflushing on or off for a part (every BOM) or for one BOM revision's use of it (<see cref="BomId"/>).</summary>
public sealed record SaveBackflushSettingRequest(Guid PartId, bool IsBackflushed, Guid? BomId = null, Guid? WarehouseId = null, Guid? BinId = null);

/// <summary>A BOM line and how it's issued: backflushed (from the part or the BOM setting) or kitted by hand.</summary>
public sealed record BackflushComponentDto(
    Guid BomId, Guid ParentPartId, string ParentPartNumber, string BomRevision, bool BomActive, Guid ComponentPartId, string PartNumber, string PartDescription,
    int? OperationSequence, bool IsBackflushed, string Source, string? Location);

public sealed record BackflushShortageDto(
    Guid Id, Guid WorkOrderId, string WorkOrderNumber, Guid PartId, string PartNumber, string PartDescription, decimal QuantityShort,
    BackflushWhen Trigger, DateOnly Date, string? Message, bool IsOpen);

public sealed record BackflushResultDto(IReadOnlyList<InventoryTransactionDto> Issued, IReadOnlyList<BackflushShortageDto> Shortages);

/// <summary>
/// Backflush settings and shortages. Backflushing itself happens inside work order completions and labor reporting.
/// Reads: Materials, Finance, Admin; settings and retries: Materials, Admin.
/// </summary>
public interface IBackflushService
{
    Task<IReadOnlyList<BackflushSettingDto>> ListSettingsAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackflushComponentDto>> ListComponentsAsync(Guid? bomId = null, CancellationToken cancellationToken = default);

    Task<BackflushSettingDto> SaveSettingAsync(SaveBackflushSettingRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a setting (a BOM override falls back to the part's setting).</summary>
    Task DeleteSettingAsync(Guid settingId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<BackflushShortageDto>> ListShortagesAsync(Guid? workOrderId = null, bool openOnly = true, CancellationToken cancellationToken = default);

    /// <summary>Backflushes again what earlier completions couldn't issue (after stock arrived).</summary>
    Task<BackflushResultDto> RetryAsync(Guid workOrderId, DateOnly date, CancellationToken cancellationToken = default);
}

// ---- Landed cost ----

/// <summary>A purchase receipt line in inventory (one inventory receipt transaction) and the landed cost it has.</summary>
public sealed record LandedReceiptLineDto(
    Guid ReceiptTransactionId, Guid PurchaseReceiptId, string ReceiptNumber, string PurchaseOrderNumber, string TransactionNumber, DateOnly ReceivedOn,
    Guid PartId, string PartNumber, string PartDescription, PartCostMethod CostMethod, string Lot, string Serial, decimal Quantity, decimal Value, decimal LandedCost);

public sealed record LandedChargeLineDto(Guid Id, LandedReceiptLineDto Receipt, decimal? Weight, decimal Amount, decimal ToInventory, decimal ToIssued, decimal ToVariance);

public sealed record LandedChargeSummaryDto(
    Guid Id, string Number, LandedChargeType Kind, string Description, DateOnly Date, decimal Amount, LandedCostBasis Method, LandedChargeState Status,
    string? Vendor, string? BillNumber, string CreditAccount, int LineCount, string? JournalEntryNumber);

/// <summary>A landed charge. Drafts show the allocation posting would make now (a preview); posted charges what it made.</summary>
public sealed record LandedChargeDto(LandedChargeSummaryDto Summary, Guid? ApBillLineId, Guid CreditAccountId, Guid? VendorId, IReadOnlyList<LandedChargeLineDto> Lines, IReadOnlyList<string> Notes);

public sealed record LandedChargeTargetRequest(Guid ReceiptTransactionId, decimal? Weight = null);

/// <summary>
/// From an AP bill line (<see cref="ApBillLineId"/>: its amount and account) or entered by hand (<see cref="Amount"/> and
/// <see cref="CreditAccountId"/>, by default the landed cost clearing account).
/// </summary>
public sealed record SaveLandedChargeRequest(
    LandedChargeType Kind, string Description, DateOnly Date, LandedCostBasis Method, IReadOnlyList<LandedChargeTargetRequest> Lines,
    Guid? ApBillLineId = null, decimal? Amount = null, Guid? CreditAccountId = null, Guid? VendorId = null);

/// <summary>An approved or posted AP bill account line not yet allocated as landed cost.</summary>
public sealed record LandedBillLineDto(
    Guid ApBillLineId, Guid BillId, string BillNumber, string Vendor, Guid VendorId, string InvoiceNumber, DateOnly InvoiceDate, int LineNumber, string Description,
    decimal Amount, string Account, bool OnClearingAccount);

public sealed record LandedCostOptionsDto(IReadOnlyList<MaterialOption> Accounts, Guid? ClearingAccountId, IReadOnlyList<MaterialOption> Vendors);

public sealed record ReceiptLandedCostDto(
    Guid PurchaseReceiptId, string ReceiptNumber, string PurchaseOrderNumber, string Vendor, DateOnly ReceivedOn, decimal ReceivedValue, decimal LandedCost,
    IReadOnlyList<LandedReceiptLineDto> Lines, IReadOnlyList<LandedChargeSummaryDto> Charges);

/// <summary>
/// Landed cost. Reads and drafts: Materials, Finance, Admin. Posting (it changes inventory value and posts to the GL): Finance, Admin.
/// </summary>
public interface ILandedCostService
{
    Task<IReadOnlyList<LandedChargeSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<LandedChargeDto> GetAsync(Guid chargeId, CancellationToken cancellationToken = default);

    Task<LandedCostOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default);

    /// <summary>Receipt lines in inventory (newest first) that a charge can cover.</summary>
    Task<IReadOnlyList<LandedReceiptLineDto>> ListReceiptLinesAsync(DateOnly? from = null, string? search = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LandedBillLineDto>> ListBillLinesAsync(CancellationToken cancellationToken = default);

    Task<LandedChargeDto> CreateAsync(SaveLandedChargeRequest request, CancellationToken cancellationToken = default);

    Task<LandedChargeDto> UpdateAsync(Guid chargeId, SaveLandedChargeRequest request, CancellationToken cancellationToken = default);

    Task<LandedChargeDto> PostAsync(Guid chargeId, CancellationToken cancellationToken = default);

    Task<LandedChargeDto> CancelAsync(Guid chargeId, CancellationToken cancellationToken = default);

    /// <summary>Receipts into inventory with their received value and landed cost (newest first).</summary>
    Task<IReadOnlyList<ReceiptLandedCostDto>> ListReceiptsAsync(DateOnly? from = null, CancellationToken cancellationToken = default);

    Task<ReceiptLandedCostDto> GetReceiptAsync(Guid purchaseReceiptId, CancellationToken cancellationToken = default);
}

// ---- Physical inventory ----

public sealed record PhysicalInventorySummaryDto(
    Guid Id, string Number, Guid WarehouseId, string Warehouse, string Scope, PhysicalInventoryState Status, DateOnly CountDate, int TagCount, int CountedCount,
    int RecountsNeeded, decimal VarianceValue, DateTime FrozenAtUtc, string CreatedBy);

public sealed record CountTagDto(
    Guid Id, int TagNumber, Guid BinId, string Bin, Guid PartId, string PartNumber, string PartDescription, string UnitOfMeasure, PartTracking Tracking,
    string Lot, string Serial, StockOwnership Ownership, string? OwnerProjectCode, decimal FrozenQuantity, decimal UnitCost, decimal? CountedQuantity,
    decimal? RecountQuantity, decimal? FinalQuantity, decimal? Variance, decimal? VarianceValue, CountTagState Status, bool IsBlankTag, string? CountedBy, string? Note);

public sealed record PhysicalInventoryDto(
    PhysicalInventorySummaryDto Summary, IReadOnlyList<string> Bins, decimal RecountPercent, decimal RecountValue, bool BlindCount, string? Notes,
    IReadOnlyList<CountTagDto> Tags, string? JournalEntryNumber);

/// <summary>Freezes the warehouse (no bins given) or the bins given and writes a count tag for every balance in them.</summary>
public sealed record CreatePhysicalInventoryRequest(
    Guid WarehouseId, DateOnly CountDate, IReadOnlyList<Guid>? BinIds = null, decimal RecountPercent = 5, decimal RecountValue = 100, bool BlindCount = true, string? Notes = null);

/// <summary>A count by tag number. A tag waiting for a recount takes this as the recount.</summary>
public sealed record EnterCountRequest(int TagNumber, decimal Quantity, string? Note = null);

/// <summary>A blank tag: stock found that isn't on record (or another lot or serial of it).</summary>
public sealed record AddCountTagRequest(
    Guid BinId, Guid PartId, decimal Quantity, string? Lot = null, string? Serial = null, StockOwnership Ownership = StockOwnership.General, Guid? OwnerProjectId = null,
    string? Note = null);

/// <summary>The tag after a count, and what to tell the counter (never the quantity on record: counts are blind).</summary>
public sealed record CountResultDto(int TagNumber, CountTagState Status, string Message);

public sealed record CountSheetFileDto(string FileName, string ContentType, string Content);

/// <summary>
/// Physical inventory. Reads: Materials, Finance, Admin; freeze, count, tags and cancel: Materials, Admin; posting the
/// adjustments (value to the inventory adjustment account): Finance, Admin.
/// </summary>
public interface IPhysicalInventoryService
{
    Task<IReadOnlyList<PhysicalInventorySummaryDto>> ListAsync(bool openOnly = false, CancellationToken cancellationToken = default);

    Task<PhysicalInventoryDto> GetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default);

    Task<PhysicalInventoryDto> CreateAsync(CreatePhysicalInventoryRequest request, CancellationToken cancellationToken = default);

    Task<CountResultDto> EnterCountAsync(Guid physicalInventoryId, EnterCountRequest request, CancellationToken cancellationToken = default);

    Task<CountResultDto> AddTagAsync(Guid physicalInventoryId, AddCountTagRequest request, CancellationToken cancellationToken = default);

    Task<PhysicalInventoryDto> VoidTagAsync(Guid physicalInventoryId, int tagNumber, CancellationToken cancellationToken = default);

    /// <summary>Count sheets as CSV (blind counts leave the quantity on record out).</summary>
    Task<CountSheetFileDto> ExportCountSheetAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default);

    Task<PhysicalInventoryDto> PostAsync(Guid physicalInventoryId, CancellationToken cancellationToken = default);

    Task<PhysicalInventoryDto> CancelAsync(Guid physicalInventoryId, string reason, CancellationToken cancellationToken = default);
}

// ---- Finite capacity scheduling ----

public sealed record CapacityExceptionDto(Guid Id, DateOnly Date, decimal Hours, string? Reason);

public sealed record WorkCenterCalendarDto(
    Guid WorkCenterId, string Code, string Name, string WorkingDays, decimal HoursPerDay, int Units, decimal DailyCapacity, IReadOnlyList<CapacityExceptionDto> Exceptions);

/// <summary><see cref="WorkingDays"/>: seven Y/N characters, Monday first (for example "YYYYYNN").</summary>
public sealed record SaveWorkCenterCalendarRequest(string WorkingDays, decimal HoursPerDay, int Units = 1);

public sealed record SaveCapacityExceptionRequest(DateOnly Date, decimal Hours, string? Reason = null);

/// <summary>Schedules every open operation (released and in-progress work orders) against capacity.</summary>
public sealed record RunScheduleRequest(ScheduleMode Direction = ScheduleMode.Forward, bool Finite = true, DateOnly? AsOf = null);

public sealed record ScheduledOperationDto(
    Guid Id, Guid WorkOrderId, string WorkOrderNumber, string PartNumber, Guid OperationId, int Sequence, string Description, Guid WorkCenterId, string WorkCenter,
    int QueuePosition, DateOnly? Start, DateOnly? End, decimal Hours, DateOnly DueDate, bool IsLate, bool IsPinned, string? Problem);

public sealed record WorkCenterWeekLoadDto(DateOnly WeekStart, decimal Capacity, decimal Load, bool IsOverloaded);

public sealed record WorkCenterLoadDto(Guid WorkCenterId, string Code, string Name, IReadOnlyList<WorkCenterWeekLoadDto> Weeks);

public sealed record CapacityScheduleDto(
    DateTime? RunAtUtc, ScheduleMode Direction, bool Finite, DateOnly From, IReadOnlyList<DateOnly> Weeks, IReadOnlyList<WorkCenterLoadDto> Load,
    IReadOnlyList<ScheduledOperationDto> Operations, int LateCount, IReadOnlyList<string> Messages);

/// <summary>Pins the operation to start on a date (null unpins it), then reschedules.</summary>
public sealed record MoveOperationRequest(DateOnly? PinnedStart);

/// <summary>The work center's queue in the new order (schedule ids), then reschedules.</summary>
public sealed record ResequenceRequest(IReadOnlyList<Guid> ScheduleIds);

/// <summary>Work center calendars and the finite capacity schedule. Reads: Materials, Finance, Admin; changes: Materials, Admin.</summary>
public interface ICapacitySchedulingService
{
    Task<IReadOnlyList<WorkCenterCalendarDto>> ListCalendarsAsync(CancellationToken cancellationToken = default);

    Task<WorkCenterCalendarDto> SaveCalendarAsync(Guid workCenterId, SaveWorkCenterCalendarRequest request, CancellationToken cancellationToken = default);

    Task<WorkCenterCalendarDto> SaveExceptionAsync(Guid workCenterId, SaveCapacityExceptionRequest request, CancellationToken cancellationToken = default);

    Task<WorkCenterCalendarDto> DeleteExceptionAsync(Guid exceptionId, CancellationToken cancellationToken = default);

    Task<CapacityScheduleDto> RunAsync(RunScheduleRequest request, CancellationToken cancellationToken = default);

    /// <summary>The schedule and load chart from <paramref name="from"/> (default: this week) for <paramref name="weeks"/> weeks.</summary>
    Task<CapacityScheduleDto> GetAsync(DateOnly? from = null, int weeks = 8, CancellationToken cancellationToken = default);

    Task<CapacityScheduleDto> MoveAsync(Guid scheduleId, MoveOperationRequest request, CancellationToken cancellationToken = default);

    Task<CapacityScheduleDto> ResequenceAsync(Guid workCenterId, ResequenceRequest request, CancellationToken cancellationToken = default);
}

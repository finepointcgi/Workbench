using Workbench.Contracts.Materials;
using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Quality;

// Enums mirror Workbench.Domain.Quality by value.
public enum InspectionType { Receiving, InProcess, Final }

public enum Sampling { Full, FixedSample, Aql }

public enum CharacteristicType { Measured, PassFail }

public enum InspectionState { Pending, Completed, Cancelled }

public enum NcrSource { ReceivingInspection, InProcessInspection, FinalInspection, Stock }

public enum NcrState { Open, PendingApproval, Closed, Cancelled }

public enum NcrDisposition { UseAsIs, Rework, ReturnToVendor, Scrap }

public enum DebitMemoState { Open, Applied, Cancelled }

public enum CarState { Open, Implemented, Closed, Cancelled }

public enum RootCause { NotDetermined, Design, Process, Material, Supplier, Workmanship, Equipment, Documentation, Other }

public enum EcoState { Draft, PendingApproval, Approved, Effective, Cancelled }

public enum EcoReason { DefectCorrection, DesignImprovement, CostReduction, CustomerRequest, Obsolescence, Regulatory, Other }

public enum Effectivity { Date, SerialOrLot }

public enum EcoStockDisposition { UseUp, Rework, Scrap, ReturnToVendor }

public enum EcoWorkOrderDisposition { FinishOnOldRevision, UpdateToNewRevision, Hold }

public enum WorkOrderReviewState { Open, Reviewed }

/// <summary>Which quality record a file belongs to.</summary>
public enum QualityFileOwner { Inspection, Nonconformance, CorrectiveAction }

public sealed record QualityFileDto(Guid Id, Guid FileId, string Kind, string FileName, string UploadedBy, DateTime UploadedAtUtc);

// ---- Inspection plans ----

public sealed record CharacteristicDto(
    Guid Id, int Sequence, string Name, CharacteristicType Kind, decimal? Nominal, decimal? LowerLimit, decimal? UpperLimit, string? Unit, bool IsCritical)
{
    public string Limits => Kind == CharacteristicType.PassFail ? "Pass / fail"
        : (LowerLimit, UpperLimit) switch
        {
            ({ } lo, { } hi) => $"{lo:0.######} to {hi:0.######} {Unit}".Trim(),
            ({ } lo, null) => $"at least {lo:0.######} {Unit}".Trim(),
            (null, { } hi) => $"at most {hi:0.######} {Unit}".Trim(),
            _ => "Record the value",
        };
}

public sealed record InspectionPlanDto(
    Guid Id, string Code, string Name, Guid PartId, string PartNumber, string PartDescription, InspectionType Kind, int? OperationSequence,
    Sampling Sampling, int SampleSize, int AcceptNumber, decimal? Aql, IReadOnlyList<string> RequiredDocuments, string? Instructions, bool IsActive,
    IReadOnlyList<CharacteristicDto> Characteristics);

public sealed record SaveCharacteristicRequest(
    string Name, CharacteristicType Kind = CharacteristicType.PassFail, decimal? Nominal = null, decimal? LowerLimit = null, decimal? UpperLimit = null,
    string? Unit = null, bool IsCritical = false);

public sealed record SaveInspectionPlanRequest(
    string Code, string Name, Guid PartId, InspectionType Kind, IReadOnlyList<SaveCharacteristicRequest> Characteristics,
    Sampling Sampling = Sampling.Full, int SampleSize = 0, int AcceptNumber = 0, decimal? Aql = null, int? OperationSequence = null,
    IReadOnlyList<string>? RequiredDocuments = null, string? Instructions = null, bool IsActive = true);

// ---- Inspections ----

public sealed record InspectionSummaryDto(
    Guid Id, string Number, InspectionType Kind, InspectionState Status, Guid PartId, string PartNumber, string PartDescription, string UnitOfMeasure,
    decimal Quantity, int SampleSize, decimal QuantityAccepted, decimal QuantityRejected, string? Source, string? Vendor, DateOnly ReadyOn,
    DateOnly? InspectedOn, string? Inspector, Guid? NonconformanceId, string? NonconformanceNumber);

public sealed record InspectionItemDto(Guid Id, string? Location, string Lot, string Serial, decimal Quantity, decimal QuantityAccepted, decimal QuantityRejected);

public sealed record InspectionResultDto(Guid? CharacteristicId, int Sequence, string Name, decimal? MeasuredValue, bool Passed, string? Note);

public sealed record InspectionDto(
    InspectionSummaryDto Summary,
    Guid? PlanId,
    string? PlanName,
    string? Instructions,
    int AcceptNumber,
    int DefectsFound,
    string? Notes,
    Guid? WorkOrderId,
    string? WorkOrderNumber,
    int? OperationSequence,
    IReadOnlyList<CharacteristicDto> Characteristics,
    IReadOnlyList<InspectionItemDto> Items,
    IReadOnlyList<InspectionResultDto> Results,
    IReadOnlyList<string> RequiredDocuments,
    IReadOnlyList<QualityFileDto> Files)
{
    /// <summary>Required documents not attached yet.</summary>
    public IReadOnlyList<string> MissingDocuments => RequiredDocuments.Where(d => !Files.Any(f => string.Equals(f.Kind, d, StringComparison.OrdinalIgnoreCase))).ToList();
}

public sealed record InspectionQuery(InspectionState? Status = InspectionState.Pending, InspectionType? Kind = null, Guid? PartId = null, Guid? VendorId = null, int Take = 200);

/// <summary>A characteristic's result: a measured value (checked against the limits) or pass/fail.</summary>
public sealed record RecordResultRequest(Guid CharacteristicId, decimal? MeasuredValue = null, bool? Passed = null, string? Note = null);

public sealed record ItemDecisionRequest(Guid ItemId, decimal QuantityAccepted);

/// <summary>
/// Completes an inspection. Accepted stock is released to available; the rest goes to nonconforming stock on a new NCR
/// (defect codes and description required then). Without <see cref="Items"/>, <see cref="QuantityAccepted"/> is spread over
/// the items in order. Required documents must be attached first, unless the lot is rejected anyway.
/// </summary>
public sealed record CompleteInspectionRequest(
    DateOnly InspectedOn,
    IReadOnlyList<RecordResultRequest> Results,
    decimal? QuantityAccepted = null,
    IReadOnlyList<ItemDecisionRequest>? Items = null,
    int DefectsFound = 0,
    IReadOnlyList<string>? DefectCodes = null,
    string? NonconformanceDescription = null,
    string? Notes = null);

public sealed record DefectCodeDto(Guid Id, string Code, string Description, bool IsActive);

public sealed record SaveDefectCodeRequest(string Code, string Description, bool IsActive = true);

/// <summary>Inspection plans and the inspection queue. Reads: Materials, Finance, Admin; changes: Materials, Admin.</summary>
public interface IInspectionService
{
    Task<IReadOnlyList<InspectionPlanDto>> ListPlansAsync(Guid? partId = null, bool activeOnly = false, CancellationToken cancellationToken = default);

    Task<InspectionPlanDto> GetPlanAsync(Guid planId, CancellationToken cancellationToken = default);

    Task<InspectionPlanDto> SavePlanAsync(Guid? planId, SaveInspectionPlanRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<InspectionSummaryDto>> ListAsync(InspectionQuery query, CancellationToken cancellationToken = default);

    Task<InspectionDto> GetAsync(Guid inspectionId, CancellationToken cancellationToken = default);

    Task<InspectionDto> CompleteAsync(Guid inspectionId, CompleteInspectionRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DefectCodeDto>> ListDefectCodesAsync(bool activeOnly = true, CancellationToken cancellationToken = default);

    Task<DefectCodeDto> SaveDefectCodeAsync(Guid? defectCodeId, SaveDefectCodeRequest request, CancellationToken cancellationToken = default);
}

// ---- Nonconformances and debit memos ----

public sealed record NcrSummaryDto(
    Guid Id, string Number, NcrState Status, NcrSource Source, Guid PartId, string PartNumber, string PartDescription, string UnitOfMeasure, string Lot, string Serial,
    decimal Quantity, IReadOnlyList<string> DefectCodes, string? Vendor, NcrDisposition? Disposition, decimal ValueAtCost, DateTime CreatedAtUtc, DateTime? ClosedAtUtc);

public sealed record NcrItemDto(Guid Id, string Location, string Lot, string Serial, decimal Quantity);

public sealed record DebitMemoDto(
    Guid Id, string Number, Guid VendorId, string Vendor, Guid NonconformanceId, string NonconformanceNumber, string PartNumber, decimal Quantity, decimal UnitPrice,
    decimal Amount, string? ReturnAuthorization, DebitMemoState Status, DateOnly Date, string? PurchaseOrderNumber, string? AppliedBy, string? AppliedReference);

public sealed record NcrDto(
    NcrSummaryDto Summary,
    string Description,
    Guid? InspectionId,
    string? InspectionNumber,
    Guid? VendorId,
    string? PurchaseOrderNumber,
    Guid? WorkOrderId,
    string? WorkOrderNumber,
    string? DispositionNote,
    Guid? ScrapAccountId,
    string? ScrapAccount,
    Guid? ScrapWbsElementId,
    string? ScrapChargeCode,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc,
    string? DecidedBy,
    DateTime? DecidedAtUtc,
    string? DecisionComment,
    Guid? ReworkWorkOrderId,
    string? ReworkWorkOrderNumber,
    DebitMemoDto? DebitMemo,
    string? JournalEntryNumber,
    IReadOnlyList<NcrItemDto> Items,
    IReadOnlyList<QualityFileDto> Files,
    IReadOnlyList<CarSummaryDto> CorrectiveActions,
    bool CanDecide,
    bool CanWithdraw);

public sealed record NcrQuery(NcrState? Status = null, Guid? VendorId = null, Guid? PartId = null, bool OpenOnly = false, int Take = 200);

/// <summary>Reports nonconforming stock found in inventory: the quantity moves from the balance to nonconforming stock.</summary>
public sealed record CreateNcrRequest(Guid BalanceId, decimal Quantity, IReadOnlyList<string> DefectCodes, string Description);

/// <summary>
/// The material review board's proposed disposition, sent for approval. Scrap charges <see cref="ScrapWbsElementId"/>
/// (direct material on a project) or <see cref="ScrapAccountId"/> (default: inventory adjustments and scrap).
/// </summary>
public sealed record ProposeDispositionRequest(
    NcrDisposition Disposition, string? Note = null, Guid? ScrapAccountId = null, Guid? ScrapWbsElementId = null, string? ReturnAuthorization = null,
    DateOnly? Date = null);

public sealed record DecideNcrRequest(string? Comment = null);

public sealed record ApplyDebitMemoRequest(string Reference);

/// <summary>Nonconformance reports (NCRs) and vendor debit memos. Reads: Materials, Finance, Admin; changes: Materials, Admin; decisions: whoever the workflow routes to.</summary>
public interface INonconformanceService
{
    Task<IReadOnlyList<NcrSummaryDto>> ListAsync(NcrQuery query, CancellationToken cancellationToken = default);

    Task<NcrDto> GetAsync(Guid ncrId, CancellationToken cancellationToken = default);

    Task<NcrDto> CreateAsync(CreateNcrRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts the "NCR disposition" approval workflow. A disposition that needs no approval is carried out at once.</summary>
    Task<NcrDto> ProposeDispositionAsync(Guid ncrId, ProposeDispositionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Approves (the last approval carries out the disposition: stock movements, GL, rework work order or debit memo).</summary>
    Task<NcrDto> ApproveAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default);

    /// <summary>Sends the disposition back (the NCR stays open).</summary>
    Task<NcrDto> RejectAsync(Guid ncrId, DecideNcrRequest request, CancellationToken cancellationToken = default);

    Task<NcrDto> WithdrawDispositionAsync(Guid ncrId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<NcrSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DebitMemoDto>> ListDebitMemosAsync(Guid? vendorId = null, bool openOnly = false, CancellationToken cancellationToken = default);

    /// <summary>Accounts payable marks a memo taken against the vendor (Finance, Materials or Admin).</summary>
    Task<DebitMemoDto> ApplyDebitMemoAsync(Guid debitMemoId, ApplyDebitMemoRequest request, CancellationToken cancellationToken = default);
}

// ---- Corrective actions and supplier quality ----

public sealed record CarSummaryDto(
    Guid Id, string Number, string Title, CarState Status, string? Vendor, string? PartNumber, string? NonconformanceNumber, string OwnerEmail, DateOnly DueDate, bool IsOverdue);

public sealed record CarDto(
    CarSummaryDto Summary, Guid? NonconformanceId, Guid? VendorId, Guid? PartId, string Problem, RootCause RootCauseCategory, string? RootCause, string? Action,
    string? PreventiveAction, DateOnly? ImplementedOn, string? VerifiedBy, string? VerificationNote, DateTime? ClosedAtUtc, string CreatedBy, DateTime CreatedAtUtc,
    IReadOnlyList<QualityFileDto> Files);

public sealed record SaveCarRequest(
    string Title, string Problem, string OwnerEmail, DateOnly DueDate, Guid? NonconformanceId = null, Guid? VendorId = null, Guid? PartId = null,
    RootCause RootCauseCategory = RootCause.NotDetermined, string? RootCause = null, string? Action = null, string? PreventiveAction = null);

public sealed record CompleteCarRequest(DateOnly ImplementedOn, string? Note = null);

public sealed record VerifyCarRequest(bool Effective, string? Note = null);

public sealed record CarQuery(CarState? Status = null, Guid? VendorId = null, bool OpenOnly = false);

public sealed record SupplierQualityRowDto(
    Guid VendorId, string VendorCode, string Vendor, int Inspections, decimal QuantityInspected, decimal QuantityAccepted, decimal QuantityRejected,
    decimal RejectRatePercent, int Ncrs, int OpenNcrs, decimal ReturnedValue, int OpenCorrectiveActions);

public sealed record SupplierQualityDto(DateOnly From, DateOnly To, IReadOnlyList<SupplierQualityRowDto> Rows);

/// <summary>Corrective action requests and the supplier quality view. Reads: Materials, Finance, Admin; changes: Materials, Admin.</summary>
public interface ICorrectiveActionService
{
    Task<IReadOnlyList<CarSummaryDto>> ListAsync(CarQuery query, CancellationToken cancellationToken = default);

    Task<CarDto> GetAsync(Guid carId, CancellationToken cancellationToken = default);

    Task<CarDto> SaveAsync(Guid? carId, SaveCarRequest request, CancellationToken cancellationToken = default);

    Task<CarDto> MarkImplementedAsync(Guid carId, CompleteCarRequest request, CancellationToken cancellationToken = default);

    /// <summary>Effective: closed. Not effective: back to open.</summary>
    Task<CarDto> VerifyAsync(Guid carId, VerifyCarRequest request, CancellationToken cancellationToken = default);

    Task<CarDto> CancelAsync(Guid carId, CancellationToken cancellationToken = default);

    Task<SupplierQualityDto> SupplierQualityAsync(DateOnly? from = null, DateOnly? to = null, CancellationToken cancellationToken = default);
}

/// <summary>Certificates, test reports and photos on inspections, NCRs and corrective actions.</summary>
public interface IQualityFileService
{
    Task<QualityFileDto> AttachAsync(QualityFileOwner owner, Guid ownerId, string kind, string fileName, Stream content, CancellationToken cancellationToken = default);

    Task<ReceiptContent> OpenAsync(Guid attachmentId, CancellationToken cancellationToken = default);

    string GetUrl(Guid attachmentId);
}

// ---- Engineering changes ----

public sealed record EcoItemDto(
    Guid Id, Guid PartId, string PartNumber, string PartRevision, string PartDescription, Guid? FromBomId, string? FromBomRevision, Guid? ToBomId, string? ToBomRevision,
    Guid? NewPartId, string? NewPartRevision, string? Note);

public sealed record WorkOrderReviewDto(
    Guid Id, Guid WorkOrderId, string WorkOrderNumber, string PartNumber, WorkOrderState WorkOrderStatus, WorkOrderReviewState Status, DateTime FlaggedAtUtc,
    string? ReviewedBy, string? Note);

public sealed record EcoSummaryDto(
    Guid Id, string Number, string Title, EcoReason Reason, EcoState Status, Effectivity EffectivityKind, DateOnly? EffectiveDate, string? EffectiveSerialOrLot,
    IReadOnlyList<string> Parts, int OpenWorkOrderReviews, DateTime CreatedAtUtc, DateTime? EffectiveAtUtc);

public sealed record EcoDto(
    EcoSummaryDto Summary, string Description, EcoStockDisposition StockDisposition, EcoWorkOrderDisposition WorkOrderDisposition, decimal CostImpact,
    string CreatedBy, string? SubmittedBy, DateTime? SubmittedAtUtc, string? DecidedBy, DateTime? DecidedAtUtc, string? DecisionComment,
    IReadOnlyList<EcoItemDto> Items, IReadOnlyList<WorkOrderReviewDto> WorkOrderReviews, bool CanDecide, bool CanEdit);

public sealed record SaveEcoItemRequest(Guid PartId, Guid? ToBomId = null, Guid? NewPartId = null, string? Note = null);

public sealed record SaveEcoRequest(
    string Title, EcoReason Reason, string Description, IReadOnlyList<SaveEcoItemRequest> Items, Effectivity EffectivityKind = Effectivity.Date,
    DateOnly? EffectiveDate = null, string? EffectiveSerialOrLot = null, EcoStockDisposition StockDisposition = EcoStockDisposition.UseUp,
    EcoWorkOrderDisposition WorkOrderDisposition = EcoWorkOrderDisposition.FinishOnOldRevision, decimal CostImpact = 0);

public sealed record DecideEcoRequest(string? Comment = null);

public sealed record ReviewWorkOrderRequest(string? Note = null);

public sealed record EcoQuery(EcoState? Status = null, Guid? PartId = null, bool OpenOnly = false);

/// <summary>One step in a part's revision history (part revisions, BOM revisions, engineering changes).</summary>
public sealed record RevisionEventDto(DateTime AtUtc, string Kind, string Revision, string Description, Guid? EngineeringChangeId, string? EngineeringChangeNumber, bool IsCurrent);

public sealed record RevisionHistoryDto(Guid PartId, string PartNumber, string PartDescription, string? ActiveBomRevision, IReadOnlyList<RevisionEventDto> Events);

/// <summary>One component compared: what the design calls for per unit against what went into the unit.</summary>
public sealed record ConfigurationLineDto(
    Guid PartId, string PartNumber, string PartDescription, decimal DesignedQuantity, decimal BuiltQuantity, IReadOnlyList<string> LotsAndSerials, string Status);

/// <summary>As-designed (the BOM revision in effect for this serial or lot) against as-built (the work order's genealogy).</summary>
public sealed record ConfigurationDto(
    Guid PartId, string PartNumber, string Serial, string Lot, string? WorkOrderNumber, DateOnly? BuiltOn, string? AsBuiltBomRevision, string? AsDesignedBomRevision,
    string? AsDesignedBy, IReadOnlyList<ConfigurationLineDto> Lines)
{
    public bool Matches => Lines.All(l => l.Status == "Matches");
}

public sealed record ConfigurationUnitDto(string Serial, string Lot, string? WorkOrderNumber, DateOnly BuiltOn, string? AsBuiltBomRevision, string? AsDesignedBomRevision, bool Matches);

/// <summary>Engineering change orders, revision history and configuration (as-designed against as-built). Reads: Materials, Finance, Admin; changes: Materials, Admin.</summary>
public interface IEngineeringChangeService
{
    Task<IReadOnlyList<EcoSummaryDto>> ListAsync(EcoQuery query, CancellationToken cancellationToken = default);

    Task<EcoDto> GetAsync(Guid ecoId, CancellationToken cancellationToken = default);

    Task<EcoDto> SaveAsync(Guid? ecoId, SaveEcoRequest request, CancellationToken cancellationToken = default);

    /// <summary>Starts the "Engineering change" approval workflow.</summary>
    Task<EcoDto> SubmitAsync(Guid ecoId, CancellationToken cancellationToken = default);

    /// <summary>The last approval makes it Approved, and Effective at once when its effectivity has come.</summary>
    Task<EcoDto> ApproveAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default);

    /// <summary>Back to draft with the reason.</summary>
    Task<EcoDto> RejectAsync(Guid ecoId, DecideEcoRequest request, CancellationToken cancellationToken = default);

    Task<EcoDto> CancelAsync(Guid ecoId, CancellationToken cancellationToken = default);

    /// <summary>Makes approved changes whose effective date has come effective (also done hourly and before work orders are released). Returns how many.</summary>
    Task<int> ApplyDueAsync(DateOnly? asOf = null, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorkOrderReviewDto>> ListOpenWorkOrderReviewsAsync(CancellationToken cancellationToken = default);

    Task<WorkOrderReviewDto> MarkWorkOrderReviewedAsync(Guid reviewId, ReviewWorkOrderRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<EcoSummaryDto>> ListWaitingOnMeAsync(CancellationToken cancellationToken = default);

    Task<RevisionHistoryDto> RevisionHistoryAsync(Guid partId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ConfigurationUnitDto>> ListUnitsAsync(Guid partId, CancellationToken cancellationToken = default);

    Task<ConfigurationDto> ConfigurationAsync(Guid partId, string? serial = null, string? lot = null, CancellationToken cancellationToken = default);
}

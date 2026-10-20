using Workbench.Domain.Materials;

namespace Workbench.Domain.Quality;

/// <summary>When a part is inspected.</summary>
public enum InspectionKind
{
    /// <summary>Purchased goods are received into inspection hold and released when they pass.</summary>
    Receiving,

    /// <summary>At a routing operation of a work order (<see cref="InspectionPlan.OperationSequence"/>); the order can't complete until it's done.</summary>
    InProcess,

    /// <summary>Finished units of a stock work order go into inspection hold and are released when they pass.</summary>
    Final,
}

/// <summary>How many units are inspected.</summary>
public enum SamplingMethod
{
    /// <summary>Every unit (100% inspection).</summary>
    Full,

    /// <summary>A fixed number of units (<see cref="InspectionPlan.SampleSize"/>), or all of them when fewer arrive.</summary>
    FixedSample,

    /// <summary>The sample size for the lot size from ANSI/ASQ Z1.4 general inspection level II (single sampling, normal).</summary>
    Aql,
}

public enum CharacteristicKind
{
    /// <summary>A measured value checked against the lower and upper limits.</summary>
    Measured,

    /// <summary>A visual or functional check that passes or fails.</summary>
    PassFail,
}

/// <summary>
/// What to check on a part, when, and how many: characteristics with spec limits, sampling, and the documents the
/// supplier must send (for example a certificate of conformance). One active plan per part and kind (and operation).
/// </summary>
public sealed class InspectionPlan : Entity
{
    /// <summary>Upper case, unique (for example "HSG-220-RCV").</summary>
    public required string Code { get; set; }

    public required string Name { get; set; }
    public Guid PartId { get; set; }
    public InspectionKind Kind { get; set; }

    /// <summary>In-process plans: the routing operation sequence the inspection follows (for example 20).</summary>
    public int? OperationSequence { get; set; }

    public SamplingMethod Sampling { get; set; }

    /// <summary>Fixed samples: units inspected.</summary>
    public int SampleSize { get; set; }

    /// <summary>The lot is accepted with at most this many defective units in the sample (0 = zero defects).</summary>
    public int AcceptNumber { get; set; }

    /// <summary>The acceptable quality level the sampling targets (for example 1.0), for reference.</summary>
    public decimal? Aql { get; set; }

    /// <summary>Documents the inspector must attach, one per line (for example "Certificate of conformance").</summary>
    public string? RequiredDocuments { get; set; }

    public string? Instructions { get; set; }
    public bool IsActive { get; set; } = true;
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<InspectionCharacteristic> Characteristics { get; } = [];

    public IReadOnlyList<string> RequiredDocumentList =>
        (RequiredDocuments ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

public sealed class InspectionCharacteristic : Entity
{
    public Guid PlanId { get; set; }
    public int Sequence { get; set; }
    public required string Name { get; set; }
    public CharacteristicKind Kind { get; set; }
    public decimal? Nominal { get; set; }
    public decimal? LowerLimit { get; set; }
    public decimal? UpperLimit { get; set; }

    /// <summary>For example "mm", "V", "Ω".</summary>
    public string? Unit { get; set; }

    /// <summary>A failed critical characteristic rejects the whole lot, whatever the accept number.</summary>
    public bool IsCritical { get; set; }

    public bool IsWithinLimits(decimal value) => (LowerLimit is null || value >= LowerLimit) && (UpperLimit is null || value <= UpperLimit);
}

public enum InspectionStatus
{
    Pending,
    Completed,
    Cancelled,
}

/// <summary>
/// One inspection: a receipt, a work order operation or a completion waiting to be inspected, then the results, who
/// inspected it, and how much was accepted (released from hold) and rejected (to nonconforming, with an NCR).
/// </summary>
public sealed class Inspection : Entity
{
    /// <summary>For example "INS-000012".</summary>
    public required string Number { get; set; }

    public Guid? PlanId { get; set; }
    public Guid PartId { get; set; }
    public InspectionKind Kind { get; set; }
    public InspectionStatus Status { get; set; } = InspectionStatus.Pending;

    public decimal Quantity { get; set; }

    /// <summary>Units to inspect under the plan's sampling (the quantity when there's no plan).</summary>
    public int SampleSize { get; set; }

    public decimal QuantityAccepted { get; set; }
    public decimal QuantityRejected { get; set; }

    /// <summary>Defective units found in the sample.</summary>
    public int DefectsFound { get; set; }

    // Where it came from.
    public Guid? VendorId { get; set; }
    public Guid? PurchaseOrderId { get; set; }
    public Guid? PurchaseOrderLineId { get; set; }
    public Guid? PurchaseReceiptId { get; set; }
    public Guid? WorkOrderId { get; set; }
    public int? OperationSequence { get; set; }

    /// <summary>The date the goods arrived or the operation was ready.</summary>
    public DateOnly ReadyOn { get; set; }

    public DateOnly? InspectedOn { get; set; }
    public string? InspectorEmail { get; set; }
    public string? Notes { get; set; }

    /// <summary>The NCR opened for the rejected quantity.</summary>
    public Guid? NonconformanceId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }

    public List<InspectionItem> Items { get; } = [];
    public List<InspectionResult> Results { get; } = [];
}

/// <summary>Held stock covered by an inspection: one location, lot and serial (none for in-process inspections).</summary>
public sealed class InspectionItem : Entity
{
    public Guid InspectionId { get; set; }
    public Guid? WarehouseId { get; set; }
    public Guid? BinId { get; set; }
    public string Lot { get; set; } = "";
    public string Serial { get; set; } = "";
    public InventoryOwnership Ownership { get; set; }
    public Guid? OwnerProjectId { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityAccepted { get; set; }
    public decimal QuantityRejected { get; set; }
}

/// <summary>The recorded result of one characteristic.</summary>
public sealed class InspectionResult : Entity
{
    public Guid InspectionId { get; set; }
    public Guid? CharacteristicId { get; set; }
    public int Sequence { get; set; }
    public required string Name { get; set; }
    public decimal? MeasuredValue { get; set; }
    public bool Passed { get; set; }
    public string? Note { get; set; }
}

/// <summary>A file kept with a quality record: a certificate of conformance, a test report, a photo of the defect.</summary>
public sealed class QualityAttachment : Entity
{
    /// <summary>"Inspection", "Nonconformance" or "CorrectiveAction".</summary>
    public required string OwnerType { get; set; }

    public Guid OwnerId { get; set; }

    /// <summary>The <c>StoredFile</c> id.</summary>
    public Guid FileId { get; set; }

    /// <summary>What the document is, for example "Certificate of conformance" or "Photo".</summary>
    public required string Kind { get; set; }

    public required string FileName { get; set; }
    public required string UploadedBy { get; set; }
    public DateTime UploadedAtUtc { get; set; }
}

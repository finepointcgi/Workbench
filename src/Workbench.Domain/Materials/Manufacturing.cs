namespace Workbench.Domain.Materials;

/// <summary>
/// A bill of materials revision for a make part. One revision per part is active at a time; components carry their own
/// effectivity dates, so an engineering change can phase a component in or out on a date.
/// </summary>
public sealed class BillOfMaterial : Entity
{
    public Guid PartId { get; set; }

    /// <summary>For example "A", "B". Unique per part.</summary>
    public required string Revision { get; set; }

    public bool IsActive { get; set; }
    public string? Notes { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }

    public List<BomComponent> Components { get; } = [];
}

public sealed class BomComponent : Entity
{
    public Guid BomId { get; set; }
    public int Sequence { get; set; }
    public Guid ComponentPartId { get; set; }

    /// <summary>Quantity of the component per one unit of the parent.</summary>
    public decimal QuantityPer { get; set; }

    /// <summary>Expected loss in percent (5 = 5%), added to requirements.</summary>
    public decimal ScrapPercent { get; set; }

    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }

    /// <summary>For example "R1, R2" (electronics) or a find number.</summary>
    public string? ReferenceDesignators { get; set; }

    /// <summary>The routing operation the component is consumed at, when it matters.</summary>
    public int? OperationSequence { get; set; }

    public bool IsEffectiveOn(DateOnly date) => (EffectiveFrom is null || EffectiveFrom <= date) && (EffectiveTo is null || date <= EffectiveTo);

    /// <summary>Required quantity for <paramref name="parentQuantity"/> units, scrap included.</summary>
    public decimal RequiredFor(decimal parentQuantity) => Math.Round(parentQuantity * QuantityPer * (1 + ScrapPercent / 100m), 4, MidpointRounding.AwayFromZero);
}

/// <summary>A place work is done (assembly bench, test station), with the labor category its operators usually charge.</summary>
public sealed class WorkCenter : Entity
{
    public required string Code { get; set; }
    public required string Name { get; set; }
    public Guid? LaborCategoryId { get; set; }

    /// <summary>Available hours per working day (for load display).</summary>
    public decimal HoursPerDay { get; set; } = 8m;

    public bool IsActive { get; set; } = true;
}

/// <summary>How a make part is built: operations in sequence at work centers. One active revision per part.</summary>
public sealed class Routing : Entity
{
    public Guid PartId { get; set; }
    public required string Revision { get; set; }
    public bool IsActive { get; set; }
    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }

    public List<RoutingOperation> Operations { get; } = [];
}

public sealed class RoutingOperation : Entity
{
    public Guid RoutingId { get; set; }

    /// <summary>For example 10, 20, 30.</summary>
    public int Sequence { get; set; }

    public required string Description { get; set; }
    public Guid WorkCenterId { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public decimal SetupHours { get; set; }
    public decimal RunHoursPerUnit { get; set; }
}

public enum WorkOrderStatus
{
    /// <summary>Created (by hand or firmed from MRP); components not yet allocated.</summary>
    Planned,

    /// <summary>Released to the floor: components and operations copied from the BOM and routing.</summary>
    Released,

    /// <summary>Components issued or labor reported.</summary>
    InProgress,

    /// <summary>The full quantity is completed or scrapped; WIP may still need closing.</summary>
    Completed,

    /// <summary>WIP relieved and variances posted. Final.</summary>
    Closed,

    Cancelled,
}

/// <summary>
/// A work order: build a quantity of a make part, either for a project (cost collects on the project's WBS element as it
/// is incurred and the finished units are delivered to it) or to stock (cost collects in work in process and is
/// relieved to inventory at completion, with variances at close).
/// </summary>
public sealed class WorkOrder : Entity
{
    /// <summary>For example "WO-000007".</summary>
    public required string Number { get; set; }

    public Guid PartId { get; set; }
    public decimal Quantity { get; set; }
    public decimal QuantityCompleted { get; set; }
    public decimal QuantityScrapped { get; set; }

    /// <summary>Project work orders: the project and the chargeable WBS element (also the shop floor's timesheet charge code).</summary>
    public Guid? ProjectId { get; set; }

    public Guid? WbsElementId { get; set; }

    /// <summary>Stock work orders: the charge code shop floor time goes on (an indirect manufacturing WBS element), if any.</summary>
    public Guid? LaborChargeWbsElementId { get; set; }

    public DateOnly StartDate { get; set; }
    public DateOnly DueDate { get; set; }
    public WorkOrderStatus Status { get; set; } = WorkOrderStatus.Planned;
    public Guid? BomId { get; set; }
    public Guid? RoutingId { get; set; }

    /// <summary>Where completions to stock go.</summary>
    public Guid? WarehouseId { get; set; }

    public Guid? BinId { get; set; }
    public Guid? PlannedOrderId { get; set; }
    public string? Notes { get; set; }

    // Cost collected (stock and project work orders alike) and relieved (stock work orders).
    public decimal MaterialCost { get; set; }
    public decimal LaborCost { get; set; }
    public decimal ScrapCost { get; set; }
    public decimal CompletedCost { get; set; }
    public decimal VarianceCost { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReleasedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public DateTime? ClosedAtUtc { get; set; }

    public bool IsForProject => ProjectId is not null;

    public decimal QuantityOpen => Math.Max(0, Quantity - QuantityCompleted - QuantityScrapped);

    /// <summary>Cost still in work in process (stock work orders).</summary>
    public decimal WipBalance => MaterialCost + LaborCost - ScrapCost - CompletedCost - VarianceCost;

    public bool IsOpen => Status is WorkOrderStatus.Released or WorkOrderStatus.InProgress;

    public List<WorkOrderComponent> Components { get; } = [];
    public List<WorkOrderOperation> Operations { get; } = [];
}

/// <summary>A component the work order needs: the BOM line at release, effective on the start date.</summary>
public sealed class WorkOrderComponent : Entity
{
    public Guid WorkOrderId { get; set; }
    public int Sequence { get; set; }
    public Guid PartId { get; set; }
    public decimal QuantityPer { get; set; }
    public decimal QuantityRequired { get; set; }
    public decimal QuantityIssued { get; set; }
    public int? OperationSequence { get; set; }

    public decimal QuantityOpen => Math.Max(0, QuantityRequired - QuantityIssued);
}

public sealed class WorkOrderOperation : Entity
{
    public Guid WorkOrderId { get; set; }
    public int Sequence { get; set; }
    public required string Description { get; set; }
    public Guid WorkCenterId { get; set; }
    public Guid? LaborCategoryId { get; set; }
    public decimal SetupHours { get; set; }
    public decimal RunHoursPerUnit { get; set; }
    public decimal QuantityCompleted { get; set; }
    public decimal HoursReported { get; set; }

    public decimal PlannedHours(decimal quantity) => SetupHours + RunHoursPerUnit * quantity;
}

/// <summary>
/// Shop floor labor reported on an operation (append-only). The hours are also on the employee's timesheet, charged to
/// the work order's charge code: the timesheet stays the DCAA record and posts the labor; this is what the work order
/// costs. Cost = hours × the employee's cost rate on the work date (computed, never typed).
/// </summary>
public sealed class WorkOrderLaborReport : Entity
{
    public Guid WorkOrderId { get; set; }
    public Guid OperationId { get; set; }
    public Guid EmployeeId { get; set; }
    public DateOnly WorkDate { get; set; }
    public decimal Hours { get; set; }
    public decimal QuantityCompleted { get; set; }
    public decimal Cost { get; set; }
    public Guid? JournalEntryId { get; set; }
    public string? Note { get; set; }
    public required string ReportedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
}

/// <summary>Records which component lots and serials went into which finished lot or serial (genealogy).</summary>
public sealed class WorkOrderGenealogy : Entity
{
    public Guid WorkOrderId { get; set; }

    /// <summary>The completion transaction of the finished unit(s).</summary>
    public Guid CompletionTransactionId { get; set; }

    /// <summary>The component issue transaction consumed.</summary>
    public Guid ComponentTransactionId { get; set; }
}

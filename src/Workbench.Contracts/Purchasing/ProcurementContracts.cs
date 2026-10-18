using Workbench.Contracts.Odcs;

namespace Workbench.Contracts.Purchasing;

// Procure-to-pay in front of accounts payable: requisitions (any employee, for projects they're assigned to or their own
// organization), approved by the project manager and then Finance above a threshold; purchase orders (Finance) with
// change orders; receiving (Finance or the receivers named on the order); and the three-way match that holds AP bill lines
// billing a PO line until quantity and price agree, or Finance overrides with a reason. Open PO amounts are commitments on
// the project and WBS; they're never posted to the general ledger.

public enum RequisitionState { Draft, PendingApproval, Approved, Rejected, Ordered, Cancelled }

public enum RequisitionApprovalStage { ProjectManager, Finance }

public enum PurchaseOrderState { Draft, Issued, PartiallyReceived, Received, Closed, Cancelled }

// ---- Requisitions ----

public sealed record RequisitionLineDto(
    Guid Id,
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitCost,
    decimal Amount,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    OdcCostType? OdcType,
    Guid? AccountId,
    string? AccountNumber,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    DateOnly? NeededBy,
    Guid? SuggestedVendorId,
    string? SuggestedVendorName,

    /// <summary>The project manager the line waits on, or "Finance".</summary>
    string? RoutedTo,
    string? ApprovedBy,
    Guid? PurchaseOrderLineId,
    string? PurchaseOrderNumber);

public sealed record RequisitionApprovalDto(RequisitionApprovalStage Stage, string Decision, string DecidedBy, DateTime DecidedAtUtc, string? ProjectCode, string? Comment);

public sealed record RequisitionSummaryDto(
    Guid Id,
    string Number,
    string RequesterEmail,
    string? RequesterName,
    string Justification,
    RequisitionState Status,
    RequisitionApprovalStage? Stage,
    decimal Total,
    DateTime CreatedAtUtc,
    DateTime? SubmittedAtUtc,
    int LineCount);

public sealed record RequisitionDto(
    RequisitionSummaryDto Requisition,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    bool NeedsFinance,
    string? RejectedBy,
    string? RejectionReason,
    IReadOnlyList<RequisitionLineDto> Lines,
    IReadOnlyList<RequisitionApprovalDto> History,
    bool CanEdit,
    bool CanApprove);

/// <summary>
/// One requisition line: a project WBS element (and ODC type, default Materials), or a GL account (charged to the
/// requisition's organization unless <see cref="OrgUnitId"/> is set).
/// </summary>
public sealed record SaveRequisitionLineRequest(
    string Description,
    decimal Quantity,
    decimal UnitCost,
    Guid? WbsElementId = null,
    OdcCostType? OdcType = null,
    Guid? AccountId = null,
    Guid? OrgUnitId = null,
    DateOnly? NeededBy = null,
    Guid? SuggestedVendorId = null);

public sealed record SaveRequisitionRequest(string Justification, IReadOnlyList<SaveRequisitionLineRequest> Lines, Guid? OrgUnitId = null);

public sealed record RequisitionDecisionRequest(string? Comment = null);

/// <summary><see cref="Mine"/>: only my requisitions (everyone else sees only theirs anyway unless Finance).</summary>
public sealed record RequisitionQuery(RequisitionState? Status = null, bool Mine = false, string? Search = null);

public sealed record RequisitionChargeOption(Guid WbsElementId, Guid ProjectId, string ProjectCode, string WbsCode, string Name);

public sealed record RequisitionAccountOption(Guid AccountId, string Number, string Name);

public sealed record RequisitionOrgOption(Guid OrgUnitId, string Code, string Name);

public sealed record RequisitionVendorOption(Guid VendorId, string Code, string Name);

/// <summary>What the signed-in person may put on a requisition.</summary>
public sealed record RequisitionOptionsDto(
    IReadOnlyList<RequisitionChargeOption> ChargeCodes,
    IReadOnlyList<RequisitionAccountOption> Accounts,
    IReadOnlyList<RequisitionOrgOption> Orgs,
    IReadOnlyList<RequisitionVendorOption> Vendors,
    Guid? DefaultOrgUnitId,
    decimal FinanceThreshold);

public interface IRequisitionService
{
    /// <summary>Finance and admins see all; others see their own and the ones they've been asked to approve.</summary>
    Task<IReadOnlyList<RequisitionSummaryDto>> ListAsync(RequisitionQuery query, CancellationToken cancellationToken = default);

    Task<RequisitionDto> GetAsync(Guid requisitionId, CancellationToken cancellationToken = default);

    Task<RequisitionOptionsDto> GetOptionsAsync(CancellationToken cancellationToken = default);

    Task<RequisitionDto> CreateAsync(SaveRequisitionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Drafts and rejected requisitions, by their requester.</summary>
    Task<RequisitionDto> UpdateAsync(Guid requisitionId, SaveRequisitionRequest request, CancellationToken cancellationToken = default);

    /// <summary>Routes it: project lines to their project managers, then Finance when over the threshold (or for account lines).</summary>
    Task<RequisitionDto> SubmitAsync(Guid requisitionId, CancellationToken cancellationToken = default);

    /// <summary>Waiting on the current user.</summary>
    Task<IReadOnlyList<RequisitionSummaryDto>> ListApprovalQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Approves the current stage (the lines on the approver's projects, or the Finance stage). Never one's own requisition.</summary>
    Task<RequisitionDto> ApproveAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default);

    Task<RequisitionDto> RejectAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default);

    /// <summary>The requester (before it's ordered) or Finance.</summary>
    Task<RequisitionDto> CancelAsync(Guid requisitionId, RequisitionDecisionRequest request, CancellationToken cancellationToken = default);
}

// ---- Purchase orders and receiving ----

public sealed record PurchaseOrderLineDto(
    Guid Id,
    int LineNumber,
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    decimal Amount,
    Guid? ProjectId,
    string? ProjectCode,
    Guid? WbsElementId,
    string? WbsCode,
    OdcCostType? OdcType,
    Guid? AccountId,
    string? AccountNumber,
    Guid? OrgUnitId,
    string? OrgUnitCode,
    DateOnly? NeededBy,
    bool IsService,
    decimal QuantityReceived,

    /// <summary>On submitted, approved or posted bills.</summary>
    decimal QuantityBilled,
    decimal AmountBilled,

    /// <summary>Open commitment: unbilled (not yet posted) quantity × price on an open order.</summary>
    decimal Committed,
    Guid? RequisitionLineId,
    string? RequisitionNumber);

public sealed record PurchaseOrderRevisionDto(int Revision, string Reason, decimal TotalBefore, decimal TotalAfter, IReadOnlyList<string> Changes, string ChangedBy, DateTime ChangedAtUtc);

public sealed record ReceiptLineDto(Guid PurchaseOrderLineId, int LineNumber, string Description, decimal Quantity, bool IsAcceptance);

public sealed record ReceiptDto(Guid Id, string Number, DateOnly ReceivedOn, string ReceivedBy, string? Note, IReadOnlyList<ReceiptLineDto> Lines);

public sealed record PurchaseOrderSummaryDto(
    Guid Id,
    string Number,
    Guid VendorId,
    string VendorCode,
    string VendorName,
    DateOnly OrderDate,
    PurchaseOrderState Status,
    int Revision,
    decimal Total,
    decimal Received,
    decimal Billed,
    decimal Committed);

public sealed record PurchaseOrderDto(
    PurchaseOrderSummaryDto Order,
    string? Terms,
    string? ShipTo,
    string? Notes,
    IReadOnlyList<string> Receivers,
    string CreatedBy,
    string? IssuedBy,
    DateTime? IssuedAtUtc,
    string? CancelReason,
    IReadOnlyList<PurchaseOrderLineDto> Lines,
    IReadOnlyList<PurchaseOrderRevisionDto> Revisions,
    IReadOnlyList<ReceiptDto> Receipts,
    IReadOnlyList<BillSummaryDto> Bills,
    bool CanEdit,
    bool CanReceive);

/// <summary>A PO line. Same charging rules as bill lines: a WBS element (project cost) or a GL account and organization.</summary>
public sealed record SavePurchaseOrderLineRequest(
    string Description,
    decimal Quantity,
    decimal UnitPrice,
    Guid? WbsElementId = null,
    OdcCostType? OdcType = null,
    Guid? AccountId = null,
    Guid? OrgUnitId = null,
    DateOnly? NeededBy = null,
    bool IsService = false,

    /// <summary>On change orders: the existing line this replaces (omit for a new line).</summary>
    Guid? LineId = null);

public sealed record SavePurchaseOrderRequest(
    Guid VendorId,
    DateOnly OrderDate,
    IReadOnlyList<SavePurchaseOrderLineRequest> Lines,
    string? Terms = null,
    string? ShipTo = null,
    string? Notes = null,
    IReadOnlyList<string>? Receivers = null);

/// <summary>Orders the approved, not-yet-ordered lines of one or more requisitions from one vendor.</summary>
public sealed record CreatePurchaseOrderFromRequisitionsRequest(
    IReadOnlyList<Guid> RequisitionIds,
    Guid VendorId,
    DateOnly OrderDate,
    string? Terms = null,
    string? ShipTo = null,
    IReadOnlyList<string>? Receivers = null,

    /// <summary>Optional: only these requisition lines (default: every open approved line).</summary>
    IReadOnlyList<Guid>? RequisitionLineIds = null);

/// <summary>
/// A change order on an issued PO: the full new line list (existing lines keep their <see cref="SavePurchaseOrderLineRequest.LineId"/>).
/// A line can't drop below what's been received or billed, and lines with receipts can't be removed.
/// </summary>
public sealed record ChangeOrderRequest(string Reason, IReadOnlyList<SavePurchaseOrderLineRequest> Lines, string? Terms = null, string? ShipTo = null);

public sealed record ReceiveLineRequest(Guid PurchaseOrderLineId, decimal Quantity);

public sealed record ReceiveRequest(DateOnly ReceivedOn, IReadOnlyList<ReceiveLineRequest> Lines, string? Note = null);

public sealed record PurchaseOrderReasonRequest(string Reason);

public sealed record PurchaseOrderQuery(PurchaseOrderState? Status = null, Guid? VendorId = null, string? Search = null, bool OpenOnly = false);

/// <summary>An approved requisition line waiting to be ordered.</summary>
public sealed record OrderableRequisitionLineDto(
    Guid RequisitionId,
    string RequisitionNumber,
    string RequesterEmail,
    RequisitionLineDto Line);

public interface IPurchaseOrderService
{
    Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListAsync(PurchaseOrderQuery query, CancellationToken cancellationToken = default);

    Task<PurchaseOrderDto> GetAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<OrderableRequisitionLineDto>> ListOrderableRequisitionLinesAsync(CancellationToken cancellationToken = default);

    /// <summary>A PO entered directly by Finance (draft).</summary>
    Task<PurchaseOrderDto> CreateAsync(SavePurchaseOrderRequest request, CancellationToken cancellationToken = default);

    Task<PurchaseOrderDto> CreateFromRequisitionsAsync(CreatePurchaseOrderFromRequisitionsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Drafts only; issued orders change through <see cref="ChangeOrderAsync"/>.</summary>
    Task<PurchaseOrderDto> UpdateAsync(Guid purchaseOrderId, SavePurchaseOrderRequest request, CancellationToken cancellationToken = default);

    Task<PurchaseOrderDto> IssueAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    Task<PurchaseOrderDto> ChangeOrderAsync(Guid purchaseOrderId, ChangeOrderRequest request, CancellationToken cancellationToken = default);

    /// <summary>Finance or the order's receivers. Quantities can't exceed what's ordered.</summary>
    Task<PurchaseOrderDto> ReceiveAsync(Guid purchaseOrderId, ReceiveRequest request, CancellationToken cancellationToken = default);

    /// <summary>Open orders the current user can receive against.</summary>
    Task<IReadOnlyList<PurchaseOrderSummaryDto>> ListReceivableAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops further receiving and billing and releases the remaining commitment.</summary>
    Task<PurchaseOrderDto> CloseAsync(Guid purchaseOrderId, CancellationToken cancellationToken = default);

    /// <summary>Only orders with nothing received or billed; their requisition lines go back to approved.</summary>
    Task<PurchaseOrderDto> CancelAsync(Guid purchaseOrderId, PurchaseOrderReasonRequest request, CancellationToken cancellationToken = default);
}

// ---- Three-way match ----

public sealed record MatchExceptionDto(
    Guid BillId,
    string BillNumber,
    BillState BillStatus,
    string VendorName,
    Guid BillLineId,
    int LineNumber,
    string Description,
    Guid PurchaseOrderId,
    string PurchaseOrderNumber,
    int PurchaseOrderLineNumber,
    decimal QuantityBilled,
    decimal QuantityReceived,
    decimal QuantityOrdered,
    decimal UnitPrice,
    decimal PoUnitPrice,
    string Problem,
    string? OverrideBy,
    string? OverrideReason);

public sealed record MatchOverrideRequest(string Reason);

public sealed record CreateBillFromPurchaseOrderRequest(Guid PurchaseOrderId, string InvoiceNumber, DateOnly InvoiceDate, string? Description = null);

public interface IThreeWayMatchService
{
    /// <summary>
    /// Bill lines on live bills that bill a PO line and don't match (billed quantity over received, or price outside the
    /// tolerance). <paramref name="includeOverridden"/> also lists the ones Finance let through.
    /// </summary>
    Task<IReadOnlyList<MatchExceptionDto>> ListExceptionsAsync(bool includeOverridden = false, CancellationToken cancellationToken = default);

    /// <summary>Finance lets a mismatched line be approved, with a reason (audited). Never on a bill one entered.</summary>
    Task<MatchExceptionDto> OverrideAsync(Guid billLineId, MatchOverrideRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A bill request for what's been received and not yet billed on each PO line (quantity × PO price), for the bill entry
    /// screen to fill in. Throws when nothing is left to bill.
    /// </summary>
    Task<SaveBillRequest> BuildBillAsync(CreateBillFromPurchaseOrderRequest request, CancellationToken cancellationToken = default);
}

// ---- Commitments ----

public sealed record CommitmentLineDto(Guid PurchaseOrderId, string PurchaseOrderNumber, string VendorName, int LineNumber, string Description, Guid? WbsElementId, string? WbsCode, decimal Committed);

public sealed record WbsCommitmentDto(Guid WbsElementId, string WbsCode, decimal Committed);

public sealed record ProjectCommitmentsDto(Guid ProjectId, decimal Total, IReadOnlyList<WbsCommitmentDto> ByWbs, IReadOnlyList<CommitmentLineDto> Lines);

public interface ICommitmentService
{
    /// <summary>Open PO amounts (not yet billed on a posted bill) on the project. Finance, Admin or the project's manager.</summary>
    Task<ProjectCommitmentsDto> GetProjectCommitmentsAsync(Guid projectId, CancellationToken cancellationToken = default);
}

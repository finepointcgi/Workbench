namespace Workbench.Contracts.Purchasing;

// AP credits: credit memos (the vendor's credit for a return, overbilling or a rebate), debit memos (the company billing the
// vendor back), applying them to open bills, vendor refunds, retainage releases and the full trail of a bill. Memos are
// entered, approved and posted like bills (IApBillService with SaveBillRequest.Kind); this service is everything around them.

public enum CreditApplicationState { Scheduled, Applied, Reversed }

public enum RefundState { Received, Void }

/// <summary>A memo taken against a bill. Scheduled: netted in an open payment run, applied when the run is paid.</summary>
public sealed record CreditApplicationDto(
    Guid Id,
    Guid CreditId,
    string CreditNumber,
    BillKind CreditKind,
    string CreditInvoiceNumber,
    Guid BillId,
    string BillNumber,
    string BillInvoiceNumber,
    Guid VendorId,
    string VendorCode,
    decimal Amount,
    DateOnly AppliedOn,
    CreditApplicationState Status,
    Guid? RunId,
    string? RunNumber,
    string AppliedBy,
    string? ReversalReason);

/// <summary>A posted credit or debit memo with credit left; <see cref="Scheduled"/> is already netted in open payment runs.</summary>
public sealed record OpenCreditDto(BillSummaryDto Credit, decimal Scheduled)
{
    public decimal Available => Credit.Balance - Scheduled;
}

public sealed record ApplyCreditItem(Guid BillId, decimal Amount);

/// <summary>Applies a memo to open bills of the same vendor (up to each bill's open balance). The date defaults to today.</summary>
public sealed record ApplyCreditRequest(IReadOnlyList<ApplyCreditItem> Items, DateOnly? AppliedOn = null);

public sealed record CreditBillLineRequest(Guid BillLineId, decimal Amount);

/// <summary>
/// "Credit this bill": a draft memo linked to the bill, with the chosen lines (default: every line, for what isn't credited
/// yet) charged the same way. <see cref="CreditNumber"/> is the vendor's credit memo number (a debit memo defaults to ours).
/// </summary>
public sealed record CreditBillRequest(
    string? CreditNumber = null,
    DateOnly? Date = null,
    string? Reason = null,
    IReadOnlyList<CreditBillLineRequest>? Lines = null,
    BillKind Kind = BillKind.CreditMemo);

/// <summary>Money the vendor sent back against an open credit (posts debit cash, credit accounts payable).</summary>
public sealed record RecordRefundRequest(decimal Amount, DateOnly ReceivedOn, string Reference);

public sealed record RefundDto(Guid Id, Guid CreditId, decimal Amount, DateOnly ReceivedOn, string Reference, RefundState Status, Guid? JournalEntryId, string RecordedBy, string? VoidReason);

/// <summary>A posted subcontract bill's retainage: held, taken back by credit memos, released, and in releases not yet posted.</summary>
public sealed record RetainageBillDto(Guid BillId, string Number, string InvoiceNumber, DateOnly InvoiceDate, decimal Held, decimal Credited, decimal Released, decimal Pending)
{
    /// <summary>What can still be released.</summary>
    public decimal Open => Held - Credited - Released - Pending;
}

public sealed record RetainageReleaseItem(Guid BillId, decimal Amount);

/// <summary>
/// Releases retainage of one subcontract's posted bills: creates a payable to the vendor (debit AP retainage, credit accounts
/// payable once approved) and sends it for approval like a bill. Billed to date must be within the ceiling, and within the
/// funded value unless Finance gives <see cref="FundingOverrideReason"/>.
/// </summary>
public sealed record ReleaseRetainageRequest(
    Guid SubcontractId,
    IReadOnlyList<RetainageReleaseItem> Items,
    DateOnly Date,
    string? InvoiceNumber = null,
    string? Description = null,
    string? FundingOverrideReason = null);

public sealed record RelatedDocumentDto(Guid Id, string Number, BillKind Kind, string InvoiceNumber, DateOnly Date, decimal Amount, BillState Status, string Relation);

/// <summary>A journal entry of the document: its posting, project cost, use tax, reversals.</summary>
public sealed record BillJournalDto(Guid Id, string Number, DateOnly Date, string Description, string Status, decimal Amount, string What);

public sealed record BillHistoryDto(DateTime AtUtc, string Actor, string Action, string? Reason);

/// <summary>Everything that happened to a bill or memo: related documents, credits, refunds, journal entries and history.</summary>
public sealed record BillTrailDto(
    Guid BillId,
    IReadOnlyList<RelatedDocumentDto> Related,
    IReadOnlyList<CreditApplicationDto> Applications,
    IReadOnlyList<RefundDto> Refunds,
    IReadOnlyList<BillJournalDto> JournalEntries,
    IReadOnlyList<BillHistoryDto> History);

public interface IApCreditService
{
    /// <summary>"Credit this bill": a draft credit (or debit) memo for some or all of a posted bill's lines, to review and submit.</summary>
    Task<BillDto> CreditBillAsync(Guid billId, CreditBillRequest request, CancellationToken cancellationToken = default);

    /// <summary>Posted memos with credit left, optionally for one vendor.</summary>
    Task<IReadOnlyList<OpenCreditDto>> ListOpenCreditsAsync(Guid? vendorId = null, CancellationToken cancellationToken = default);

    /// <summary>Applies a posted memo to open bills of its vendor. No GL entry: both sides are accounts payable.</summary>
    Task<BillDto> ApplyAsync(Guid creditId, ApplyCreditRequest request, CancellationToken cancellationToken = default);

    /// <summary>Takes an applied credit back off its bill (reason required); returns the memo.</summary>
    Task<BillDto> UnapplyAsync(Guid applicationId, BillReasonRequest request, CancellationToken cancellationToken = default);

    /// <summary>Records a vendor refund of an open credit (Pay vendors permission).</summary>
    Task<BillDto> RecordRefundAsync(Guid creditId, RecordRefundRequest request, CancellationToken cancellationToken = default);

    /// <summary>Voids a refund (a bounced check): reverses its entry and reopens the credit.</summary>
    Task<BillDto> VoidRefundAsync(Guid refundId, BillReasonRequest request, CancellationToken cancellationToken = default);

    /// <summary>The subcontract's posted bills with retainage, and what's left to release on each.</summary>
    Task<IReadOnlyList<RetainageBillDto>> ListRetainageAsync(Guid subcontractId, CancellationToken cancellationToken = default);

    /// <summary>Creates a retainage release and submits it for approval; returns it.</summary>
    Task<BillDto> ReleaseRetainageAsync(ReleaseRetainageRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// A draft AP debit memo for a quality return to vendor (the quality debit memo): credits the receiving clearing account
    /// the return debited. The quality memo is marked applied with the AP memo's number.
    /// </summary>
    Task<BillDto> CreateFromQualityReturnAsync(Guid qualityDebitMemoId, CancellationToken cancellationToken = default);

    /// <summary>The full trail of a bill or memo.</summary>
    Task<BillTrailDto> GetTrailAsync(Guid billId, CancellationToken cancellationToken = default);
}

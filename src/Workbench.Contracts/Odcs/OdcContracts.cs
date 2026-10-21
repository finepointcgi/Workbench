namespace Workbench.Contracts.Odcs;

// Enums mirror Workbench.Domain.ProjectAccounting.OdcType / OdcStatus by name and value.

public enum OdcCostType { Travel, Materials, Equipment, Subcontract, Consultant, Other }

public enum OdcApprovalState { Draft, Submitted, Approved, Rejected }

/// <summary>An ODC: a vendor cost (invoice) or one line of an employee expense report.</summary>
public sealed record OdcEntryDto(
    Guid Id,
    Guid ProjectId,
    string ProjectCode,
    Guid WbsElementId,
    string WbsCode,
    string WbsName,
    OdcCostType Type,
    DateOnly IncurredOn,
    decimal Amount,
    string Description,
    string? Vendor,
    string? InvoiceNumber,
    Guid? EmployeeId,
    Guid? ExpenseReportId,
    bool IsUnallowable,
    string? UnallowableReason,
    Guid? ReceiptFileId,
    string? ReceiptFileName,
    OdcApprovalState Status,
    string SubmittedBy,
    DateTime CreatedAtUtc,
    DateTime? SubmittedAtUtc,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    string? RejectionReason,

    // Added by multicurrency: an expense paid in another currency (Amount is what it costs in the functional currency).
    string? CurrencyCode = null,
    decimal? TransactionAmount = null,
    decimal? ExchangeRate = null)
{
    /// <summary>True when the entry can be changed: drafts and rejected entries.</summary>
    public bool IsEditable => Status is OdcApprovalState.Draft or OdcApprovalState.Rejected;
}

/// <summary>Creates or updates a vendor ODC (subcontract invoice, materials, equipment, consultant).</summary>
public sealed record SaveVendorOdcRequest(
    Guid WbsElementId,
    OdcCostType Type,
    DateOnly IncurredOn,
    decimal Amount,
    string Description,
    string Vendor,
    string InvoiceNumber,
    bool IsUnallowable = false,
    string? UnallowableReason = null);

/// <summary>Filters for a project's ODC list. Dates are inclusive.</summary>
public sealed record OdcListQuery(
    DateOnly? From = null,
    DateOnly? To = null,
    OdcCostType? Type = null,
    OdcApprovalState? Status = null,
    bool IncludeExpenses = false);

public sealed record OdcTypeTotalDto(OdcCostType Type, int Count, decimal Amount, decimal AllowableAmount, decimal UnallowableAmount);

/// <summary>ODC entries plus totals. Allowable totals exclude unallowable costs (FAR 31.205).</summary>
public sealed record OdcListDto(
    IReadOnlyList<OdcEntryDto> Entries,
    IReadOnlyList<OdcTypeTotalDto> ByType,
    decimal Total,
    decimal AllowableTotal,
    decimal UnallowableTotal,
    decimal ApprovedAllowableTotal);

/// <summary>An open receipt. Dispose it to release the stream.</summary>
public sealed record ReceiptContent(string FileName, string ContentType, Stream Content) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

/// <summary>Expense policy values the UI shows (receipt threshold from config <c>Expenses:ReceiptThreshold</c>).</summary>
public sealed record ExpensePolicyDto(decimal ReceiptThreshold);

/// <summary>Vendor ODCs: entry by PMs and finance, submit for the project manager's approval, and project lists.</summary>
public interface IOdcService
{
    Task<OdcListDto> ListProjectOdcsAsync(Guid projectId, OdcListQuery? query = null, CancellationToken cancellationToken = default);

    Task<OdcEntryDto?> GetAsync(Guid odcId, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> CreateVendorOdcAsync(Guid projectId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Updates a draft or rejected vendor ODC. Approved entries are locked until reopened.</summary>
    Task<OdcEntryDto> UpdateVendorOdcAsync(Guid odcId, SaveVendorOdcRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft or rejected vendor ODC (audited).</summary>
    Task DeleteVendorOdcAsync(Guid odcId, CancellationToken cancellationToken = default);

    /// <summary>Attaches (or replaces) the receipt / invoice copy of a draft or rejected entry, vendor or expense line.</summary>
    Task<OdcEntryDto> AttachReceiptAsync(Guid odcId, string fileName, Stream content, CancellationToken cancellationToken = default);

    Task<ReceiptContent> OpenReceiptAsync(Guid odcId, CancellationToken cancellationToken = default);

    /// <summary>A URL the browser can open to download the receipt.</summary>
    string GetReceiptUrl(Guid odcId);

    /// <summary>Validates and submits a vendor ODC to the project manager.</summary>
    Task<OdcEntryDto> SubmitAsync(Guid odcId, CancellationToken cancellationToken = default);

    Task<ExpensePolicyDto> GetPolicyAsync(CancellationToken cancellationToken = default);
}

// ---- Expense reports ----

public sealed record ExpenseReportDto(
    Guid Id,
    Guid EmployeeId,
    string EmployeeName,
    string Title,
    string? Purpose,
    DateOnly? TripStart,
    DateOnly? TripEnd,
    OdcApprovalState Status,
    int LineCount,
    decimal Total,
    decimal AllowableTotal,
    DateTime CreatedAtUtc,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc,
    string? ApprovedBy,
    DateTime? ApprovedAtUtc,
    string? RejectionReason)
{
    public bool IsEditable => Status is OdcApprovalState.Draft or OdcApprovalState.Rejected;
}

public sealed record ExpenseReportDetailDto(ExpenseReportDto Report, IReadOnlyList<OdcEntryDto> Lines);

public sealed record SaveExpenseReportRequest(string Title, DateOnly? TripStart = null, DateOnly? TripEnd = null, string? Purpose = null);

/// <summary>An expense line: Travel or Other, charged to one of the employee's charge codes.</summary>
public sealed record SaveExpenseLineRequest(
    Guid WbsElementId,
    OdcCostType Type,
    DateOnly IncurredOn,
    decimal Amount,
    string Description,
    string? Vendor = null,
    bool IsUnallowable = false,
    string? UnallowableReason = null,

    /// <summary>
    /// Added by multicurrency: the currency the expense was paid in. <see cref="Amount"/> is then in that currency and is
    /// reimbursed in the functional currency at <see cref="ExchangeRate"/> (the card or receipt rate) or the spot rate on
    /// the date incurred.
    /// </summary>
    string? CurrencyCode = null,
    decimal? ExchangeRate = null,

    /// <summary>
    /// Added by receipts: a file from the employee's receipt library (<c>IReceiptLibraryService</c>) to attach as the line's
    /// receipt, so a receipt uploaded once (for example to fill the line) isn't uploaded again. Null keeps the current receipt.
    /// </summary>
    Guid? ReceiptFileId = null);

/// <summary>The signed-in employee's expense reports.</summary>
public interface IExpenseReportService
{
    Task<IReadOnlyList<ExpenseReportDto>> ListMyReportsAsync(CancellationToken cancellationToken = default);

    Task<ExpenseReportDetailDto> GetReportAsync(Guid reportId, CancellationToken cancellationToken = default);

    Task<ExpenseReportDto> CreateReportAsync(SaveExpenseReportRequest request, CancellationToken cancellationToken = default);

    Task<ExpenseReportDto> UpdateReportAsync(Guid reportId, SaveExpenseReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a draft or rejected report and its lines.</summary>
    Task DeleteReportAsync(Guid reportId, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> AddLineAsync(Guid reportId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> UpdateLineAsync(Guid reportId, Guid lineId, SaveExpenseLineRequest request, CancellationToken cancellationToken = default);

    Task RemoveLineAsync(Guid reportId, Guid lineId, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> AttachReceiptAsync(Guid reportId, Guid lineId, string fileName, Stream content, CancellationToken cancellationToken = default);

    /// <summary>Validates every line (receipts included) and submits the report to the employee's supervisor.</summary>
    Task<ExpenseReportDto> SubmitAsync(Guid reportId, CancellationToken cancellationToken = default);
}

// ---- Approvals ----

/// <summary>What waits for the signed-in approver: expense reports of their direct reports and vendor ODCs of their projects.</summary>
public sealed record OdcApprovalQueueDto(IReadOnlyList<ExpenseReportDetailDto> Reports, IReadOnlyList<OdcEntryDto> VendorOdcs);

public sealed record OdcDecisionRequest(string? Reason = null);

public sealed record SetUnallowableRequest(bool IsUnallowable, string? Reason = null);

/// <summary>
/// Approvals. Expense reports go to the employee's supervisor, vendor ODCs to the project manager (or the PM's
/// supervisor when the PM entered it). Nobody approves their own submission: that throws <see cref="UnauthorizedAccessException"/>,
/// as does acting on something routed to someone else. Wrong-state actions throw <see cref="InvalidOperationException"/>.
/// </summary>
public interface IOdcApprovalService
{
    Task<OdcApprovalQueueDto> GetMyQueueAsync(CancellationToken cancellationToken = default);

    /// <summary>Approved items routed to the signed-in approver, newest first, so they can be reopened.</summary>
    Task<OdcApprovalQueueDto> ListMyApprovedAsync(CancellationToken cancellationToken = default);

    Task<ExpenseReportDto> ApproveReportAsync(Guid reportId, string? comment = null, CancellationToken cancellationToken = default);

    Task<ExpenseReportDto> RejectReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Unlocks an approved or rejected report back to draft. The reason is required and audited.</summary>
    Task<ExpenseReportDto> ReopenReportAsync(Guid reportId, string reason, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> ApproveOdcAsync(Guid odcId, string? comment = null, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> RejectOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default);

    Task<OdcEntryDto> ReopenOdcAsync(Guid odcId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Flags (or clears) a submitted entry or expense line as unallowable during review.</summary>
    Task<OdcEntryDto> SetUnallowableAsync(Guid odcId, bool isUnallowable, string? reason, CancellationToken cancellationToken = default);
}

/// <summary>Totals shared by the server and the UI. Unallowable costs are recorded but excluded from allowable totals.</summary>
public static class OdcTotals
{
    public static OdcListDto Summarize(IReadOnlyList<OdcEntryDto> entries) =>
        new(entries,
            entries.GroupBy(e => e.Type).OrderBy(g => g.Key)
                .Select(g => new OdcTypeTotalDto(g.Key, g.Count(), g.Sum(e => e.Amount), Allowable(g), g.Where(e => e.IsUnallowable).Sum(e => e.Amount)))
                .ToList(),
            entries.Sum(e => e.Amount),
            Allowable(entries),
            entries.Where(e => e.IsUnallowable).Sum(e => e.Amount),
            Allowable(entries.Where(e => e.Status == OdcApprovalState.Approved)));

    public static decimal Allowable(IEnumerable<OdcEntryDto> entries) => entries.Where(e => !e.IsUnallowable).Sum(e => e.Amount);
}

namespace Workbench.Contracts.IncurredCost;

// The annual incurred cost submission (FAR 52.216-7(d)(2); the DCAA ICE model). Every schedule is a table of rows and
// columns so the UI, the print view and the .xlsx export render them the same way. Finance and administrators only.

public enum IncurredCostState { Draft, Reviewed, Certified, Submitted }

public enum IceColumnKind { Text, Amount, Rate, Hours, Date }

public enum IceRowKind { Detail, Section, Subtotal, Total }

public enum IceCheckSeverity { Info, Warning, Error }

/// <summary>
/// A column of a schedule. <see cref="Editable"/> columns are typed in where Workbench has no source field; the values
/// are saved with the package (<see cref="IceInputDto"/>), and a source value, when there is one, is the default.
/// </summary>
public sealed record IceColumnDto(string Key, string Header, IceColumnKind Kind, bool Editable = false);

/// <summary>
/// One row. Numeric cells (amounts, rates as fractions, hours) are in <see cref="Numbers"/>, text and dates
/// (<c>yyyy-MM-dd</c>) in <see cref="Text"/>, both keyed by column. A row with <see cref="Key"/> can take inputs.
/// </summary>
public sealed record IceRowDto(string Key, IceRowKind Kind, IReadOnlyDictionary<string, string?> Text, IReadOnlyDictionary<string, decimal> Numbers)
{
    public decimal Number(string column) => Numbers.TryGetValue(column, out var v) ? v : 0;

    public decimal? NumberOrNull(string column) => Numbers.TryGetValue(column, out var v) ? v : null;

    public string? TextOf(string column) => Text.TryGetValue(column, out var v) ? v : null;
}

/// <summary>A schedule: <see cref="Code"/> is A…O, H-1, U (unallowable costs) or Cover.</summary>
public sealed record IceScheduleDto(string Code, string Title, string Description, IReadOnlyList<IceColumnDto> Columns, IReadOnlyList<IceRowDto> Rows, IReadOnlyList<string> Notes)
{
    public IceRowDto? Row(string key) => Rows.FirstOrDefault(r => r.Key == key);

    public IceRowDto? TotalRow => Rows.LastOrDefault(r => r.Kind == IceRowKind.Total);
}

/// <summary>
/// A tie-out or completeness check. Errors (G doesn't reconcile to the GL, L doesn't tie to the invoices) must be fixed
/// before the package can be reviewed; warnings are listed for the reviewer.
/// </summary>
public sealed record IceCheckDto(string Code, IceCheckSeverity Severity, string Schedule, string Title, string Detail, decimal? Expected = null, decimal? Actual = null)
{
    public decimal? Difference => Expected is { } e && Actual is { } a ? a - e : null;
}

/// <summary>A headline figure of a schedule; regeneration compares these.</summary>
public sealed record IceTotalDto(string Schedule, string Key, string Label, decimal Value);

/// <summary>A total that changed when a draft was regenerated (null: it didn't exist before or doesn't now).</summary>
public sealed record IceTotalChangeDto(string Schedule, string Key, string Label, decimal? Before, decimal? After)
{
    public decimal Change => (After ?? 0) - (Before ?? 0);
}

public sealed record IceInputDto(string Schedule, string RowKey, string Column, string? Value);

public sealed record IncurredCostSummaryDto(
    Guid Id,
    int FiscalYear,
    IncurredCostState Status,
    int Revision,
    string GeneratedBy,
    DateTime GeneratedAtUtc,
    decimal ClaimedCost,
    int Errors,
    int Warnings,
    string? CertifiedBy,
    DateTime? CertifiedAtUtc,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc);

public sealed record IncurredCostPackageDto(
    Guid Id,
    int FiscalYear,
    DateOnly YearStart,
    DateOnly YearEnd,
    IncurredCostState Status,
    int Revision,
    string GeneratedBy,
    DateTime GeneratedAtUtc,
    string? ReviewedBy,
    DateTime? ReviewedAtUtc,
    string? CertifiedBy,
    DateTime? CertifiedAtUtc,
    string? CertifierName,
    string? CertifierTitle,
    string? CertificateText,
    string? SubmittedBy,
    DateTime? SubmittedAtUtc,
    string? SubmissionReference,
    IReadOnlyList<IceScheduleDto> Schedules,
    IReadOnlyList<IceCheckDto> Checks,
    IReadOnlyList<IceTotalDto> Totals,

    /// <summary>What the latest regeneration changed (empty when it changed nothing or never ran).</summary>
    IReadOnlyList<IceTotalChangeDto> LastChanges,
    IReadOnlyList<string> Notes)
{
    public IceScheduleDto Schedule(string code) => Schedules.First(s => s.Code == code);

    public decimal Total(string schedule, string key) => Totals.FirstOrDefault(t => t.Schedule == schedule && t.Key == key)?.Value ?? 0;

    public bool CanEdit => Status == IncurredCostState.Draft;

    public bool IsLocked => Status == IncurredCostState.Submitted;

    public int Errors => Checks.Count(c => c.Severity == IceCheckSeverity.Error);

    public int Warnings => Checks.Count(c => c.Severity == IceCheckSeverity.Warning);
}

public sealed record CreateIncurredCostRequest(int FiscalYear);

public sealed record SaveIceInputsRequest(IReadOnlyList<IceInputDto> Inputs);

/// <summary>
/// Certifies the package (Schedule N). <see cref="Acknowledged"/> must be true: the official has read the certificate,
/// which is recorded as shown with who certified and when.
/// </summary>
public sealed record CertifyIncurredCostRequest(string CertifierName, string CertifierTitle, bool Acknowledged);

public sealed record SubmitIncurredCostRequest(string? Reference = null);

/// <summary>Takes a reviewed or certified package back to draft (clearing the review and certification).</summary>
public sealed record ReopenIncurredCostRequest(string Reason);

public sealed record IncurredCostExportFile(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Incurred cost submissions, one per fiscal year. Draft → reviewed → certified → submitted; only drafts are regenerated
/// or edited, and a submitted package is locked. Wrong state is <see cref="InvalidOperationException"/> (409).
/// </summary>
public interface IIncurredCostService
{
    Task<IReadOnlyList<IncurredCostSummaryDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<IncurredCostPackageDto> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Generates a draft for a fiscal year (one package per year).</summary>
    Task<IncurredCostPackageDto> CreateAsync(CreateIncurredCostRequest request, CancellationToken cancellationToken = default);

    /// <summary>Regenerates a draft from the current books; <see cref="IncurredCostPackageDto.LastChanges"/> lists the totals that moved.</summary>
    Task<IncurredCostPackageDto> RegenerateAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Saves typed-in values (blank clears one). Drafts only; only editable columns of existing rows.</summary>
    Task<IncurredCostPackageDto> SaveInputsAsync(Guid id, SaveIceInputsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Draft → reviewed. Refused while any check is an error.</summary>
    Task<IncurredCostPackageDto> MarkReviewedAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IncurredCostPackageDto> ReopenAsync(Guid id, ReopenIncurredCostRequest request, CancellationToken cancellationToken = default);

    /// <summary>Reviewed → certified, recording the certifying official and the Schedule N text.</summary>
    Task<IncurredCostPackageDto> CertifyAsync(Guid id, CertifyIncurredCostRequest request, CancellationToken cancellationToken = default);

    /// <summary>Certified → submitted. The package is locked from then on.</summary>
    Task<IncurredCostPackageDto> SubmitAsync(Guid id, SubmitIncurredCostRequest request, CancellationToken cancellationToken = default);

    /// <summary>The package as an .xlsx workbook laid out like the DCAA ICE model, one sheet per schedule.</summary>
    Task<IncurredCostExportFile> ExportWorkbookAsync(Guid id, CancellationToken cancellationToken = default);
}

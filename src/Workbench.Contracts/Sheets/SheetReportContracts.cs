namespace Workbench.Contracts.Sheets;

/// <summary>How a report filter compares a row's value with <see cref="SheetReportFilterDto.Value"/>.</summary>
public enum SheetReportOperator
{
    Is,
    IsNot,
    Contains,
    DoesNotContain,
    IsBlank,
    IsNotBlank,
    GreaterThan,
    LessThan,
    OnOrAfter,
    OnOrBefore,
}

/// <summary>A filter on a column, by name (matched in every sheet, ignoring case); <c>Title</c>, <c>Start</c>, <c>Finish</c> and <c>Sheet</c> work too.</summary>
public sealed record SheetReportFilterDto(string Column, SheetReportOperator Operator, string? Value = null);

/// <summary>
/// A report pulling rows from several sheets, like a Smartsheet report: <paramref name="Columns"/> are column names,
/// matched by name in each sheet. <paramref name="Shared"/>: members of <paramref name="Workspace"/> can open it (each
/// sees only what they may); otherwise only its owner.
/// </summary>
public sealed record SheetReportDto(
    Guid Id, string Name, string OwnerEmail, bool Shared, Guid? WorkspaceId, string? Workspace,
    IReadOnlyList<Guid> SheetIds, IReadOnlyList<string> Columns, IReadOnlyList<SheetReportFilterDto> Filters, bool MatchAll,
    string? SortBy, bool SortDescending, bool CanEdit, DateTime UpdatedAtUtc);

public sealed record SaveSheetReportRequest(
    string Name, IReadOnlyList<Guid> SheetIds, IReadOnlyList<string> Columns, IReadOnlyList<SheetReportFilterDto>? Filters = null,
    bool MatchAll = true, string? SortBy = null, bool SortDescending = false, Guid? SharedWithWorkspaceId = null);

/// <summary>A column of the result: its name and the type it has in the first sheet that has it.</summary>
public sealed record SheetReportColumnDto(string Name, ColumnKind Type);

/// <summary>A row of the result, with the sheet and item it came from (open it at <c>sheets/{SheetId}?item={ItemId}</c>).</summary>
public sealed record SheetReportRowDto(Guid SheetId, string SheetName, Guid ItemId, string Title, IReadOnlyList<string?> Cells);

/// <summary>
/// The live result. <paramref name="HiddenSheets"/>: sheets in the report the user can't see (left out). Cells of columns a
/// sheet doesn't have, or that are hidden from the user there, are null.
/// </summary>
public sealed record SheetReportResultDto(
    SheetReportDto Report, IReadOnlyList<SheetReportColumnDto> Columns, IReadOnlyList<SheetReportRowDto> Rows, int HiddenSheets, bool Truncated);

public interface ISheetReportService
{
    /// <summary>The user's reports and those shared with them.</summary>
    Task<IReadOnlyList<SheetReportDto>> ListAsync(CancellationToken cancellationToken = default);

    Task<SheetReportDto?> GetAsync(Guid reportId, CancellationToken cancellationToken = default);

    Task<SheetReportDto> CreateAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default);

    /// <summary>Changes a report. Only its owner (or an administrator).</summary>
    Task<SheetReportDto> UpdateAsync(Guid reportId, SaveSheetReportRequest request, CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Runs a saved report as the current user.</summary>
    Task<SheetReportResultDto> RunAsync(Guid reportId, CancellationToken cancellationToken = default);

    /// <summary>Runs an unsaved definition (the report builder's preview) as the current user.</summary>
    Task<SheetReportResultDto> PreviewAsync(SaveSheetReportRequest request, CancellationToken cancellationToken = default);
}

namespace Workbench.Contracts.Sheets;

public sealed record SheetSummary(
    Guid Id,
    string Name,
    string? Description,
    string Workspace,
    int ItemCount,
    bool SchedulingEnabled,
    string? CostpointProjectId,
    DateTime UpdatedAtUtc);

public sealed record SheetDetail(SheetSummary Summary, IReadOnlyList<ColumnDto> Columns);

public sealed record CreateSheetRequest(string Name, string? Description, bool SchedulingEnabled);

/// <summary>
/// Sheet operations used by the UI. The web host binds this to in-process application
/// services; the desktop app binds it to an HTTP client against the server API.
/// </summary>
public interface ISheetService
{
    Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Returns the sheet with its columns, or null when it doesn't exist.</summary>
    Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default);

    Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default);
}

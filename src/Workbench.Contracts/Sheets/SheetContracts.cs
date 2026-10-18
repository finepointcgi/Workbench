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

/// <summary>A sheet and the columns the user may see. <paramref name="CanSeeRestricted"/>: they see restricted columns and may restrict columns.</summary>
public sealed record SheetDetail(SheetSummary Summary, IReadOnlyList<ColumnDto> Columns, bool CanSeeRestricted = false);

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

namespace Workbench.Contracts.Sheets;

/// <summary>
/// A sheet template in the New sheet gallery. Built-in templates have ids like <c>builtin:issue-tracker</c> and no
/// workspace; saved ("workspace") templates belong to the workspace of the sheet they were saved from and are shown to its members.
/// </summary>
public sealed record SheetTemplateDto(
    string Id, string Name, string Description, string Category, bool IsBuiltIn, string? Workspace, bool SchedulingEnabled,
    IReadOnlyList<string> ColumnNames, int RowCount, string? CreatedBy, bool CanDelete);

/// <summary>Creates a sheet from a template. <paramref name="IncludeRows"/>: also the template's rows (built-in ones have examples).</summary>
public sealed record CreateSheetFromTemplateRequest(string TemplateId, string Name, string? Description = null, bool IncludeRows = true);

/// <summary>Saves the sheet's columns (and optionally its rows) as a template for its workspace.</summary>
public sealed record SaveSheetTemplateRequest(string Name, string? Description = null, bool IncludeRows = false);

/// <summary>Copies a sheet: its columns, cross-sheet references and (optionally) rows, into a new sheet.</summary>
public sealed record CopySheetRequest(string Name, bool IncludeRows = true);

public interface ISheetTemplateService
{
    /// <summary>Built-in templates, then the workspace templates the user can see.</summary>
    Task<IReadOnlyList<SheetTemplateDto>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>A new sheet from the template, in a workspace the user can edit.</summary>
    Task<SheetSummary> CreateSheetAsync(CreateSheetFromTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Saves a template from the sheet. Needs Editor on it; columns hidden from the user are left out.</summary>
    Task<SheetTemplateDto> SaveAsTemplateAsync(Guid sheetId, SaveSheetTemplateRequest request, CancellationToken cancellationToken = default);

    /// <summary>Deletes a workspace template. Its creator, or an Owner of its workspace.</summary>
    Task DeleteAsync(Guid templateId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copies the sheet (Viewer is enough) into its own workspace when the user can edit there, otherwise one they can.
    /// Columns and values hidden from the user aren't copied.
    /// </summary>
    Task<SheetSummary> CopyAsync(Guid sheetId, CopySheetRequest request, CancellationToken cancellationToken = default);
}

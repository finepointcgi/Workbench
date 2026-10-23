namespace Workbench.Domain.Sheets;

/// <summary>
/// A named cross-sheet reference, used in this sheet's formulas as <c>{Name}</c>: one or more columns of another sheet
/// (every row, in outline order). Columns are kept by id, so renaming them doesn't break formulas. Evaluated for each
/// viewer: someone who can't see the other sheet (or one of the columns) gets #NO ACCESS.
/// </summary>
public sealed class SheetReference : Entity
{
    public Guid SheetId { get; set; }

    /// <summary>The name formulas use between braces; unique on the sheet, ignoring case.</summary>
    public required string Name { get; set; }

    public Guid SourceSheetId { get; set; }

    /// <summary>Field keys of the source sheet: column ids, or <c>title</c>, <c>start</c>, <c>finish</c>; left to right.</summary>
    public List<string> Fields { get; set; } = [];

    public string? CreatedBy { get; set; }

    public DateTime CreatedAtUtc { get; set; }
}

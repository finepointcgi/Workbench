namespace Workbench.Contracts.Sheets;

/// <summary>
/// A named cross-sheet reference: formulas on the sheet use it as <c>{Name}</c>. <paramref name="Fields"/> are the other
/// sheet's field keys, <paramref name="FieldNames"/> their current names. <paramref name="CanSee"/> is false when the user
/// can't see the other sheet (then its name and field names are left out, and the formula shows #NO ACCESS to them).
/// </summary>
public sealed record SheetReferenceDto(
    Guid Id, string Name, Guid SourceSheetId, string? SourceSheetName, IReadOnlyList<string> Fields, IReadOnlyList<string> FieldNames, bool CanSee);

/// <summary>Creates a reference to one or more columns (left to right) of another sheet.</summary>
public sealed record CreateSheetReferenceRequest(string Name, Guid SourceSheetId, IReadOnlyList<string> Fields);

/// <summary>
/// The result of checking a formula before saving it: the problem (plain words) or null, and what it gives on the first
/// row (<paramref name="SampleValue"/>, with <paramref name="Explanation"/> when that is an error value).
/// </summary>
public sealed record FormulaCheckDto(string? Problem, string? SampleRowTitle, string? SampleValue, string? Explanation);

public sealed record FormulaFunctionDto(string Name, string Category, string Syntax, string Description);

/// <summary>Formula help: cross-sheet references, checking a formula, and the function list.</summary>
public interface ISheetFormulaService
{
    /// <summary>The sheet's cross-sheet references. Needs Viewer.</summary>
    Task<IReadOnlyList<SheetReferenceDto>> ListReferencesAsync(Guid sheetId, CancellationToken cancellationToken = default);

    /// <summary>Adds a reference. Needs Editor on the sheet and access to the other sheet; names are unique on the sheet.</summary>
    Task<SheetReferenceDto> CreateReferenceAsync(Guid sheetId, CreateSheetReferenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>Removes a reference (formulas that use it show #REF!). Needs Editor.</summary>
    Task DeleteReferenceAsync(Guid sheetId, Guid referenceId, CancellationToken cancellationToken = default);

    /// <summary>Checks a formula and works it out on the first row, without saving. Needs Viewer.</summary>
    Task<FormulaCheckDto> CheckAsync(Guid sheetId, string formula, CancellationToken cancellationToken = default);

    /// <summary>Every function, for the formula help.</summary>
    Task<IReadOnlyList<FormulaFunctionDto>> ListFunctionsAsync(CancellationToken cancellationToken = default);
}

/// <summary>Plain-language meanings of formula error values, for tooltips (mirrors the formula engine's errors).</summary>
public static class FormulaErrorText
{
    public static string? Explain(string? value) => value switch
    {
        "#REF!" => "This formula uses a column or reference that doesn't exist. It may have been deleted.",
        "#DIV/0!" => "This formula divides by zero, or by a blank cell, so there's no answer.",
        "#VALUE!" => "A value isn't the right kind here: for example text where a number or date is needed.",
        "#NAME?" => "This formula can't be read. Open the column's formula to fix it.",
        "#CIRCULAR!" => "Two or more formulas depend on each other, so none of them can be worked out.",
        "#NO MATCH" => "The value you looked for isn't in the range.",
        "#NO ACCESS" => "This formula reads another sheet that you don't have access to, so its value is hidden from you.",
        _ => null,
    };
}

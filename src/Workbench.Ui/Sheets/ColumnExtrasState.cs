using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Sheets;

/// <summary>
/// What <see cref="ColumnExtras"/> collects for the column dialog: a formula, the sheet a link column points at, or a
/// lookup's link column and field. <see cref="Enabled"/> is false when the dialog has no sheet (then nothing changes).
/// </summary>
public sealed class ColumnExtrasState
{
    public bool Enabled { get; set; }

    public bool UseFormula { get; set; }

    public string Formula { get; set; } = "";

    public Guid? LinkSheetId { get; set; }

    public Guid? LookupLinkColumnId { get; set; }

    public string? LookupField { get; set; }

    public bool IsComplete(ColumnKind type) => !Enabled || type switch
    {
        ColumnKind.LinkedRow => LinkSheetId is not null,
        ColumnKind.Lookup => LookupLinkColumnId is not null && !string.IsNullOrEmpty(LookupField),
        _ => !UseFormula || Formula.Trim().Length > 0,
    };

    /// <summary>The Options a link or lookup column keeps its settings in; null for other types.</summary>
    public IReadOnlyList<string>? OptionsFor(ColumnKind type) => !Enabled ? null : type switch
    {
        ColumnKind.LinkedRow when LinkSheetId is { } sheet => [sheet.ToString()],
        ColumnKind.Lookup when LookupLinkColumnId is { } link && LookupField is { } field => [link.ToString(), field],
        _ => null,
    };

    /// <summary>The formula to save (null for none); without a sheet the column's own formula is kept as it was.</summary>
    public string? FormulaFor(ColumnKind type, ColumnDto? column) =>
        !Enabled ? column?.Formula
        : type is ColumnKind.LinkedRow or ColumnKind.Lookup || !UseFormula || Formula.Trim().Length == 0 ? null
        : Formula.Trim();

    public void Load(ColumnDto? column)
    {
        if (column is null)
        {
            return;
        }

        UseFormula = column.Formula is not null;
        Formula = column.Formula ?? "";
        if (column.Type == ColumnKind.LinkedRow && column.Options.Count > 0 && Guid.TryParse(column.Options[0], out var sheet))
        {
            LinkSheetId = sheet;
        }

        if (column.Type == ColumnKind.Lookup && column.Options.Count > 1 && Guid.TryParse(column.Options[0], out var link))
        {
            LookupLinkColumnId = link;
            LookupField = column.Options[1];
        }
    }
}

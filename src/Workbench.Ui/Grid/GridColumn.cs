using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

public enum GridColumnKind
{
    Title,
    Start,
    Finish,
    Wbs,
    Custom,
}

/// <summary>A column as the grid shows it: one of the built-in item fields or a sheet column.</summary>
public sealed record GridColumn(string Key, string Name, GridColumnKind Kind, ColumnDto? Column, int DefaultWidth)
{
    /// <summary>How values in this column are edited and displayed.</summary>
    public ColumnKind ValueKind => Kind switch
    {
        GridColumnKind.Start or GridColumnKind.Finish => ColumnKind.Date,
        GridColumnKind.Custom => Column!.Type,
        _ => ColumnKind.Text,
    };

    public bool IsFormula => Column?.Formula is not null;

    /// <summary>Formula columns are computed server-side and can't be edited in the grid.</summary>
    public bool ReadOnly => IsFormula;

    /// <summary>Title, then Start/Finish when scheduling is on, then the sheet's columns, then WBS.</summary>
    public static IReadOnlyList<GridColumn> For(SheetDetail sheet)
    {
        var columns = new List<GridColumn> { new("title", "Title", GridColumnKind.Title, null, 320) };
        if (sheet.Summary.SchedulingEnabled)
        {
            columns.Add(new("start", "Start", GridColumnKind.Start, null, 130));
            columns.Add(new("finish", "Finish", GridColumnKind.Finish, null, 130));
        }

        columns.AddRange(sheet.Columns
            .OrderBy(c => c.Order)
            .Select(c => new GridColumn(c.Id.ToString(), c.Name, GridColumnKind.Custom, c, DefaultWidthFor(c.Type))));
        columns.Add(new("wbs", "WBS", GridColumnKind.Wbs, null, 140));
        return columns;
    }

    private static int DefaultWidthFor(ColumnKind kind) => kind switch
    {
        ColumnKind.Checkbox => 100,
        ColumnKind.Date => 130,
        ColumnKind.Person => 200,
        _ => 150,
    };
}

/// <summary>A visible row: the item, its depth in the hierarchy and where it sits among its siblings.</summary>
public sealed record GridRow(ItemDto Item, int Depth, bool HasChildren, bool HasPreviousSibling);

/// <summary>What <see cref="ColumnDialog"/> returns. The type is ignored when editing an existing column.</summary>
public sealed record ColumnDialogResult(string Name, ColumnKind Type, IReadOnlyList<string> Options);

using Microsoft.AspNetCore.Components;
using Workbench.Contracts.Sheets;

namespace Workbench.Ui.Grid;

public enum GridColumnKind
{
    Title,
    Start,
    Finish,
    Wbs,
    Custom,

    /// <summary>The item's Assignees (task assignment), edited in a people-search dialog.</summary>
    Assignees,

    /// <summary>Working days from Start to Finish (the plan editor); typing it sets the Finish. 0 is a milestone.</summary>
    Duration,

    /// <summary>MS Project predecessor text over task IDs, e.g. <c>3, 5SS+2d</c> (the plan editor).</summary>
    Predecessors,

    /// <summary>Read-only: the task's start in the baseline the grid compares to (<see cref="SheetViewDefinition.BaselineId"/>).</summary>
    BaselineStart,

    /// <summary>Read-only: the task's finish in the compared baseline.</summary>
    BaselineFinish,

    /// <summary>Read-only: how many plan working days the finish has moved since the baseline (positive = later).</summary>
    FinishVariance,

    /// <summary>Read-only: working days the task can slip without moving the plan's finish (0 or less = critical).</summary>
    TotalFloat,

    /// <summary>Read-only: working days the task can slip without moving any successor.</summary>
    FreeFloat,

    /// <summary>A column another feature adds to the grid (<see cref="GridExtraColumns"/>); it draws its own cells.</summary>
    Extra,
}

/// <summary>
/// A column a page adds to the grid around it, e.g. the Plan tab's people, hours and cost. The grid places it after the
/// plan's date columns and treats it as read-only; <see cref="Cell"/> draws the cell (it may hold its own buttons) and
/// <see cref="Text"/> is what copying the cell gives.
/// </summary>
public sealed record GridExtraColumn(string Key, string Name, int Width, Func<ItemDto, RenderFragment?> Cell, Func<ItemDto, string?> Text, bool AlignRight = false);

/// <summary>Extra columns for the grid, cascaded by the page (a new instance redraws the grid).</summary>
public sealed record GridExtraColumns(IReadOnlyList<GridExtraColumn> Columns);

/// <summary>A column as the grid shows it: one of the built-in item fields or a sheet column.</summary>
public sealed record GridColumn(string Key, string Name, GridColumnKind Kind, ColumnDto? Column, int DefaultWidth, GridExtraColumn? Extra = null)
{
    /// <summary>How values in this column are edited and displayed.</summary>
    public ColumnKind ValueKind => Kind switch
    {
        GridColumnKind.Start or GridColumnKind.Finish or GridColumnKind.BaselineStart or GridColumnKind.BaselineFinish => ColumnKind.Date,
        GridColumnKind.FinishVariance or GridColumnKind.TotalFloat or GridColumnKind.FreeFloat => ColumnKind.Number,
        GridColumnKind.Custom => Column!.Type,
        _ => ColumnKind.Text,
    };

    public bool IsFormula => Column?.Formula is not null;

    public bool IsRestricted => Column?.IsEffectivelyRestricted == true;

    public const string AssigneesKey = "assignees";

    /// <summary>Formula and lookup columns are computed server-side and can't be edited in the grid; baseline columns are history.</summary>
    public bool ReadOnly => IsFormula || Column?.Type == ColumnKind.Lookup || IsBaseline || IsFloat || Extra is not null || Column is { } c && ColumnValues.IsAutomatic(c.Type);

    /// <summary>Total and free float, read from the plan's critical-path analysis.</summary>
    public bool IsFloat => Kind is GridColumnKind.TotalFloat or GridColumnKind.FreeFloat;

    public bool IsBaseline => Kind is GridColumnKind.BaselineStart or GridColumnKind.BaselineFinish or GridColumnKind.FinishVariance;

    /// <summary>
    /// Title, then Assignees (when shown), then Start/Finish when scheduling is on (with Duration and Predecessors when the
    /// plan is loaded, and the baseline columns when comparing to a baseline), then the sheet's columns, then WBS.
    /// </summary>
    public static IReadOnlyList<GridColumn> For(SheetDetail sheet, bool assignees = false, bool plan = false, bool baseline = false, bool floats = false,
        IReadOnlyList<GridExtraColumn>? extras = null)
    {
        var columns = new List<GridColumn> { new("title", "Title", GridColumnKind.Title, null, 320) };
        if (assignees)
        {
            columns.Add(new(AssigneesKey, "Assignees", GridColumnKind.Assignees, null, 170));
        }

        if (sheet.Summary.SchedulingEnabled)
        {
            columns.Add(new("start", "Start", GridColumnKind.Start, null, 130));
            columns.Add(new("finish", "Finish", GridColumnKind.Finish, null, 130));
            if (plan)
            {
                columns.Add(new("duration", "Duration", GridColumnKind.Duration, null, 100));
                columns.Add(new("predecessors", "Predecessors", GridColumnKind.Predecessors, null, 130));
            }

            if (baseline)
            {
                columns.Add(new(SheetFields.BaselineStart, "Baseline start", GridColumnKind.BaselineStart, null, 130));
                columns.Add(new(SheetFields.BaselineFinish, "Baseline finish", GridColumnKind.BaselineFinish, null, 130));
                columns.Add(new(SheetFields.FinishVariance, "Finish variance", GridColumnKind.FinishVariance, null, 130));
            }

            if (floats)
            {
                columns.Add(new(SheetFields.TotalFloat, "Total float", GridColumnKind.TotalFloat, null, 160));
                columns.Add(new(SheetFields.FreeFloat, "Free float", GridColumnKind.FreeFloat, null, 110));
            }
        }

        foreach (var extra in extras ?? [])
        {
            columns.Add(new(extra.Key, extra.Name, GridColumnKind.Extra, null, extra.Width, extra));
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
        ColumnKind.MultiSelect => 200,
        ColumnKind.Progress => 140,
        ColumnKind.Symbol => 130,
        ColumnKind.AutoNumber => 110,
        ColumnKind.CreatedDate or ColumnKind.ModifiedDate => 170,
        ColumnKind.CreatedBy or ColumnKind.ModifiedBy => 200,
        _ => 150,
    };
}

/// <summary>A visible row: the item, its depth in the hierarchy and where it sits among its siblings.</summary>
public sealed record GridRow(ItemDto Item, int Depth, bool HasChildren, bool HasPreviousSibling);

/// <summary>What <see cref="ColumnDialog"/> returns. The type is ignored when editing an existing column.</summary>
public sealed record ColumnDialogResult(string Name, ColumnKind Type, IReadOnlyList<string> Options, bool IsRestricted = false, IReadOnlyList<string>? OptionColors = null, string? Formula = null);

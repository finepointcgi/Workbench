using Microsoft.AspNetCore.Components;

namespace Workbench.Ui.Customization;

/// <summary>How a list column's values are compared, filtered, shown and exported.</summary>
public enum ListColumnKind
{
    Text,
    Number,
    Money,

    /// <summary>A fraction (0.25 = 25%).</summary>
    Percent,
    Date,
    Bool,
}

/// <summary>
/// One column a list page can show. <see cref="Value"/> is the raw value (sorting, filtering, grouping, export);
/// <see cref="Cell"/> optionally renders it richly (links, chips) on screen. A column the viewer can't see is never put in
/// the list's column set: leave it out (row-level rules) or give it a <see cref="PermissionKey"/> (field permissions).
/// </summary>
public sealed class ListColumn<T>
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required Func<T, object?> Value { get; init; }

    public ListColumnKind Kind { get; init; } = ListColumnKind.Text;

    /// <summary>On screen; null shows <see cref="ListViewEngine.Text"/> of the value.</summary>
    public RenderFragment<T>? Cell { get; init; }

    /// <summary>Text for export, grouping and quick search, when it differs from the value (an enum's label, a name for an id).</summary>
    public Func<T, string?>? Display { get; init; }

    /// <summary>In the standard view (the page as built).</summary>
    public bool Standard { get; init; } = true;

    /// <summary>A field permission the viewer needs to be offered the column.</summary>
    public string? PermissionKey { get; init; }

    /// <summary>A custom field from the field catalog (its values are read per row).</summary>
    public bool IsCustom { get; init; }

    /// <summary>Offered for grouping (text-like columns with few distinct values).</summary>
    public bool Groupable { get; init; } = true;

    public bool AlignRight => Kind is ListColumnKind.Number or ListColumnKind.Money or ListColumnKind.Percent;
}

/// <summary>A list page: its key (saved views belong to it), its entity type (for custom field columns) and its columns.</summary>
public sealed class ListDefinition<T>
{
    public required string ListKey { get; init; }

    public required string EntityType { get; init; }

    /// <summary>File name and title of exports.</summary>
    public required string Title { get; init; }

    /// <summary>Every column the viewer may see, standard ones in their built order.</summary>
    public required IReadOnlyList<ListColumn<T>> Columns { get; init; }

    /// <summary>The record id (custom field values).</summary>
    public required Func<T, Guid> Id { get; init; }

    /// <summary>The standard view's sort.</summary>
    public IReadOnlyList<Contracts.Customization.ListSort> StandardSort { get; init; } = [];

    /// <summary>The standard view's grouping.</summary>
    public string? StandardGroupBy { get; init; }

    /// <summary>The standard view: standard columns, standard sort.</summary>
    public Contracts.Customization.ListViewDefinition StandardView =>
        new(Columns.Where(c => c.Standard).Select(c => c.Key).ToList(), StandardSort, [], StandardGroupBy);
}

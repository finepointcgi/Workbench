using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Application.Sheets;

/// <summary>
/// Which columns of a sheet are restricted: those marked so, plus every formula column that reads one (on the same row
/// or its children, in a column range, directly or through another formula). Otherwise <c>[Budget] / [Planned hours]</c> would show the
/// budget to anyone who can do arithmetic.
/// </summary>
public static class RestrictedColumns
{
    public static IReadOnlySet<Guid> Of(IEnumerable<Column> columns)
    {
        var list = columns.ToList();
        var restricted = list.Where(c => c.IsRestricted).Select(c => c.Id).ToHashSet();
        if (restricted.Count == 0)
        {
            return restricted;
        }

        var reads = new Dictionary<Guid, IReadOnlySet<string>>();
        foreach (var column in list.Where(c => !string.IsNullOrWhiteSpace(c.Formula)))
        {
            try
            {
                var formula = Formula.Parse(column.Formula!);
                reads[column.Id] = formula.References.Concat(formula.ChildReferences)
                    .Concat(formula.ColumnRanges.SelectMany(r => Formulas.FormulaCalculator.RangeColumns(list, r.From, r.To)?.Select(c => c.Name) ?? []))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (FormulaParseException)
            {
                // Evaluates to an error anyway; reads nothing.
            }
        }

        for (var added = true; added;)
        {
            var names = list.Where(c => restricted.Contains(c.Id)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var more = reads.Where(r => !restricted.Contains(r.Key) && r.Value.Overlaps(names)).Select(r => r.Key).ToList();
            restricted.UnionWith(more);
            added = more.Count > 0;
        }

        return restricted;
    }

    /// <summary>The DTO, flagged as restricted directly or through the formula columns it reads.</summary>
    public static Contracts.Sheets.ColumnDto ToDto(Column column, IReadOnlySet<Guid> restricted) =>
        column.ToDto() with { IsRestricted = column.IsRestricted, ReadsRestricted = !column.IsRestricted && restricted.Contains(column.Id) };
}

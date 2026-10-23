using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Application.Sheets;

public sealed class ColumnService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock, SheetCalculator? calculator = null) : IColumnService
{
    private readonly SheetCalculator _calculator = calculator ?? new SheetCalculator(dbFactory, clock);

    public async Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);

        var nextOrder = await db.Columns.Where(c => c.SheetId == sheetId).MaxAsync(c => (int?)c.Order, cancellationToken) ?? -1;
        var type = request.Type.ToDomain();
        var formula = await ValidateFormulaAsync(db, sheetId, null, request.Name.Trim(), request.Formula, cancellationToken);
        if (formula is not null && type is ColumnType.LinkedRow or ColumnType.Lookup)
        {
            throw new ArgumentException($"A {(type == ColumnType.Lookup ? "lookup" : "link to row")} column can't have a formula.");
        }

        var column = new Column
        {
            SheetId = sheetId,
            Name = request.Name.Trim(),
            Type = type,
            Order = nextOrder + 1,
            Options = await ValidateOptionsAsync(db, sheetId, request.Name.Trim(), type, request.Options?.ToList() ?? [], cancellationToken),
            Formula = formula,
            IsRestricted = request.IsRestricted,
        };
        db.Columns.Add(column);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, column, cancellationToken);
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var column = await db.Columns.FirstOrDefaultAsync(c => c.Id == columnId && c.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Column", columnId);

        var oldName = column.Name;
        var newName = request.Name.Trim();
        var renamed = !string.Equals(oldName.Trim(), newName, StringComparison.Ordinal);
        if (renamed && !string.Equals(oldName.Trim(), newName, StringComparison.OrdinalIgnoreCase)
            && (await db.Columns.Where(c => c.SheetId == sheetId && c.Id != columnId).Select(c => c.Name).ToListAsync(cancellationToken))
                .Any(n => string.Equals(n.Trim(), newName, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"There's already a column named \"{newName}\". Pick another name so formulas know which one you mean.");
        }

        column.Name = newName;
        column.Options = column.Type is ColumnType.LinkedRow or ColumnType.Lookup && request.Options.Count == 0
            ? column.Options // a dialog that only renames sends no options; keep the link settings
            : await ValidateOptionsAsync(db, sheetId, newName, column.Type, request.Options.ToList(), cancellationToken, columnId);
        var requested = renamed && request.Formula is not null ? Formula.RenameColumn(request.Formula, oldName, newName) : request.Formula;
        column.Formula = column.Type is ColumnType.LinkedRow or ColumnType.Lookup
            ? null
            : await ValidateFormulaAsync(db, sheetId, columnId, column.Name, requested, cancellationToken);
        if (renamed)
        {
            await RewriteFormulasAsync(db, sheetId, columnId, oldName, newName, cancellationToken);
        }
        column.Order = request.Order;
        column.IsRestricted = request.IsRestricted ?? column.IsRestricted;
        if (column.Formula is not null)
        {
            // Formula values are computed on read; drop anything typed into the column before it became one.
            await RemoveValuesAsync(db, sheetId, columnId, cancellationToken);
        }

        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return await ToDtoAsync(db, column, cancellationToken);
    }

    /// <summary>The column's DTO, with whether it reads a restricted column (that depends on the sheet's other columns).</summary>
    private static async Task<ColumnDto> ToDtoAsync(IWorkbenchDbContext db, Column column, CancellationToken cancellationToken)
    {
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == column.SheetId && c.Id != column.Id).ToListAsync(cancellationToken);
        return RestrictedColumns.ToDto(column, RestrictedColumns.Of([.. columns, column]));
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var column = await db.Columns.FirstOrDefaultAsync(c => c.Id == columnId && c.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Column", columnId);

        await RemoveValuesAsync(db, sheetId, columnId, cancellationToken);
        db.Columns.Remove(column);
        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private static async Task RemoveValuesAsync(IWorkbenchDbContext db, Guid sheetId, Guid columnId, CancellationToken cancellationToken)
    {
        var key = columnId.ToString();
        var items = await db.Items.Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        foreach (var item in items.Where(i => i.Values.ContainsKey(key)))
        {
            item.Values = new Dictionary<string, string?>(item.Values.Where(kv => kv.Key != key));
        }
    }

    /// <summary>
    /// Renaming a column rewrites every formula on the sheet that names it (<c>[Old]</c>, <c>CHILDREN([Old])</c>,
    /// <c>[Old]:[Old]</c>), so they keep working instead of turning into #REF!. Cross-sheet references and lookups keep
    /// columns by id, so they need nothing.
    /// </summary>
    private static async Task RewriteFormulasAsync(
        IWorkbenchDbContext db, Guid sheetId, Guid columnId, string oldName, string newName, CancellationToken cancellationToken)
    {
        var others = await db.Columns.Where(c => c.SheetId == sheetId && c.Id != columnId && c.Formula != null).ToListAsync(cancellationToken);
        foreach (var other in others)
        {
            var rewritten = Formula.RenameColumn(other.Formula!, oldName, newName);
            if (rewritten != other.Formula)
            {
                other.Formula = rewritten;
            }
        }
    }

    /// <summary>
    /// Link and lookup columns keep their settings in Options: a link column the linked sheet's id (which the user must be
    /// able to see), a lookup the link column's id and the field to show. Other types keep their options as sent.
    /// </summary>
    private async Task<List<string>> ValidateOptionsAsync(
        IWorkbenchDbContext db, Guid sheetId, string name, ColumnType type, List<string> options, CancellationToken cancellationToken, Guid? columnId = null)
    {
        switch (type)
        {
            case ColumnType.LinkedRow:
            {
                if (options.Count == 0 || !Guid.TryParse(options[0], out var targetId))
                {
                    throw new ArgumentException($"Choose the sheet whose rows \"{name}\" links to.");
                }

                if (!await db.Sheets.AnyAsync(s => s.Id == targetId, cancellationToken) || !await _calculator.CanSeeSheetAsync(targetId, cancellationToken))
                {
                    throw new ArgumentException("You can only link to rows of a sheet you can see.");
                }

                return [targetId.ToString()];
            }

            case ColumnType.Lookup:
            {
                if (options.Count < 2 || !Guid.TryParse(options[0], out var linkId))
                {
                    throw new ArgumentException($"Choose the link column and the field \"{name}\" shows.");
                }

                var link = await db.Columns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == linkId && c.SheetId == sheetId && c.Id != columnId, cancellationToken);
                if (link?.Type != ColumnType.LinkedRow || SheetLinks.TargetOf(link) is not { } targetId)
                {
                    throw new ArgumentException("A lookup shows a field of the rows linked in a \"Link to row\" column of this sheet. Add that column first.");
                }

                var field = options[1].Trim().ToLowerInvariant();
                var known = SheetCalculator.BuiltInFields.Contains(field)
                    || (Guid.TryParse(field, out var fieldId) && await db.Columns.AnyAsync(c => c.Id == fieldId && c.SheetId == targetId, cancellationToken));
                if (!known)
                {
                    throw new ArgumentException("The linked sheet has no such column to look up.");
                }

                return [linkId.ToString(), field];
            }

            default:
                return options;
        }
    }

    /// <summary>Returns the trimmed formula (null for none), or throws <see cref="ArgumentException"/> explaining what's wrong.</summary>
    private static async Task<string?> ValidateFormulaAsync(
        IWorkbenchDbContext db, Guid sheetId, Guid? columnId, string columnName, string? formula, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(formula))
        {
            return null;
        }

        formula = formula.Trim();
        var names = await db.Columns
            .Where(c => c.SheetId == sheetId && c.Id != columnId)
            .Select(c => c.Name)
            .ToListAsync(cancellationToken);
        names.Add(columnName);
        var references = await db.Set<Domain.Sheets.SheetReference>().Where(r => r.SheetId == sheetId).Select(r => r.Name).ToListAsync(cancellationToken);
        if (Formula.Validate(formula, names, references) is { } problem)
        {
            throw new ArgumentException($"Invalid formula for column '{columnName}': {problem}");
        }

        return formula;
    }

    private async Task TouchSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, cancellationToken);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
    }
}

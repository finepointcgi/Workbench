using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Formulas;

namespace Workbench.Application.Sheets;

public sealed class ColumnService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock) : IColumnService
{
    public async Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var sheet = await db.Sheets.FirstOrDefaultAsync(s => s.Id == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Sheet", sheetId);

        var nextOrder = await db.Columns.Where(c => c.SheetId == sheetId).MaxAsync(c => (int?)c.Order, cancellationToken) ?? -1;
        var formula = await ValidateFormulaAsync(db, sheetId, null, request.Name.Trim(), request.Formula, cancellationToken);
        var column = new Column
        {
            SheetId = sheetId,
            Name = request.Name.Trim(),
            Type = request.Type.ToDomain(),
            Order = nextOrder + 1,
            Options = request.Options?.ToList() ?? [],
            OptionColors = request.Type == ColumnKind.Dropdown ? StatusLabels.Colors(request.Options ?? [], request.OptionColors ?? []) : [],
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

        column.Name = request.Name.Trim();
        if (column.Type == ColumnType.Dropdown)
        {
            column.OptionColors = StatusLabels.Colors(request.Options, request.OptionColors ?? StatusLabels.ByName(column.Options, column.OptionColors, request.Options));
        }

        column.Options = request.Options.ToList();
        column.Formula = await ValidateFormulaAsync(db, sheetId, columnId, column.Name, request.Formula, cancellationToken);
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
        if (Formula.Validate(formula, names) is { } problem)
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

using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Sheets;
using Workbench.Domain;

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
        var column = new Column
        {
            SheetId = sheetId,
            Name = request.Name.Trim(),
            Type = request.Type.ToDomain(),
            Order = nextOrder + 1,
            Options = request.Options?.ToList() ?? [],
            Formula = string.IsNullOrWhiteSpace(request.Formula) ? null : request.Formula,
        };
        db.Columns.Add(column);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return column.ToDto();
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var column = await db.Columns.FirstOrDefaultAsync(c => c.Id == columnId && c.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Column", columnId);

        column.Name = request.Name.Trim();
        column.Options = request.Options.ToList();
        column.Formula = string.IsNullOrWhiteSpace(request.Formula) ? null : request.Formula;
        column.Order = request.Order;
        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        return column.ToDto();
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var column = await db.Columns.FirstOrDefaultAsync(c => c.Id == columnId && c.SheetId == sheetId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Column", columnId);

        var key = columnId.ToString();
        var items = await db.Items.Where(i => i.SheetId == sheetId).ToListAsync(cancellationToken);
        foreach (var item in items.Where(i => i.Values.ContainsKey(key)))
        {
            item.Values = new Dictionary<string, string?>(item.Values.Where(kv => kv.Key != key));
        }

        db.Columns.Remove(column);
        await TouchSheetAsync(db, sheetId, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task TouchSheetAsync(IWorkbenchDbContext db, Guid sheetId, CancellationToken cancellationToken)
    {
        var sheet = await db.Sheets.FirstAsync(s => s.Id == sheetId, cancellationToken);
        sheet.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
    }
}

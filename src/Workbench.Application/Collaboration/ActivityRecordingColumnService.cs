using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;

namespace Workbench.Application.Collaboration;

/// <summary>Decorates <see cref="IColumnService"/> to record activity and publish live changes.</summary>
public sealed class ActivityRecordingColumnService(
    IColumnService inner,
    IWorkbenchDbContextFactory dbFactory,
    ActivityRecorder activity) : IColumnService
{
    public async Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        var created = await inner.CreateAsync(sheetId, request, cancellationToken);
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnCreated, $"Added column \"{created.Name}\" ({created.Type})",
            SheetChangeKind.ColumnCreated, created.Id);
        return created;
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        var oldName = await NameAsync(sheetId, columnId, cancellationToken);
        var updated = await inner.UpdateAsync(sheetId, columnId, request, cancellationToken);
        var summary = oldName is not null && oldName != updated.Name
            ? $"Renamed column \"{oldName}\" to \"{updated.Name}\""
            : $"Updated column \"{updated.Name}\"";
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnUpdated, summary, SheetChangeKind.ColumnUpdated, columnId);
        return updated;
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        var name = await NameAsync(sheetId, columnId, cancellationToken);
        await inner.DeleteAsync(sheetId, columnId, cancellationToken);
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnDeleted, $"Deleted column \"{name}\"", SheetChangeKind.ColumnDeleted, columnId);
    }

    private async Task<string?> NameAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Columns.Where(c => c.Id == columnId && c.SheetId == sheetId).Select(c => c.Name).FirstOrDefaultAsync(cancellationToken);
    }
}

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
        // Everyone on the sheet reads its activity: restricted columns stay unnamed there.
        var summary = created.IsEffectivelyRestricted ? "Added a restricted column" : $"Added column \"{created.Name}\" ({created.Type})";
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnCreated, summary,
            SheetChangeKind.ColumnCreated, created.Id);
        return created;
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        var (oldName, wasRestricted) = await DescribeAsync(sheetId, columnId, cancellationToken);
        var updated = await inner.UpdateAsync(sheetId, columnId, request, cancellationToken);
        var summary = (wasRestricted, updated.IsEffectivelyRestricted) switch
        {
            (false, true) => $"Restricted column \"{oldName}\" to Finance and owners",
            (true, false) => $"Lifted the restriction on column \"{updated.Name}\"",
            (true, true) => "Updated a restricted column",
            _ when oldName is not null && oldName != updated.Name => $"Renamed column \"{oldName}\" to \"{updated.Name}\"",
            _ => $"Updated column \"{updated.Name}\"",
        };
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnUpdated, summary, SheetChangeKind.ColumnUpdated, columnId);
        return updated;
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        var (name, restricted) = await DescribeAsync(sheetId, columnId, cancellationToken);
        await inner.DeleteAsync(sheetId, columnId, cancellationToken);
        await activity.RecordAsync(sheetId, null, ActivityKinds.ColumnDeleted, restricted ? "Deleted a restricted column" : $"Deleted column \"{name}\"",
            SheetChangeKind.ColumnDeleted, columnId);
    }

    private async Task<(string? Name, bool Restricted)> DescribeAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        return (columns.FirstOrDefault(c => c.Id == columnId)?.Name, Sheets.RestrictedColumns.Of(columns).Contains(columnId));
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Sheets;
using Workbench.Domain;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Collaboration;

/// <summary>
/// Decorates <see cref="IItemService"/>: records activity, publishes live changes, and removes the comments
/// and attachments of deleted items. Registered with <see cref="CollaborationServiceCollectionExtensions.Decorate{TService, TDecorator}"/>.
/// </summary>
public sealed class ActivityRecordingItemService(
    IItemService inner,
    IWorkbenchDbContextFactory dbFactory,
    ActivityRecorder activity,
    IBlobStore blobs) : IItemService
{
    public Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        inner.ListAsync(sheetId, cancellationToken);

    public async Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        var created = await inner.CreateAsync(sheetId, request, cancellationToken);
        await activity.RecordAsync(sheetId, created.Id, ActivityKinds.ItemCreated, $"Added \"{created.Title}\"", SheetChangeKind.ItemCreated);
        return created;
    }

    public async Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        Item? before;
        List<Column> columns;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            before = await db.Items.AsNoTracking().FirstOrDefaultAsync(i => i.Id == itemId && i.SheetId == sheetId, cancellationToken);
            columns = await db.Columns.AsNoTracking().Where(c => c.SheetId == sheetId).ToListAsync(cancellationToken);
        }

        var updated = await inner.UpdateAsync(sheetId, itemId, request, cancellationToken);
        var changes = before is null ? [] : Describe(before, updated, columns);
        if (changes.Count > 0)
        {
            await activity.RecordAsync(sheetId, itemId, ActivityKinds.ItemUpdated,
                $"Updated \"{updated.Title}\": {string.Join("; ", changes)}", SheetChangeKind.ItemUpdated);
        }

        return updated;
    }

    public async Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
    {
        var moved = await inner.MoveAsync(sheetId, itemId, request, cancellationToken);
        await activity.RecordAsync(sheetId, itemId, ActivityKinds.ItemMoved, $"Moved \"{moved.Title}\"", SheetChangeKind.ItemMoved);
        return moved;
    }

    public async Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        string? title;
        HashSet<Guid> doomed;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var items = await db.Items.AsNoTracking().Where(i => i.SheetId == sheetId)
                .Select(i => new { i.Id, i.ParentId, i.Title }).ToListAsync(cancellationToken);
            title = items.FirstOrDefault(i => i.Id == itemId)?.Title;
            doomed = [itemId];
            for (var added = true; added;)
            {
                var children = items.Where(i => i.ParentId is { } p && doomed.Contains(p) && !doomed.Contains(i.Id)).Select(i => i.Id).ToList();
                doomed.UnionWith(children);
                added = children.Count > 0;
            }
        }

        await inner.DeleteAsync(sheetId, itemId, cancellationToken);
        await RemoveCollaborationDataAsync(sheetId, doomed);

        var summary = $"Deleted \"{title}\"" + (doomed.Count > 1 ? $" and {doomed.Count - 1} subitem{(doomed.Count == 2 ? "" : "s")}" : "");
        await activity.RecordAsync(sheetId, itemId, ActivityKinds.ItemDeleted, summary, SheetChangeKind.ItemDeleted);
    }

    private async Task RemoveCollaborationDataAsync(Guid sheetId, HashSet<Guid> itemIds)
    {
        var ids = itemIds.ToList();
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var comments = await db.Set<Comment>().Where(c => c.SheetId == sheetId && ids.Contains(c.ItemId)).ToListAsync();
        var attachments = await db.Set<Attachment>().Where(a => a.SheetId == sheetId && ids.Contains(a.ItemId)).ToListAsync();
        db.Set<Comment>().RemoveRange(comments);
        db.Set<Attachment>().RemoveRange(attachments);
        await db.SaveChangesAsync();
        foreach (var attachment in attachments)
        {
            await blobs.DeleteAsync(attachment.StorageKey);
        }
    }

    /// <summary>Human-readable field changes, e.g. <c>Status: "In progress" → "Done"</c>.</summary>
    internal static List<string> Describe(Item before, ItemDto after, IReadOnlyList<Column> columns)
    {
        var changes = new List<string>();
        Add("Title", before.Title, after.Title);
        Add("Start", before.Start?.ToString("yyyy-MM-dd"), after.Start?.ToString("yyyy-MM-dd"));
        Add("Finish", before.Finish?.ToString("yyyy-MM-dd"), after.Finish?.ToString("yyyy-MM-dd"));
        Add("WBS", before.CostpointWbsId, after.CostpointWbsId);

        var keys = before.Values.Keys.Union(after.Values.Keys);
        foreach (var key in keys)
        {
            var name = columns.FirstOrDefault(c => c.Id.ToString() == key)?.Name;
            if (name is null)
            {
                continue;
            }

            Add(name, before.Values.GetValueOrDefault(key), after.Values.GetValueOrDefault(key));
        }

        return changes;

        void Add(string field, string? from, string? to)
        {
            from = string.IsNullOrEmpty(from) ? null : from;
            to = string.IsNullOrEmpty(to) ? null : to;
            if (from != to)
            {
                changes.Add($"{field}: {Quote(from)} → {Quote(to)}");
            }
        }

        static string Quote(string? value) => value is null ? "(empty)" : $"\"{value}\"";
    }
}

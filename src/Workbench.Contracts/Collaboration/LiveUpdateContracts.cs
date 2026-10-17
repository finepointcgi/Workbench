namespace Workbench.Contracts.Collaboration;

public enum SheetChangeKind
{
    ItemCreated,
    ItemUpdated,
    ItemDeleted,
    ItemMoved,
    ColumnCreated,
    ColumnUpdated,
    ColumnDeleted,
    CommentAdded,
    CommentUpdated,
    CommentDeleted,
    AttachmentAdded,
    AttachmentDeleted,
}

/// <summary>Something changed in a sheet. <see cref="ItemId"/> / <see cref="ColumnId"/> are set when relevant.</summary>
public sealed record SheetChange(Guid SheetId, SheetChangeKind Kind, Guid? ItemId, Guid? ColumnId, string Actor, DateTime OccurredAtUtc);

/// <summary>
/// Live change notifications for a sheet. The web host delivers them in-process; the desktop app
/// receives them over SignalR from <c>/hubs/sheets</c>. Handlers may run on any thread.
/// </summary>
public interface ISheetLiveUpdates
{
    /// <summary>Calls <paramref name="handler"/> for every change to the sheet until the result is disposed.</summary>
    Task<IAsyncDisposable> SubscribeAsync(Guid sheetId, Func<SheetChange, Task> handler, CancellationToken cancellationToken = default);
}

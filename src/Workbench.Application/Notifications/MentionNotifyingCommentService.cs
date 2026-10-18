using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Collaboration;

namespace Workbench.Application.Notifications;

/// <summary>
/// Decorates <see cref="ICommentService"/>: notifies people @mentioned in a new comment, or newly mentioned in an
/// edit. The author is never notified about their own comment.
/// </summary>
public sealed class MentionNotifyingCommentService(
    ICommentService inner,
    IWorkbenchDbContextFactory dbFactory,
    Notifier notifier,
    ICurrentUser currentUser) : ICommentService
{
    public Task<IReadOnlyList<CommentDto>> ListAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default) =>
        inner.ListAsync(sheetId, itemId, cancellationToken);

    public async Task<CommentDto> CreateAsync(Guid sheetId, Guid itemId, CreateCommentRequest request, CancellationToken cancellationToken = default)
    {
        var created = await inner.CreateAsync(sheetId, itemId, request, cancellationToken);
        await NotifyAsync(sheetId, created, created.Mentions);
        return created;
    }

    public async Task<CommentDto> UpdateAsync(Guid sheetId, Guid commentId, UpdateCommentRequest request, CancellationToken cancellationToken = default)
    {
        List<string> before;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            before = await db.Set<Comment>().AsNoTracking()
                .Where(c => c.Id == commentId && c.SheetId == sheetId)
                .Select(c => c.Mentions)
                .FirstOrDefaultAsync(cancellationToken) ?? [];
        }

        var updated = await inner.UpdateAsync(sheetId, commentId, request, cancellationToken);
        await NotifyAsync(sheetId, updated, updated.Mentions.Except(before, StringComparer.OrdinalIgnoreCase));
        return updated;
    }

    public Task DeleteAsync(Guid sheetId, Guid commentId, CancellationToken cancellationToken = default) =>
        inner.DeleteAsync(sheetId, commentId, cancellationToken);

    private async Task NotifyAsync(Guid sheetId, CommentDto comment, IEnumerable<string> mentioned)
    {
        var author = currentUser.Email;
        var recipients = mentioned.Where(m => !string.Equals(m, author, StringComparison.OrdinalIgnoreCase)).ToList();
        if (recipients.Count == 0)
        {
            return;
        }

        string itemTitle;
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            itemTitle = await db.Items.Where(i => i.Id == comment.ItemId).Select(i => i.Title).FirstOrDefaultAsync() ?? "an item";
        }

        await notifier.NotifyAsync(recipients, new NotificationRequest(
            NotificationKinds.Mention,
            $"{author} mentioned you on \"{itemTitle}\"",
            comment.Body,
            sheetId,
            comment.ItemId));
    }
}

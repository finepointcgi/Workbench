using Microsoft.EntityFrameworkCore;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using DomainDelivery = Workbench.Domain.Notifications.NotificationDelivery;

namespace Workbench.Application.Notifications;

public sealed class NotificationService(IWorkbenchDbContextFactory dbFactory, ICurrentUser currentUser, TimeProvider clock) : INotificationService
{
    public const int MaxPageSize = 100;

    public async Task<NotificationPage> ListMineAsync(int skip = 0, int take = 20, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, MaxPageSize);
        var me = Me;

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var mine = db.Set<Notification>().AsNoTracking().Where(n => n.RecipientEmail == me);
        var filtered = unreadOnly ? mine.Where(n => n.ReadAtUtc == null) : mine;
        var total = await filtered.CountAsync(cancellationToken);
        var unread = unreadOnly ? total : await mine.CountAsync(n => n.ReadAtUtc == null, cancellationToken);
        var page = await filtered
            .OrderByDescending(n => n.CreatedAtUtc)
            .ThenByDescending(n => n.Id)
            .Skip(skip)
            .Take(take)
            .ToListAsync(cancellationToken);
        return new NotificationPage(page.Select(n => n.ToDto()).ToList(), total, unread);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        var me = Me;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<Notification>().CountAsync(n => n.RecipientEmail == me && n.ReadAtUtc == null, cancellationToken);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        var me = Me;
        await using var db = await dbFactory.CreateAsync(cancellationToken);

        // Someone else's notification is reported as missing, not forbidden, so ids can't be probed.
        var notification = await db.Set<Notification>().FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientEmail == me, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Notification", notificationId);
        notification.ReadAtUtc ??= clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        var me = Me;
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var unread = await db.Set<Notification>().Where(n => n.RecipientEmail == me && n.ReadAtUtc == null).ToListAsync(cancellationToken);
        foreach (var notification in unread)
        {
            notification.ReadAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    public async Task<NotificationPreferenceDto> GetPreferenceAsync(CancellationToken cancellationToken = default)
    {
        var me = Me;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var preference = await db.Set<NotificationPreference>().AsNoTracking().FirstOrDefaultAsync(p => p.UserEmail == me, cancellationToken);
        return new NotificationPreferenceDto((Contracts.Notifications.NotificationDelivery)(int)(preference?.Delivery ?? DomainDelivery.DailyDigest));
    }

    public async Task<NotificationPreferenceDto> SetPreferenceAsync(NotificationPreferenceDto preference, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(preference.Delivery))
        {
            throw new ArgumentException("Unknown delivery option.", nameof(preference));
        }

        var me = Me;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<NotificationPreference>().FirstOrDefaultAsync(p => p.UserEmail == me, cancellationToken);
        if (row is null)
        {
            row = new NotificationPreference { UserEmail = me };
            db.Set<NotificationPreference>().Add(row);
        }

        row.Delivery = (DomainDelivery)(int)preference.Delivery;
        await db.SaveChangesAsync(cancellationToken);
        return preference;
    }

    private string Me => currentUser.Email.Trim().ToLowerInvariant();
}

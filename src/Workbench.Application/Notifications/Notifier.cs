using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using DomainDelivery = Workbench.Domain.Notifications.NotificationDelivery;

namespace Workbench.Application.Notifications;

public sealed record NotificationRequest(string Kind, string Title, string Body, Guid? SheetId, Guid? ItemId);

/// <summary>
/// Creates notifications: saves one per recipient, pushes it to the live feed and emails people who chose
/// immediate delivery. Rules, @mentions and assignments all go through here.
/// </summary>
public sealed class Notifier(
    IWorkbenchDbContextFactory dbFactory,
    NotificationHub hub,
    IEmailSender email,
    NotificationOptions options,
    TimeProvider clock,
    ILogger<Notifier>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Notifies each distinct, valid email once. Returns the recipients notified.</summary>
    public async Task<IReadOnlyList<string>> NotifyAsync(IEnumerable<string?> recipients, NotificationRequest request)
    {
        var emails = recipients.Select(Normalize).OfType<string>().Distinct().ToList();
        if (emails.Count == 0)
        {
            return [];
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var link = request.SheetId is { } sheetId
            ? request.ItemId is { } itemId ? $"sheets/{sheetId}?item={itemId}" : $"sheets/{sheetId}"
            : null;
        var created = emails.Select(e => new Notification
        {
            RecipientEmail = e,
            Kind = request.Kind,
            Title = Truncate(request.Title, 500),
            Body = Truncate(request.Body, 4_000),
            Link = link,
            SheetId = request.SheetId,
            ItemId = request.ItemId,
            CreatedAtUtc = now,
        }).ToList();

        // The change that caused this is already saved, so don't let a cancelled request drop its notifications.
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            db.Set<Notification>().AddRange(created);
            await db.SaveChangesAsync(CancellationToken.None);

            var immediate = await db.Set<NotificationPreference>()
                .Where(p => emails.Contains(p.UserEmail) && p.Delivery == DomainDelivery.ImmediateEmail)
                .Select(p => p.UserEmail)
                .ToListAsync();
            var sent = false;
            foreach (var notification in created.Where(n => immediate.Contains(n.RecipientEmail)))
            {
                try
                {
                    await email.SendAsync(NotificationEmails.Single(notification, options));
                    notification.EmailedAtUtc = clock.GetUtcNow().UtcDateTime;
                    sent = true;
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "Couldn't email notification {NotificationId} to {Recipient}.", notification.Id, notification.RecipientEmail);
                }
            }

            if (sent)
            {
                await db.SaveChangesAsync(CancellationToken.None);
            }
        }

        foreach (var notification in created)
        {
            await hub.PublishAsync(notification.RecipientEmail, notification.ToDto());
        }

        return emails;
    }

    /// <summary>A trimmed lower-case email, or null when it doesn't look like one.</summary>
    public static string? Normalize(string? email)
    {
        var value = email?.Trim().ToLowerInvariant();
        return value is { Length: > 2 and <= 320 } && value.IndexOf('@') is > 0 and var at && at < value.Length - 1 && !value.Contains(' ')
            ? value
            : null;
    }

    private static string Truncate(string value, int max) => value.Length > max ? value[..(max - 1)] + "…" : value;
}

internal static class NotificationMapping
{
    public static NotificationDto ToDto(this Notification n) =>
        new(n.Id, n.Kind, n.Title, n.Body, n.Link, n.SheetId, n.ItemId, n.CreatedAtUtc, n.ReadAtUtc);
}

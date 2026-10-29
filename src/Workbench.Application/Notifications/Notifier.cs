using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using DomainDelivery = Workbench.Domain.Notifications.NotificationDelivery;

namespace Workbench.Application.Notifications;

/// <summary>
/// A notification to create. <see cref="Link"/> is app-relative (e.g. <c>time/2026-10-12</c>); when null and
/// <see cref="SheetId"/> is set, it links to the sheet (and item).
/// </summary>
public sealed record NotificationRequest(string Kind, string Title, string Body, Guid? SheetId, Guid? ItemId, string? Link = null);

/// <summary>
/// Creates notifications: saves one per recipient, pushes it to the live feed and emails people who chose
/// immediate delivery. Rules, @mentions and assignments all go through here; other features use it as
/// <see cref="INotificationPublisher"/>.
/// </summary>
public sealed class Notifier(
    IWorkbenchDbContextFactory dbFactory,
    NotificationHub hub,
    IEmailSender email,
    NotificationOptions options,
    TimeProvider clock,
    ILogger<Notifier>? logger = null,
    INotificationEmailExtras? extras = null,
    IServiceProvider? services = null) : INotificationPublisher
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

        ArgumentException.ThrowIfNullOrWhiteSpace(request.Kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Title);
        var now = clock.GetUtcNow().UtcDateTime;
        var link = CleanLink(request.Link) ?? (request.SheetId is { } sheetId
            ? request.ItemId is { } itemId ? $"sheets/{sheetId}?item={itemId}" : $"sheets/{sheetId}"
            : null);
        var created = emails.Select(e => new Notification
        {
            RecipientEmail = e,
            Kind = request.Kind,
            Title = Truncate(request.Title, 500),
            Body = Truncate(request.Body ?? "", 4_000),
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
                    var extra = extras is null ? null : await extras.ForSingleAsync(notification, CancellationToken.None);
                    await email.SendAsync(NotificationEmails.Single(notification, options, extra));
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

        // Also to the Teams channel for the people who chose it; posts in the background and never fails the caller.
        if (ChatSender() is { } chat)
        {
            await chat.NotifyAsync(created);
        }

        foreach (var notification in created)
        {
            await hub.PublishAsync(notification.RecipientEmail, notification.ToDto());
        }

        return emails;
    }

    /// <summary>Resolved on use, so a host without the integrations (or their configuration) still sends notifications.</summary>
    private ChatNotificationSender? ChatSender()
    {
        try
        {
            return (ChatNotificationSender?)services?.GetService(typeof(ChatNotificationSender));
        }
        catch (Exception ex) when (ex is InvalidOperationException)
        {
            _log.LogDebug(ex, "Chat notifications aren't available on this host.");
            return null;
        }
    }

    public async Task<bool> PublishAsync(
        string recipientEmail,
        string kind,
        string title,
        string? body = null,
        string? link = null,
        Guid? sheetId = null,
        Guid? itemId = null) =>
        (await NotifyAsync([recipientEmail], new NotificationRequest(kind, title, body ?? "", sheetId, itemId, link))).Count > 0;

    public Task<IReadOnlyList<string>> PublishAsync(
        IEnumerable<string?> recipientEmails,
        string kind,
        string title,
        string? body = null,
        string? link = null,
        Guid? sheetId = null,
        Guid? itemId = null) =>
        NotifyAsync(recipientEmails, new NotificationRequest(kind, title, body ?? "", sheetId, itemId, link));

    /// <summary>Links must stay inside the app (the bell navigates to them), so absolute URLs are refused.</summary>
    private static string? CleanLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            return null;
        }

        // Leading slashes are trimmed so "//evil.example" can't become a protocol-relative URL.
        link = link.Trim().TrimStart('/', '\\');
        if (Uri.TryCreate(link, UriKind.Absolute, out _))
        {
            throw new ArgumentException("A notification link must be app-relative, e.g. \"time/2026-10-12\".", nameof(link));
        }

        return link.Length > 500 ? throw new ArgumentException("A notification link can be at most 500 characters.", nameof(link)) : link;
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

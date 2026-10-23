using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>
/// Sends each daily-digest user one email a day with their unread notifications that haven't been emailed yet.
/// People without a preference row get the digest (the default).
/// </summary>
public sealed class DigestSender(
    IWorkbenchDbContextFactory dbFactory,
    IEmailSender email,
    NotificationOptions options,
    TimeProvider clock,
    ILogger<DigestSender>? logger = null,
    INotificationEmailExtras? extras = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;

    /// <summary>Sends the digests that are due now. Returns how many emails went out.</summary>
    public async Task<int> SendDueAsync(CancellationToken cancellationToken = default)
    {
        var localNow = clock.GetLocalNow();
        if (localNow.Hour < options.DigestHourLocal)
        {
            return 0;
        }

        var today = DateOnly.FromDateTime(localNow.DateTime);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var notDigest = db.Set<NotificationPreference>()
            .Where(p => p.Delivery != NotificationDelivery.DailyDigest)
            .Select(p => p.UserEmail);
        var pending = await db.Set<Notification>()
            .Where(n => n.ReadAtUtc == null && n.EmailedAtUtc == null && !notDigest.Contains(n.RecipientEmail))
            .OrderBy(n => n.CreatedAtUtc)
            .ToListAsync(cancellationToken);
        if (pending.Count == 0)
        {
            return 0;
        }

        var recipients = pending.Select(n => n.RecipientEmail).Distinct().ToList();
        var preferences = await db.Set<NotificationPreference>()
            .Where(p => recipients.Contains(p.UserEmail))
            .ToDictionaryAsync(p => p.UserEmail, cancellationToken);

        var sent = 0;
        foreach (var group in pending.GroupBy(n => n.RecipientEmail))
        {
            var preference = preferences.GetValueOrDefault(group.Key);
            if (preference is not null && preference.Delivery != NotificationDelivery.DailyDigest)
            {
                continue;
            }

            if (preference?.LastDigestSentUtc is { } last && LocalDate(last) >= today)
            {
                continue;
            }

            var notifications = group.ToList();
            try
            {
                var extra = extras is null ? null : await extras.ForDigestAsync(group.Key, notifications, cancellationToken);
                await email.SendAsync(NotificationEmails.Digest(group.Key, notifications, options, extra), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning(ex, "Couldn't send the notification digest to {Recipient}.", group.Key);
                continue;
            }

            var now = clock.GetUtcNow().UtcDateTime;
            foreach (var notification in notifications)
            {
                notification.EmailedAtUtc = now;
            }

            if (preference is null)
            {
                preference = new NotificationPreference { UserEmail = group.Key };
                db.Set<NotificationPreference>().Add(preference);
            }

            preference.LastDigestSentUtc = now;
            await db.SaveChangesAsync(cancellationToken);
            sent++;
        }

        return sent;
    }

    private DateOnly LocalDate(DateTime utc) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), clock.LocalTimeZone));
}

/// <summary>Checks hourly for digests that are due.</summary>
public sealed class DigestBackgroundService(IServiceProvider services, Tenancy.ITenantJobRunner tenants, TimeProvider clock, ILogger<DigestBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(1), clock);
        do
        {
            try
            {
                await tenants.ForEachTenantAsync(async ct =>
                {
                    var sent = await services.GetRequiredService<DigestSender>().SendDueAsync(ct);
                    if (sent > 0)
                    {
                        logger.LogInformation("Sent {Count} notification digests", sent);
                    }
                }, stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Notification digest run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

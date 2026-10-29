using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Connectors;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Identity;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>
/// One chat product a notification can also be posted to (Teams today). Adding Slack is one more implementation: say which hosts its
/// incoming webhooks live on and how a <see cref="ChatMessage"/> looks, then register it with <c>AddSingleton&lt;IChatChannelFormat, ...&gt;</c>.
/// </summary>
public interface IChatChannelFormat
{
    /// <summary>The <see cref="ChatChannelKinds"/> value stored on the channel.</summary>
    string Kind { get; }

    string Name { get; }

    /// <summary>True when an incoming-webhook address on this host belongs to the product. Anything else is refused, so a pasted address can't point at an arbitrary server.</summary>
    bool IsAllowedHost(string host);

    JsonObject Format(ChatMessage message);

    /// <summary>What an administrator is told to paste, in plain words.</summary>
    string HostHelp { get; }
}

/// <summary>Teams: Workflows ("Post to a channel when a webhook request is received") and the older Office 365 connector addresses.</summary>
public sealed class TeamsChannelFormat : IChatChannelFormat
{
    private static readonly string[] Suffixes =
        [".webhook.office.com", ".logic.azure.com", ".powerplatform.com", ".office.com", ".office365.com", ".microsoft.com"];

    public string Kind => ChatChannelKinds.Teams;
    public string Name => "Microsoft Teams";
    public string HostHelp => "a Microsoft Teams Workflows web address (it contains logic.azure.com or powerplatform.com) or a Teams connector address (webhook.office.com)";

    public bool IsAllowedHost(string host)
    {
        host = host.Trim().TrimEnd('.').ToLowerInvariant();
        return Suffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal)) || host is "outlook.office.com" or "outlook.office365.com" or "webhook.office.com";
    }

    public JsonObject Format(ChatMessage message) => TeamsConnector.Card(message);
}

/// <summary>Lets a feature point a chat message at a better page than the notification's own link (approvals: the document itself).</summary>
public interface INotificationChatLinks
{
    /// <summary>An app-relative link, or null to keep the notification's.</summary>
    Task<string?> LinkAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>Allows at most <see cref="MaxPerWindow"/> posts per channel per window so a burst (a bulk submit) can't flood a channel or trip Teams' limits.</summary>
public sealed class ChatRateLimiter(TimeProvider clock)
{
    public const int MaxPerWindow = 20;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Queue<DateTimeOffset>> _posts = [];

    public bool TryTake(string channelKey)
    {
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (!_posts.TryGetValue(channelKey, out var queue))
            {
                _posts[channelKey] = queue = new Queue<DateTimeOffset>();
            }

            while (queue.Count > 0 && now - queue.Peek() > Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxPerWindow)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}

/// <summary>
/// Posts notifications to the connected chat channel for the people who chose it. Called by <see cref="Notifier"/> after the notification is
/// saved: it never throws, looks up who wants what quickly, and then posts in the background, so a slow or down channel never holds up the
/// action that caused the notification. The webhook address is decrypted only to post and is never logged; logs and errors name the host.
/// </summary>
public sealed class ChatNotificationSender(
    IWorkbenchDbContextFactory dbFactory,
    IEnumerable<IChatChannelFormat> formats,
    IntegrationSecrets secrets,
    OutboundUrlGuard guard,
    IOutboundTransport transport,
    ChatRateLimiter limiter,
    NotificationOptions options,
    TimeProvider clock,
    INotificationChatLinks? links = null,
    ILogger<ChatNotificationSender>? logger = null)
{
    private readonly ILogger _log = (ILogger?)logger ?? NullLogger.Instance;
    private readonly ConcurrentDictionary<Task, byte> _inflight = new();

    public IChatChannelFormat? FormatFor(string kind) => formats.FirstOrDefault(f => f.Kind == kind);

    /// <summary>Finds the recipients who chose a group these notifications belong to and posts for them (everyone, when the sender already knows they want it). Never throws.</summary>
    public async Task NotifyAsync(IReadOnlyList<Notification> notifications, bool postToAll = false)
    {
        try
        {
            foreach (var format in formats)
            {
                await NotifyAsync(format, notifications, postToAll);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _log.LogWarning(ex, "Couldn't post notifications to chat.");
        }
    }

    private async Task NotifyAsync(IChatChannelFormat format, IReadOnlyList<Notification> notifications, bool postToAll)
    {
        var emails = notifications.Select(n => n.RecipientEmail).Distinct().ToList();
        List<Post> posts = [];
        ChatChannel? channel;
        await using (var db = await dbFactory.CreateAsync(CancellationToken.None))
        {
            channel = await db.Set<ChatChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Kind == format.Kind && c.IsActive);
            if (channel is null)
            {
                return;
            }

            var choices = (await db.Set<ChatNotificationChoice>().AsNoTracking()
                    .Where(c => c.ChannelKind == format.Kind && emails.Contains(c.UserEmail)).ToListAsync())
                .ToLookup(c => c.UserEmail, c => c.Group);
            var wanted = postToAll ? notifications.ToList() : notifications.Where(n => choices[n.RecipientEmail].Contains(NotificationGroups.For(n.Kind))).ToList();
            if (wanted.Count == 0)
            {
                return;
            }

            var names = await db.Set<AppUser>().AsNoTracking().Where(u => emails.Contains(u.Email)).ToDictionaryAsync(u => u.Email, u => u.DisplayName);
            foreach (var n in wanted)
            {
                var link = n.Link;
                if (links is not null && NotificationGroups.For(n.Kind) == NotificationGroups.Approvals)
                {
                    try
                    {
                        link = await links.LinkAsync(n) ?? link;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _log.LogDebug(ex, "No document link for notification {NotificationId}.", n.Id);
                    }
                }

                posts.Add(new Post(n.RecipientEmail, Message(n, names.GetValueOrDefault(n.RecipientEmail) ?? n.RecipientEmail, link)));
            }
        }

        var key = TenantScope.Qualify(format.Kind);
        foreach (var post in posts)
        {
            if (!limiter.TryTake(key))
            {
                _log.LogInformation("Chat channel {Kind} is at its posting limit; skipped a notification.", format.Kind);
                continue;
            }

            var task = Task.Run(() => PostAsync(format, channel, post.Message, CancellationToken.None));
            _inflight[task] = 0;
            _ = task.ContinueWith(t => _inflight.TryRemove(t, out _), TaskScheduler.Default);
        }
    }

    /// <summary>For tests and shutdown: completes when every background post has finished.</summary>
    public async Task WaitIdleAsync()
    {
        while (_inflight.Keys.ToArray() is { Length: > 0 } running)
        {
            await Task.WhenAll(running);
        }
    }

    /// <summary>Posts one message and records how it went on the channel. Returns null on success, else a plain-language reason. Never throws.</summary>
    public async Task<string?> PostAsync(IChatChannelFormat format, ChatChannel channel, ChatMessage message, CancellationToken cancellationToken)
    {
        string? error;
        try
        {
            var url = await guard.ValidateAsync(secrets.UnprotectChatUrl(channel.WebhookUrlProtected), "The channel address", cancellationToken);
            if (!format.IsAllowedHost(url.IdnHost))
            {
                error = $"The saved address isn't a {format.Name} address any more. Connect the channel again.";
            }
            else
            {
                var response = await transport.SendAsync(new OutboundRequest(url, format.Format(message).ToJsonString(), "application/json", []), cancellationToken);
                error = response.IsSuccess ? null
                    : response.StatusCode is { } status ? $"{format.Name} answered {status}. The address may have been deleted; connect the channel again."
                    : $"Couldn't reach {format.Name}: {response.Error}";
            }
        }
        catch (ArgumentException ex)
        {
            error = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Posting to {Kind} failed on {Host}: {Reason}", format.Kind, channel.Host, ex.GetType().Name);
            error = $"Couldn't reach {format.Name}.";
        }

        await RecordAsync(channel.Id, error);
        return error;
    }

    private async Task RecordAsync(Guid channelId, string? error)
    {
        try
        {
            await using var db = await dbFactory.CreateAsync(CancellationToken.None);
            var row = await db.Set<ChatChannel>().FirstOrDefaultAsync(c => c.Id == channelId);
            if (row is null)
            {
                return;
            }

            row.LastError = error is null ? null : error.Length > 500 ? error[..500] : error;
            if (error is null)
            {
                row.LastPostedAtUtc = clock.GetUtcNow().UtcDateTime;
            }

            await db.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Couldn't record the outcome of a chat post.");
        }
    }

    private ChatMessage Message(Notification n, string name, string? link)
    {
        var facts = new List<(string, string)> { ("For", name) };
        if (n.Body.Length > 0)
        {
            facts.Add(("Details", n.Body.Length > 300 ? n.Body[..299] + "…" : n.Body));
        }

        return new ChatMessage(n.Title, facts, NotificationEmails.AbsoluteLink(link, options) is { } url && Uri.IsWellFormedUriString(url, UriKind.Absolute) ? url : null);
    }

    private sealed record Post(string Email, ChatMessage Message);
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Connectors;
using Workbench.Contracts;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;

namespace Workbench.Application.Notifications;

/// <summary>
/// Connects one chat channel per product (administrators, "Manage integrations") and records who wants which groups of notifications there
/// (everyone, for themselves). The webhook address is checked against the product's own hosts and the SSRF guard, stored encrypted, and
/// shown back only as its host. Teams and Slack differ only in their <see cref="IChatChannelFormat"/>.
/// </summary>
public abstract class ChatNotificationService(
    string kind,
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    IntegrationSecrets secrets,
    OutboundUrlGuard guard,
    ChatNotificationSender sender,
    TimeProvider clock,
    NotificationOptions options) : IChatNotificationService
{
    private readonly string Kind = kind;

    string IChatNotificationService.Kind => Kind;

    public async Task<ChatChannelDto> GetChannelAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await db.Set<ChatChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Kind == Kind, cancellationToken));
    }

    public async Task<ChatChannelDto> ConnectAsync(ConnectChatChannelRequest request, CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await access.RequireIntegrationAdminAsync(cancellationToken);
        var format = sender.FormatFor(Kind)!;
        var text = request.WebhookUrl?.Trim();
        var url = await guard.ValidateAsync(text, "The channel address", cancellationToken);
        if (!format.IsAllowedHost(url.IdnHost))
        {
            throw new ArgumentException($"That doesn't look like a {format.Name} address. Paste {format.HostHelp}.");
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<ChatChannel>().FirstOrDefaultAsync(c => c.Kind == Kind, cancellationToken);
        if (row is null)
        {
            row = new ChatChannel { Kind = Kind, WebhookUrlProtected = "", Host = "", UpdatedBy = access.Email };
            db.Set<ChatChannel>().Add(row);
        }

        row.WebhookUrlProtected = secrets.ProtectChatUrl(url.ToString());
        row.Host = url.IdnHost;
        row.IsActive = true;
        row.LastError = null;
        row.UpdatedBy = access.Email;
        row.UpdatedAtUtc = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
        return ToDto(row);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        await db.Set<ChatChannel>().Where(c => c.Kind == Kind).ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<ChatChannelDto> SendTestAsync(CancellationToken cancellationToken = default)
    {
        access.RequireSignedIn();
        await access.RequireIntegrationAdminAsync(cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<ChatChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Kind == Kind, cancellationToken)
            ?? throw new ArgumentException($"Connect a {sender.FormatFor(Kind)!.Name} channel first.");
        var link = NotificationEmails.AbsoluteLink("notifications", options);
        var message = new ChatMessage("Workbench is connected to this channel",
            [("Next", "People choose which notifications also come here on their Notifications page.")],
            link is not null && Uri.IsWellFormedUriString(link, UriKind.Absolute) ? link : null);
        await sender.PostAsync(sender.FormatFor(Kind)!, row, message, cancellationToken);
        await using var after = await dbFactory.CreateAsync(cancellationToken);
        return ToDto(await after.Set<ChatChannel>().AsNoTracking().FirstOrDefaultAsync(c => c.Kind == Kind, cancellationToken));
    }

    public async Task<ChatChoicesDto> GetMineAsync(CancellationToken cancellationToken = default)
    {
        var me = Me();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var available = await db.Set<ChatChannel>().AsNoTracking().AnyAsync(c => c.Kind == Kind && c.IsActive, cancellationToken);
        var chosen = await db.Set<ChatNotificationChoice>().AsNoTracking().Where(c => c.UserEmail == me && c.ChannelKind == Kind).Select(c => c.Group).ToListAsync(cancellationToken);
        return new ChatChoicesDto(available, NotificationGroups.All.Select(g => new ChatGroupChoiceDto(g.Key, g.Label, g.Hint, chosen.Contains(g.Key))).ToList());
    }

    public async Task<ChatChoicesDto> SetMineAsync(SaveChatChoicesRequest request, CancellationToken cancellationToken = default)
    {
        var me = Me();
        var wanted = (request.Groups ?? []).Distinct().ToList();
        if (wanted.FirstOrDefault(g => NotificationGroups.All.All(x => x.Key != g)) is { } unknown)
        {
            throw new ArgumentException($"Unknown notification group \"{unknown}\".");
        }

        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var existing = await db.Set<ChatNotificationChoice>().Where(c => c.UserEmail == me && c.ChannelKind == Kind).ToListAsync(cancellationToken);
            db.Set<ChatNotificationChoice>().RemoveRange(existing.Where(c => !wanted.Contains(c.Group)));
            db.Set<ChatNotificationChoice>().AddRange(wanted.Where(g => existing.All(c => c.Group != g))
                .Select(g => new ChatNotificationChoice { UserEmail = me, ChannelKind = Kind, Group = g }));
            await db.SaveChangesAsync(cancellationToken);
        }

        return await GetMineAsync(cancellationToken);
    }

    private string Me()
    {
        access.RequireSignedIn();
        return access.Email;
    }

    private ChatChannelDto ToDto(ChatChannel? row) => row is null
        ? new ChatChannelDto(Kind, false, false, null, null, null, null)
        : new ChatChannelDto(Kind, true, row.IsActive, row.Host, row.LastError, row.LastPostedAtUtc, row.UpdatedAtUtc);
}

public sealed class TeamsNotificationService(
    IWorkbenchDbContextFactory dbFactory, AccessControl access, IntegrationSecrets secrets, OutboundUrlGuard guard,
    ChatNotificationSender sender, TimeProvider clock, NotificationOptions options)
    : ChatNotificationService(ChatChannelKinds.Teams, dbFactory, access, secrets, guard, sender, clock, options), ITeamsNotificationService;

public sealed class SlackNotificationService(
    IWorkbenchDbContextFactory dbFactory, AccessControl access, IntegrationSecrets secrets, OutboundUrlGuard guard,
    ChatNotificationSender sender, TimeProvider clock, NotificationOptions options)
    : ChatNotificationService(ChatChannelKinds.Slack, dbFactory, access, secrets, guard, sender, clock, options), ISlackNotificationService;

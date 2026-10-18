using Workbench.Application.Notifications;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Notifications;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Automation;

namespace Workbench.Tests.Notifications;

public sealed class NotificationSourceTests
{
    [Fact]
    public async Task Comment_service_is_decorated()
    {
        await using var host = new AutomationHost();
        Assert.IsType<MentionNotifyingCommentService>(host.Comments);
    }

    [Fact]
    public async Task Mentions_notify_everyone_but_the_author()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Spec"));

        await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("@bob@example.com and @alice@example.com please look"));

        var notification = Assert.Single(await host.AllNotificationsAsync());
        Assert.Equal("bob@example.com", notification.RecipientEmail);
        Assert.Equal(NotificationKinds.Mention, notification.Kind);
        Assert.Equal("alice@example.com mentioned you on \"Spec\"", notification.Title);
        Assert.Equal($"sheets/{sheetId}?item={item.Id}", notification.Link);
    }

    [Fact]
    public async Task Editing_a_comment_only_notifies_new_mentions()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Spec"));
        var comment = await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("cc @bob@example.com"));

        await host.Comments.UpdateAsync(sheetId, comment.Id, new UpdateCommentRequest("cc @bob@example.com @carol@example.com"));

        Assert.Equal(["bob@example.com", "carol@example.com"], (await host.AllNotificationsAsync()).Select(n => n.RecipientEmail));
    }

    [Fact]
    public async Task Assignment_notifies_the_new_person_unless_they_made_the_change()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var owner = await host.PersonColumnAsync(sheetId);
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Survey"));

        await host.SetAsync(sheetId, item, (owner, "Bob@Example.com"));
        await host.SetAsync(sheetId, item, (owner, "bob@example.com"));
        await host.SetAsync(sheetId, item, (owner, "alice@example.com"));
        host.User.Email = "carol@example.com";
        await host.Items.CreateAsync(sheetId, new CreateItemRequest("New", Values: new Dictionary<string, string?> { [owner.Id.ToString()] = "dave@example.com" }));

        var notifications = await host.AllNotificationsAsync();
        Assert.Equal(["bob@example.com", "dave@example.com"], notifications.Select(n => n.RecipientEmail));
        Assert.Equal("alice@example.com assigned you to \"Survey\"", notifications[0].Title);
        Assert.All(notifications, n => Assert.Equal(NotificationKinds.Assignment, n.Kind));
    }

    [Fact]
    public async Task Immediate_delivery_emails_right_away_and_others_wait()
    {
        await using var host = new AutomationHost();
        var sheetId = await host.NewSheetAsync();
        var item = await host.Items.CreateAsync(sheetId, new CreateItemRequest("Spec"));
        host.User.Email = "bob@example.com";
        await host.Notifications.SetPreferenceAsync(new NotificationPreferenceDto(NotificationDelivery.ImmediateEmail));
        host.User.Email = "alice@example.com";

        await host.Comments.CreateAsync(sheetId, item.Id, new CreateCommentRequest("@bob@example.com @carol@example.com"));

        var email = Assert.Single(host.Emails.Sent);
        Assert.Equal("bob@example.com", email.To);
        Assert.Equal("alice@example.com mentioned you on \"Spec\"", email.Subject);
        Assert.Contains("&quot;Spec&quot;", email.HtmlBody);
        Assert.Contains($"sheets/{sheetId}?item={item.Id}", email.TextBody);
        Assert.NotNull((await host.AllNotificationsAsync()).Single(n => n.RecipientEmail == "bob@example.com").EmailedAtUtc);
    }

    [Fact]
    public async Task Feed_delivers_only_the_current_users_notifications()
    {
        await using var host = new AutomationHost();
        var received = new List<NotificationDto>();
        host.User.Email = "bob@example.com";
        await using var subscription = await host.Get<INotificationFeed>().SubscribeAsync(n =>
        {
            received.Add(n);
            return Task.CompletedTask;
        });
        var notifier = host.Get<Notifier>();

        await notifier.NotifyAsync(["bob@example.com", "carol@example.com"], new NotificationRequest(NotificationKinds.Rule, "Hi", "", null, null));

        Assert.Equal("Hi", Assert.Single(received).Title);
    }

    [Fact]
    public async Task Service_pages_counts_and_marks_read_for_the_current_user_only()
    {
        await using var host = new AutomationHost();
        var notifier = host.Get<Notifier>();
        for (var i = 0; i < 3; i++)
        {
            await notifier.NotifyAsync(["alice@example.com"], new NotificationRequest(NotificationKinds.Rule, $"N{i}", "", null, null));
            host.Clock.Advance(TimeSpan.FromMinutes(1));
        }

        await notifier.NotifyAsync(["bob@example.com"], new NotificationRequest(NotificationKinds.Rule, "Bob's", "", null, null));

        var page = await host.Notifications.ListMineAsync(0, 2);
        Assert.Equal(["N2", "N1"], page.Items.Select(n => n.Title));
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(3, page.UnreadCount);

        await host.Notifications.MarkReadAsync(page.Items[0].Id);
        Assert.Equal(2, await host.Notifications.GetUnreadCountAsync());
        Assert.Equal(2, (await host.Notifications.ListMineAsync(unreadOnly: true)).TotalCount);

        var bobs = (await host.AllNotificationsAsync()).Single(n => n.RecipientEmail == "bob@example.com");
        await Assert.ThrowsAsync<WorkbenchNotFoundException>(() => host.Notifications.MarkReadAsync(bobs.Id));

        await host.Notifications.MarkAllReadAsync();
        Assert.Equal(0, await host.Notifications.GetUnreadCountAsync());
        Assert.Equal(NotificationDelivery.DailyDigest, (await host.Notifications.GetPreferenceAsync()).Delivery);
    }
}

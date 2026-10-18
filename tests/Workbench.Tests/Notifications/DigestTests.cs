using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;
using Workbench.Tests.Automation;

namespace Workbench.Tests.Notifications;

public sealed class DigestTests
{
    private static NotificationRequest Request(string title) => new(NotificationKinds.Rule, title, "Body of " + title, Guid.NewGuid(), Guid.NewGuid());

    [Fact]
    public async Task Sends_one_email_per_user_per_day_with_their_unread_notifications()
    {
        await using var host = new AutomationHost();
        var notifier = host.Get<Notifier>();
        await notifier.NotifyAsync(["alice@example.com"], Request("First"));
        await notifier.NotifyAsync(["alice@example.com", "bob@example.com"], Request("Second"));
        await notifier.NotifyAsync(["alice@example.com"], Request("Already read"));
        await host.Notifications.MarkReadAsync((await host.Notifications.ListMineAsync()).Items.First(n => n.Title == "Already read").Id);

        Assert.Equal(2, await host.Digests.SendDueAsync());

        var alice = Assert.Single(host.Emails.Sent, e => e.To == "alice@example.com");
        Assert.Equal("Workbench: 2 unread notifications", alice.Subject);
        Assert.Contains("First", alice.TextBody);
        Assert.Contains("Second", alice.HtmlBody);
        Assert.DoesNotContain("Already read", alice.TextBody);
        Assert.Equal("Workbench: 1 unread notification", Assert.Single(host.Emails.Sent, e => e.To == "bob@example.com").Subject);

        // Same day: nothing more, even with a new notification.
        await notifier.NotifyAsync(["alice@example.com"], Request("Third"));
        host.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(0, await host.Digests.SendDueAsync());

        // Next day: only what hasn't been emailed yet.
        host.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await host.Digests.SendDueAsync());
        var next = host.Emails.Sent.Last();
        Assert.Equal("alice@example.com", next.To);
        Assert.Contains("Third", next.TextBody);
        Assert.DoesNotContain("First", next.TextBody);
    }

    [Fact]
    public async Task Waits_for_the_digest_hour_and_skips_other_preferences()
    {
        await using var host = new AutomationHost();
        host.Clock.Now = new DateTimeOffset(2026, 10, 17, 5, 0, 0, TimeSpan.Zero);
        host.User.Email = "inapp@example.com";
        await host.Notifications.SetPreferenceAsync(new NotificationPreferenceDto(NotificationDelivery.InAppOnly));
        var notifier = host.Get<Notifier>();
        await notifier.NotifyAsync(["inapp@example.com", "digest@example.com"], Request("Hello"));

        Assert.Equal(0, await host.Digests.SendDueAsync());

        host.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Equal(1, await host.Digests.SendDueAsync());
        Assert.Equal("digest@example.com", Assert.Single(host.Emails.Sent).To);
    }

    [Fact]
    public void Templates_encode_content_and_use_no_external_assets()
    {
        var message = NotificationEmails.Digest("a@example.com",
            [new Domain.Notifications.Notification { RecipientEmail = "a@example.com", Kind = "rule", Title = "<script>x</script>", Body = "a & b", Link = "sheets/1?item=2" }],
            new NotificationOptions { PublicBaseUrl = "https://workbench.example.com/" });

        Assert.DoesNotContain("<script>", message.HtmlBody);
        Assert.Contains("&lt;script&gt;", message.HtmlBody);
        Assert.Contains("a &amp; b", message.HtmlBody);
        Assert.Contains("https://workbench.example.com/sheets/1?item=2", message.TextBody);
        Assert.Contains("<html lang=\"en\">", message.HtmlBody);
        Assert.DoesNotContain("<img", message.HtmlBody);
        Assert.DoesNotContain("<link", message.HtmlBody);
        Assert.DoesNotContain("src=", message.HtmlBody);
    }
}

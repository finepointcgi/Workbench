using Workbench.Application.Notifications;
using Workbench.Contracts.Notifications;
using Workbench.Tests.Automation;

namespace Workbench.Tests.Notifications;

public sealed class NotificationPublisherTests
{
    [Fact]
    public async Task Publishes_with_an_app_relative_link_and_honors_delivery_preferences()
    {
        await using var host = new AutomationHost();
        host.User.Email = "pm@workbench.local";
        await host.Notifications.SetPreferenceAsync(new NotificationPreferenceDto(NotificationDelivery.ImmediateEmail));
        var publisher = host.Get<INotificationPublisher>();

        var sent = await publisher.PublishAsync("PM@workbench.local", NotificationKinds.TimesheetSubmitted,
            "Dev submitted a timesheet", "Week of Oct 12: 40.0 h", "/time/approvals?week=2026-10-12");

        Assert.True(sent);
        var mine = Assert.Single((await host.Notifications.ListMineAsync()).Items);
        Assert.Equal(NotificationKinds.TimesheetSubmitted, mine.Kind);
        Assert.Equal("time/approvals?week=2026-10-12", mine.Link);
        Assert.Null(mine.SheetId);
        var email = Assert.Single(host.Emails.Sent);
        Assert.Equal("pm@workbench.local", email.To);
        Assert.Contains("time/approvals?week=2026-10-12", email.TextBody);
    }

    [Fact]
    public async Task Skips_invalid_emails_and_notifies_each_person_once()
    {
        await using var host = new AutomationHost();
        var publisher = host.Get<INotificationPublisher>();

        Assert.False(await publisher.PublishAsync("not an email", NotificationKinds.TimesheetMissing, "Missing"));
        var sent = await publisher.PublishAsync(["a@example.com", "A@example.com", null, "", "b@example.com"],
            NotificationKinds.ExpenseApproval, "Expense needs approval");

        Assert.Equal(["a@example.com", "b@example.com"], sent);
        Assert.Equal(2, (await host.AllNotificationsAsync()).Count);
    }

    [Theory]
    [InlineData("https://evil.example/x")]
    [InlineData("javascript:alert(1)")]
    public async Task Refuses_links_that_leave_the_app(string link)
    {
        await using var host = new AutomationHost();
        var publisher = host.Get<INotificationPublisher>();

        await Assert.ThrowsAsync<ArgumentException>(() => publisher.PublishAsync("a@example.com", NotificationKinds.Rule, "Hi", link: link));
        Assert.Empty(await host.AllNotificationsAsync());
    }

    [Fact]
    public async Task Protocol_relative_links_become_app_relative()
    {
        await using var host = new AutomationHost();

        await host.Get<INotificationPublisher>().PublishAsync("a@example.com", NotificationKinds.Rule, "Hi", link: "//evil.example/x");

        Assert.Equal("evil.example/x", Assert.Single(await host.AllNotificationsAsync()).Link);
    }
}

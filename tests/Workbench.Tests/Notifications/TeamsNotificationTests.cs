using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Application.Notifications;
using Workbench.ApiClient;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using Workbench.Tests.Identity;
using Workbench.Tests.Integrations;

namespace Workbench.Tests.Notifications;

public sealed class TeamsNotificationTests
{
    private const string Admin = "dev@workbench.local";
    private const string Person = "analyst@example.com";
    private const string Webhook = "https://contoso.webhook.office.com/webhookb2/very-secret-path-1234/IncomingWebhook/abcd";

    private static (WebApplicationFactory<Program> App, FakeTransport Transport) Host(WorkbenchWebFactory factory)
    {
        var transport = new FakeTransport();
        var app = factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IOutboundTransport>(transport));
            s.Replace(ServiceDescriptor.Singleton<IOutboundDns>(new FakeDns()));
        }));
        return (app, transport);
    }

    private static async Task<string> BodyOf(OutboundRequest request) => await Task.FromResult(request.Body);

    [Theory]
    [InlineData("contoso.webhook.office.com", true)]
    [InlineData("prod-12.westus.logic.azure.com", true)]
    [InlineData("abc.environment.api.powerplatform.com", true)]
    [InlineData("outlook.office.com", true)]
    [InlineData("hooks.slack.com", false)]
    [InlineData("evil.example.com", false)]
    [InlineData("webhook.office.com.evil.example", false)]
    [InlineData("notoffice.com", false)]
    public void Only_microsoft_hosts_are_teams_addresses(string host, bool allowed) =>
        Assert.Equal(allowed, new TeamsChannelFormat().IsAllowedHost(host));

    [Theory]
    [InlineData("approval.requested", NotificationGroups.Approvals)]
    [InlineData("timesheet.submitted", NotificationGroups.Approvals)]
    [InlineData("leave.requested", NotificationGroups.Approvals)]
    [InlineData("ap.bill_approval", NotificationGroups.Approvals)]
    [InlineData("expense.approved", NotificationGroups.Decisions)]
    [InlineData("timesheet.rejected", NotificationGroups.Decisions)]
    [InlineData("mention", NotificationGroups.Assignments)]
    [InlineData("crm.task", NotificationGroups.Assignments)]
    [InlineData("timesheet.missing", NotificationGroups.Timesheets)]
    [InlineData("rule", NotificationGroups.Other)]
    public void Every_kind_belongs_to_one_group_by_its_wording(string kind, string group) =>
        Assert.Equal(group, NotificationGroups.For(kind));

    [Fact]
    public async Task An_admin_connects_a_channel_and_people_choose_what_also_goes_there()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        var (app, transport) = Host(web);
        await using var _ = app;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var person = app.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Person);
        var adminTeams = new HttpTeamsNotificationService(admin);
        var myTeams = new HttpTeamsNotificationService(person);

        // Until a channel is connected, nothing is offered.
        Assert.False((await myTeams.GetMineAsync()).Available);
        Assert.False((await adminTeams.GetChannelAsync()).Connected);

        // Only administrators connect it, and only to Microsoft's addresses.
        await Assert.ThrowsAnyAsync<Exception>(() => new HttpTeamsNotificationService(person).ConnectAsync(new ConnectChatChannelRequest(Webhook)));
        await Assert.ThrowsAnyAsync<Exception>(() => new HttpTeamsNotificationService(person).GetChannelAsync());
        var wrong = await admin.PutAsJsonAsync("api/v1/integrations/teams", new ConnectChatChannelRequest("https://hooks.slack.com/services/T0/B0/xyz"));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("doesn't look like a Microsoft Teams address", await wrong.Content.ReadAsStringAsync());

        var channel = await adminTeams.ConnectAsync(new ConnectChatChannelRequest(Webhook));
        Assert.Equal("contoso.webhook.office.com", channel.Host);
        Assert.DoesNotContain("very-secret", System.Text.Json.JsonSerializer.Serialize(channel)); // the secret path never comes back
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var factoryDb = scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>();
            await using var db = await factoryDb.CreateAsync(default);
            var stored = db.Set<ChatChannel>().Single();
            Assert.DoesNotContain("very-secret", stored.WebhookUrlProtected); // encrypted at rest
        }

        var choices = await myTeams.GetMineAsync();
        Assert.True(choices.Available);
        Assert.All(choices.Groups, g => Assert.False(g.Enabled)); // off until the person turns it on
        await myTeams.SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals, NotificationGroups.Decisions]));
        Assert.Equal([NotificationGroups.Approvals, NotificationGroups.Decisions], (await myTeams.GetMineAsync()).Groups.Where(g => g.Enabled).Select(g => g.Group));
        await Assert.ThrowsAnyAsync<Exception>(() => myTeams.SetMineAsync(new SaveChatChoicesRequest(["everything"])));

        // A notification in a chosen group is posted to the channel with a link; one in an unchosen group is not.
        var publisher = app.Services.GetRequiredService<INotificationPublisher>();
        var sender = app.Services.GetRequiredService<ChatNotificationSender>();
        await publisher.PublishAsync(Person, "approval.requested", "Approval needed: Timesheet for Pat", "40 hours", "approvals");
        await publisher.PublishAsync(Person, NotificationKinds.Mention, "Pat mentioned you", null, "sheets/abc");
        await publisher.PublishAsync("pm@example.com", "approval.requested", "Approval needed: for someone who didn't choose", null, "approvals");
        await sender.WaitIdleAsync();

        var posted = Assert.Single(transport.Sent);
        Assert.Equal("contoso.webhook.office.com", posted.Url.Host);
        Assert.Contains("Approval needed: Timesheet for Pat", posted.Body);
        Assert.Contains("40 hours", posted.Body);
        Assert.Contains("AdaptiveCard", posted.Body);

        // The test button posts too, and a failing channel is reported, not thrown.
        var tested = await adminTeams.SendTestAsync();
        Assert.Null(tested.LastError);
        Assert.NotNull(tested.LastPostedAtUtc);
        transport.Answer(FakeTransport.Status(410));
        var failed = await adminTeams.SendTestAsync();
        Assert.Contains("410", failed.LastError);
        Assert.DoesNotContain("very-secret", failed.LastError);

        await adminTeams.DisconnectAsync();
        Assert.False((await myTeams.GetMineAsync()).Available);
    }

    [Fact]
    public async Task A_down_channel_never_stops_the_notification_and_bursts_are_rate_limited()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        var (app, transport) = Host(web);
        await using var _ = app;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var person = app.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Person);
        await new HttpTeamsNotificationService(admin).ConnectAsync(new ConnectChatChannelRequest(Webhook));
        await new HttpTeamsNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        var publisher = app.Services.GetRequiredService<INotificationPublisher>();
        var sender = app.Services.GetRequiredService<ChatNotificationSender>();

        // Teams is unreachable: the notification is still saved and the caller gets its answer.
        var before = (await new HttpNotificationService(person).ListMineAsync(0, 100)).TotalCount;
        transport.Answer(FakeTransport.Unreachable);
        Assert.True(await publisher.PublishAsync(Person, "approval.requested", "Approval needed: one", null, "approvals"));
        await sender.WaitIdleAsync();
        Assert.Contains("Couldn't reach", (await new HttpTeamsNotificationService(admin).GetChannelAsync()).LastError);
        Assert.Equal(before + 1, (await new HttpNotificationService(person).ListMineAsync(0, 100)).TotalCount);

        // A burst of 40 posts at most the limit.
        transport.Sent.Clear();
        for (var i = 0; i < 40; i++)
        {
            await publisher.PublishAsync(Person, "approval.requested", $"Approval needed: n{i}", null, "approvals");
        }

        await sender.WaitIdleAsync();
        Assert.InRange(transport.Sent.Count, 1, ChatRateLimiter.MaxPerWindow);
        Assert.Equal(before + 41, (await new HttpNotificationService(person).ListMineAsync(0, 100)).TotalCount); // every one is still in the bell
    }
}

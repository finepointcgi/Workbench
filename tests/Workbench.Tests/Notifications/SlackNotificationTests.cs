using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Bunit;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MudBlazor.Services;
using Workbench.Application;
using Workbench.Application.Integrations;
using Workbench.Application.Integrations.Connectors;
using Workbench.Application.Notifications;
using Workbench.ApiClient;
using Workbench.Contracts.Notifications;
using Workbench.Domain.Notifications;
using Workbench.Tests.Identity;
using Workbench.Tests.Integrations;
using Workbench.Ui.Integrations;
using Workbench.Ui.Notifications;

namespace Workbench.Tests.Notifications;

public sealed class SlackNotificationTests
{
    private const string Admin = "dev@workbench.local";
    private const string Person = "analyst@example.com";
    private const string Webhook = "https://hooks.slack.com/services/T0000/B0000/very-secret-token-1234";
    private const string TeamsWebhook = "https://contoso.webhook.office.com/webhookb2/teams-secret-path-5678/IncomingWebhook/abcd";

    private static (WebApplicationFactory<Program> App, FakeTransport Transport) Host(WorkbenchWebFactory factory)
    {
        var transport = new FakeTransport();
        var app = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Notifications:PublicBaseUrl", "https://workbench.example.com");
            b.ConfigureTestServices(s =>
        {
            s.Replace(ServiceDescriptor.Singleton<IOutboundTransport>(transport));
            s.Replace(ServiceDescriptor.Singleton<IOutboundDns>(new FakeDns()));
            });
        });
        return (app, transport);
    }

    [Theory]
    [InlineData("hooks.slack.com", true)]
    [InlineData("HOOKS.SLACK.COM", true)]
    [InlineData("hooks.slack.com.", true)]
    [InlineData("slack.com", false)]
    [InlineData("api.slack.com", false)]
    [InlineData("hooks.slack.com.evil.example", false)]
    [InlineData("evilhooks.slack.com", false)]
    [InlineData("contoso.webhook.office.com", false)]
    public void Only_hooks_slack_com_is_a_slack_address(string host, bool allowed) =>
        Assert.Equal(allowed, new SlackChannelFormat().IsAllowedHost(host));

    [Fact]
    public void The_slack_message_is_a_block_kit_section_with_the_same_content_as_the_teams_card_and_a_button()
    {
        var message = new ChatMessage("Approval needed: Timesheet for Pat",
            [("For", "Sam Lee"), ("Details", "40 hours")], "https://workbench.example.com/approvals");

        var json = new SlackChannelFormat().Format(message);

        Assert.Equal(
            """
            {"text":"Approval needed: Timesheet for Pat","blocks":[{"type":"section","text":{"type":"mrkdwn","text":"*Approval needed: Timesheet for Pat*\n*For:* Sam Lee\n*Details:* 40 hours"}},{"type":"actions","elements":[{"type":"button","text":{"type":"plain_text","text":"Open in Workbench"},"url":"https://workbench.example.com/approvals"}]}]}
            """.Trim(),
            json.ToJsonString());
        var teams = new TeamsChannelFormat().Format(message).ToJsonString();
        Assert.All(new[] { "Approval needed: Timesheet for Pat", "Sam Lee", "40 hours", "Open in Workbench", "https://workbench.example.com/approvals" }, text =>
        {
            Assert.Contains(text, teams);
            Assert.Contains(text, json.ToJsonString());
        });
    }

    [Fact]
    public void Without_a_link_there_is_no_button()
    {
        var json = new SlackChannelFormat().Format(new ChatMessage("Hello", [], null));

        Assert.Single(json["blocks"]!.AsArray());
    }

    [Fact]
    public void Titles_and_details_cannot_make_links_or_mentions()
    {
        var message = new ChatMessage("<!channel> <https://evil.example|Click> & <@U123>", [("Details", "<!here> a > b")], null);

        var json = new SlackChannelFormat().Format(message).ToJsonString();

        Assert.DoesNotContain("<!channel>", json);
        Assert.DoesNotContain("<!here>", json);
        Assert.DoesNotContain("<https", json);
        Assert.DoesNotContain("<@U123>", json);
        var parsed = JsonNode.Parse(json)!;
        Assert.Equal("&lt;!channel&gt; &lt;https://evil.example|Click&gt; &amp; &lt;@U123&gt;", parsed["text"]!.GetValue<string>());
        Assert.Contains("&lt;!here&gt; a &gt; b", parsed["blocks"]![0]!["text"]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task An_admin_connects_slack_and_people_choose_what_also_goes_there()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        var (app, transport) = Host(web);
        await using var _ = app;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var person = app.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Person);
        var adminSlack = new HttpSlackNotificationService(admin);
        var mySlack = new HttpSlackNotificationService(person);

        Assert.False((await mySlack.GetMineAsync()).Available);
        Assert.False((await adminSlack.GetChannelAsync()).Connected);

        // Only administrators connect it, and only to Slack's webhook host.
        await Assert.ThrowsAnyAsync<Exception>(() => mySlack.ConnectAsync(new ConnectChatChannelRequest(Webhook)));
        await Assert.ThrowsAnyAsync<Exception>(() => mySlack.GetChannelAsync());
        var wrong = await admin.PutAsJsonAsync("api/v1/integrations/slack", new ConnectChatChannelRequest(TeamsWebhook));
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);
        Assert.Contains("doesn't look like a Slack address", await wrong.Content.ReadAsStringAsync());

        var channel = await adminSlack.ConnectAsync(new ConnectChatChannelRequest(Webhook));
        Assert.Equal("hooks.slack.com", channel.Host);
        Assert.Equal("slack", channel.Kind);
        Assert.DoesNotContain("very-secret", System.Text.Json.JsonSerializer.Serialize(channel));
        await using (var scope = app.Services.CreateAsyncScope())
        {
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync(default);
            Assert.DoesNotContain("very-secret", db.Set<ChatChannel>().Single(c => c.Kind == "slack").WebhookUrlProtected); // encrypted at rest
        }

        var choices = await mySlack.GetMineAsync();
        Assert.True(choices.Available);
        Assert.All(choices.Groups, g => Assert.False(g.Enabled));
        await mySlack.SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        Assert.Equal([NotificationGroups.Approvals], (await mySlack.GetMineAsync()).Groups.Where(g => g.Enabled).Select(g => g.Group));
        await Assert.ThrowsAnyAsync<Exception>(() => mySlack.SetMineAsync(new SaveChatChoicesRequest(["everything"])));

        // Approval notifications reach Slack with the document link; unchosen groups and other people's choices don't.
        var publisher = app.Services.GetRequiredService<INotificationPublisher>();
        var sender = app.Services.GetRequiredService<ChatNotificationSender>();
        await publisher.PublishAsync(Person, "approval.requested", "Approval needed: Timesheet <!channel> for Pat", "40 hours & <@U1>", "approvals");
        await publisher.PublishAsync(Person, NotificationKinds.Mention, "Pat mentioned you", null, "sheets/abc");
        await publisher.PublishAsync("pm@example.com", "approval.requested", "Approval needed: for someone who didn't choose", null, "approvals");
        await sender.WaitIdleAsync();

        var posted = Assert.Single(transport.Sent);
        Assert.Equal("hooks.slack.com", posted.Url.Host);
        Assert.Equal("application/json", posted.ContentType);
        var body = JsonNode.Parse(posted.Body)!;
        var section = body["blocks"]![0]!["text"]!["text"]!.GetValue<string>();
        Assert.Contains("Approval needed: Timesheet &lt;!channel&gt; for Pat", section);
        Assert.Contains("40 hours &amp; &lt;@U1&gt;", section);
        Assert.Contains("*For:* ", section);
        Assert.Equal("Open in Workbench", body["blocks"]![1]!["elements"]![0]!["text"]!["text"]!.GetValue<string>());
        Assert.Equal("https://workbench.example.com/approvals", body["blocks"]![1]!["elements"]![0]!["url"]!.GetValue<string>());

        // The test button posts too; a failing channel is reported with its status and never the secret.
        transport.Sent.Clear();
        var tested = await adminSlack.SendTestAsync();
        Assert.Null(tested.LastError);
        Assert.NotNull(tested.LastPostedAtUtc);
        Assert.Contains("Workbench is connected to this channel", Assert.Single(transport.Sent).Body);
        transport.Answer(FakeTransport.Status(404));
        var failed = await adminSlack.SendTestAsync();
        Assert.Contains("404", failed.LastError);
        Assert.DoesNotContain("very-secret", failed.LastError);

        await adminSlack.DisconnectAsync();
        Assert.False((await mySlack.GetMineAsync()).Available);
    }

    [Fact]
    public async Task With_both_connected_each_person_picks_teams_slack_or_both_and_a_down_slack_never_stops_the_action()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        var (app, transport) = Host(web);
        await using var _ = app;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var person = app.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Person);
        await new HttpSlackNotificationService(admin).ConnectAsync(new ConnectChatChannelRequest(Webhook));
        await new HttpTeamsNotificationService(admin).ConnectAsync(new ConnectChatChannelRequest(TeamsWebhook));
        var publisher = app.Services.GetRequiredService<INotificationPublisher>();
        var sender = app.Services.GetRequiredService<ChatNotificationSender>();

        // Teams only, then Slack only, then both: the choices are independent per product.
        await new HttpTeamsNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        await publisher.PublishAsync(Person, "approval.requested", "Approval needed: one", null, "approvals");
        await sender.WaitIdleAsync();
        Assert.Equal(["contoso.webhook.office.com"], transport.Sent.Select(r => r.Url.Host));

        transport.Sent.Clear();
        await new HttpTeamsNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([]));
        await new HttpSlackNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        await publisher.PublishAsync(Person, "approval.requested", "Approval needed: two", null, "approvals");
        await sender.WaitIdleAsync();
        Assert.Equal(["hooks.slack.com"], transport.Sent.Select(r => r.Url.Host));

        transport.Sent.Clear();
        await new HttpTeamsNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        await publisher.PublishAsync(Person, "approval.requested", "Approval needed: three", null, "approvals");
        await sender.WaitIdleAsync();
        Assert.Equal(["contoso.webhook.office.com", "hooks.slack.com"], transport.Sent.Select(r => r.Url.Host).Order());

        // Slack is unreachable: the notification is still saved, the caller gets its answer, the admin sees the error, Teams is unaffected.
        var before = (await new HttpNotificationService(person).ListMineAsync(0, 100)).TotalCount;
        transport.Sent.Clear();
        await new HttpTeamsNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([]));
        transport.Answer(FakeTransport.Unreachable);
        Assert.True(await publisher.PublishAsync(Person, "approval.requested", "Approval needed: four", null, "approvals"));
        await sender.WaitIdleAsync();
        Assert.Contains("Couldn't reach", (await new HttpSlackNotificationService(admin).GetChannelAsync()).LastError);
        Assert.Null((await new HttpTeamsNotificationService(admin).GetChannelAsync()).LastError);
        Assert.Equal(before + 1, (await new HttpNotificationService(person).ListMineAsync(0, 100)).TotalCount);
    }

    [Fact]
    public async Task Slack_posts_are_rate_limited_per_channel()
    {
        await using var web = new WorkbenchWebFactory("Development", testAuth: true);
        var (app, transport) = Host(web);
        await using var _ = app;
        using var admin = app.CreateClient();
        admin.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Admin);
        using var person = app.CreateClient();
        person.DefaultRequestHeaders.Add(WorkbenchWebFactory.UserHeader, Person);
        await new HttpSlackNotificationService(admin).ConnectAsync(new ConnectChatChannelRequest(Webhook));
        await new HttpSlackNotificationService(person).SetMineAsync(new SaveChatChoicesRequest([NotificationGroups.Approvals]));
        var publisher = app.Services.GetRequiredService<INotificationPublisher>();

        for (var i = 0; i < 40; i++)
        {
            await publisher.PublishAsync(Person, "approval.requested", $"Approval needed: n{i}", null, "approvals");
        }

        await app.Services.GetRequiredService<ChatNotificationSender>().WaitIdleAsync();
        Assert.InRange(transport.Sent.Count, 1, ChatRateLimiter.MaxPerWindow);
    }

    // ---- UI ----

    private sealed class FakeChat(string kind, string host) : IChatNotificationService, ISlackNotificationService, ITeamsNotificationService
    {
        public string Kind => kind;
        public ChatChannelDto Channel { get; set; } = new(kind, false, false, null, null, null, null);
        public List<string> Chosen { get; set; } = [];
        public string? ConnectedWith { get; private set; }

        public Task<ChatChannelDto> GetChannelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Channel);

        public Task<ChatChannelDto> ConnectAsync(ConnectChatChannelRequest request, CancellationToken cancellationToken = default)
        {
            ConnectedWith = request.WebhookUrl;
            return Task.FromResult(Channel = new(kind, true, true, host, null, null, null));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ChatChannelDto> SendTestAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(Channel = Channel with { LastError = "Slack answered 404. The address may have been deleted; connect the channel again." });

        public Task<ChatChoicesDto> GetMineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatChoicesDto(Channel.Connected, NotificationGroups.All.Select(g => new ChatGroupChoiceDto(g.Key, g.Label, g.Hint, Chosen.Contains(g.Key))).ToList()));

        public Task<ChatChoicesDto> SetMineAsync(SaveChatChoicesRequest request, CancellationToken cancellationToken = default)
        {
            Chosen = [.. request.Groups];
            return GetMineAsync(cancellationToken);
        }
    }

    private static BunitContext Context(FakeChat? teams, FakeChat? slack)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        if (teams is not null)
        {
            ctx.Services.AddSingleton<ITeamsNotificationService>(teams);
        }

        if (slack is not null)
        {
            ctx.Services.AddSingleton<ISlackNotificationService>(slack);
        }

        return ctx;
    }

    private static FakeChat Connected(string kind, string host) =>
        new(kind, host) { Channel = new(kind, true, true, host, null, null, null) };

    [Fact]
    public async Task The_notifications_page_shows_only_the_connected_destination_and_both_when_both_are_connected()
    {
        var teams = new FakeChat("teams", "contoso.webhook.office.com");
        var slack = Connected("slack", "hooks.slack.com");
        await using var ctx = Context(teams, slack);

        var onlySlack = ctx.Render<ChatChoicesCard>();
        onlySlack.WaitForAssertion(() => Assert.NotNull(onlySlack.Find("[data-testid='chat-choices']")));
        Assert.Contains("Also post to Slack", onlySlack.Markup);
        Assert.DoesNotContain("Teams", onlySlack.Markup);
        onlySlack.Find("input[data-testid='slack-approvals'], [data-testid='slack-approvals'] input").Change(true);
        onlySlack.WaitForAssertion(() => Assert.Equal([NotificationGroups.Approvals], slack.Chosen));

        teams.Channel = Connected("teams", "contoso.webhook.office.com").Channel;
        var both = ctx.Render<ChatChoicesCard>();
        both.WaitForAssertion(() => Assert.Contains("Also post to Teams or Slack", both.Markup));
        Assert.NotNull(both.Find("input[data-testid='slack-approvals'], [data-testid='slack-approvals'] input"));
        both.Find("input[data-testid='teams-decisions'], [data-testid='teams-decisions'] input").Change(true);
        both.WaitForAssertion(() => Assert.Equal([NotificationGroups.Decisions], teams.Chosen));
        Assert.Equal([NotificationGroups.Approvals], slack.Chosen); // Slack's choices are untouched

        await using var none = Context(new FakeChat("teams", "x"), new FakeChat("slack", "y"));
        Assert.Empty(none.Render<ChatChoicesCard>().FindAll("[data-testid='chat-choices']"));
    }

    [Fact]
    public async Task An_admin_connects_and_tests_a_slack_channel_by_pasting_its_address()
    {
        var slack = new FakeChat("slack", "hooks.slack.com");
        await using var ctx = Context(null, slack);
        var tab = ctx.Render<SlackTab>();

        tab.WaitForAssertion(() => Assert.NotNull(tab.Find("[data-testid='slack-url']")));
        Assert.Contains("Slack notifications", tab.Markup);
        Assert.True(tab.Find("[data-testid='slack-connect']").HasAttribute("disabled"));
        tab.Find("input[data-testid='slack-url'], [data-testid='slack-url'] input").Change("https://hooks.slack.com/services/T0/B0/abc");
        tab.Find("[data-testid='slack-connect']").Click();

        tab.WaitForAssertion(() => Assert.Contains("hooks.slack.com", tab.Find("[data-testid='slack-status']").TextContent));
        Assert.Equal("https://hooks.slack.com/services/T0/B0/abc", slack.ConnectedWith);
        Assert.DoesNotContain("services/T0", tab.Find("[data-testid='slack-status']").TextContent);

        tab.Find("[data-testid='slack-test']").Click();
        tab.WaitForAssertion(() => Assert.Contains("Slack answered 404", tab.Find("[data-testid='slack-status']").TextContent));
    }
}

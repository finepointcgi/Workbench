using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;
using Workbench.Contracts.Calendar;
using Workbench.Contracts.Notifications;
using Workbench.Ui.Calendar;
using Workbench.Ui.Integrations;
using Workbench.Ui.Notifications;

namespace Workbench.Tests.Notifications;

public sealed class TeamsAndCalendarUiTests
{
    private sealed class FakeFeed : ICalendarFeedService
    {
        public CalendarFeedDto State { get; set; } = new(false, null);
        public int Rotations { get; private set; }

        public Task<CalendarFeedDto> GetMineAsync(CancellationToken cancellationToken = default) => Task.FromResult(State);

        public Task<CalendarFeedDto> EnableAsync(CancellationToken cancellationToken = default) => Task.FromResult(State = new(true, "calendar/token-one.ics"));

        public Task<CalendarFeedDto> RotateAsync(CancellationToken cancellationToken = default)
        {
            Rotations++;
            return Task.FromResult(State = new(true, "calendar/token-two.ics"));
        }

        public Task RevokeAsync(CancellationToken cancellationToken = default)
        {
            State = new(false, null);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTeams : ITeamsNotificationService
    {
        public ChatChannelDto Channel { get; set; } = new("teams", false, false, null, null, null, null);
        public List<string> Chosen { get; set; } = [];
        public string? ConnectedWith { get; private set; }

        public Task<ChatChannelDto> GetChannelAsync(CancellationToken cancellationToken = default) => Task.FromResult(Channel);

        public Task<ChatChannelDto> ConnectAsync(ConnectChatChannelRequest request, CancellationToken cancellationToken = default)
        {
            ConnectedWith = request.WebhookUrl;
            return Task.FromResult(Channel = new("teams", true, true, "contoso.webhook.office.com", null, null, null));
        }

        public Task DisconnectAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<ChatChannelDto> SendTestAsync(CancellationToken cancellationToken = default) => Task.FromResult(Channel);

        public Task<ChatChoicesDto> GetMineAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new ChatChoicesDto(Channel.Connected, NotificationGroups.All.Select(g => new ChatGroupChoiceDto(g.Key, g.Label, g.Hint, Chosen.Contains(g.Key))).ToList()));

        public Task<ChatChoicesDto> SetMineAsync(SaveChatChoicesRequest request, CancellationToken cancellationToken = default)
        {
            Chosen = [.. request.Groups];
            return GetMineAsync(cancellationToken);
        }
    }

    private static BunitContext Context(params (Type, object)[] services)
    {
        var ctx = new BunitContext();
        ctx.JSInterop.Mode = JSRuntimeMode.Loose;
        ctx.Services.AddMudServices();
        foreach (var (type, instance) in services)
        {
            ctx.Services.AddSingleton(type, instance);
        }

        return ctx;
    }

    [Fact]
    public async Task Add_to_my_calendar_turns_on_a_link_with_instructions_for_each_calendar_and_can_renew_or_stop_it()
    {
        var feed = new FakeFeed();
        await using var ctx = Context((typeof(ICalendarFeedService), feed));
        var card = ctx.Render<CalendarSubscribeCard>();

        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='calendar-on']")));
        Assert.Empty(card.FindAll("[data-testid='calendar-url']"));
        card.Find("[data-testid='calendar-on']").Click();

        card.WaitForAssertion(() => Assert.Contains("calendar/token-one.ics", card.Find("input[data-testid='calendar-url'], [data-testid='calendar-url'] input").GetAttribute("value")));
        foreach (var id in new[] { "howto-outlook", "howto-google", "howto-apple" })
        {
            Assert.NotNull(card.Find($"[data-testid='{id}']"));
        }

        Assert.Contains("Outlook", card.Markup);
        Assert.Contains("From URL", card.Markup);
        Assert.Contains("New Calendar Subscription", card.Markup);

        card.Find("[data-testid='calendar-copy']").Click();
        ctx.JSInterop.VerifyInvoke("navigator.clipboard.writeText");

        card.Find("[data-testid='calendar-rotate']").Click();
        card.WaitForAssertion(() => Assert.Contains("token-two", card.Find("input[data-testid='calendar-url'], [data-testid='calendar-url'] input").GetAttribute("value")));
        Assert.Equal(1, feed.Rotations);

        card.Find("[data-testid='calendar-off']").Click();
        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='calendar-on']")));
    }

    [Fact]
    public async Task The_notifications_page_offers_teams_only_once_a_channel_is_connected_and_saves_each_choice()
    {
        var teams = new FakeTeams();
        await using var ctx = Context((typeof(ITeamsNotificationService), teams));
        var hidden = ctx.Render<TeamsChoicesCard>();
        Assert.Empty(hidden.FindAll("[data-testid='teams-choices']"));

        teams.Channel = teams.Channel with { Connected = true, IsActive = true, Host = "contoso.webhook.office.com" };
        var card = ctx.Render<TeamsChoicesCard>();
        card.WaitForAssertion(() => Assert.NotNull(card.Find("[data-testid='teams-choices']")));
        card.Find("input[data-testid='teams-approvals'], [data-testid='teams-approvals'] input").Change(true);

        card.WaitForAssertion(() => Assert.Equal([NotificationGroups.Approvals], teams.Chosen));
    }

    [Fact]
    public async Task An_admin_connects_a_channel_by_pasting_its_address()
    {
        var teams = new FakeTeams();
        await using var ctx = Context((typeof(ITeamsNotificationService), teams));
        var tab = ctx.Render<TeamsTab>();

        tab.WaitForAssertion(() => Assert.NotNull(tab.Find("[data-testid='teams-url']")));
        Assert.True(tab.Find("[data-testid='teams-connect']").HasAttribute("disabled"));
        tab.Find("input[data-testid='teams-url'], [data-testid='teams-url'] input").Change("https://contoso.webhook.office.com/webhookb2/abc");
        tab.Find("[data-testid='teams-connect']").Click();

        tab.WaitForAssertion(() => Assert.Contains("contoso.webhook.office.com", tab.Find("[data-testid='teams-status']").TextContent));
        Assert.Equal("https://contoso.webhook.office.com/webhookb2/abc", teams.ConnectedWith);
        Assert.DoesNotContain("webhookb2", tab.Find("[data-testid='teams-status']").TextContent); // the secret path isn't shown back
        Assert.NotNull(tab.Find("[data-testid='teams-test']"));
    }
}

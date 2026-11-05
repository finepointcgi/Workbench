using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Workbench.Contracts.Calendar;
using Workbench.Contracts.Notifications;

namespace Workbench.Api;

/// <summary>The person's calendar feed (<c>/api/v1/me/calendar-feed</c>) Teams and Slack notifications and reminder choices (<c>/api/v1/me/teams</c>, <c>/api/v1/me/slack</c>, <c>/api/v1/me/reminders</c>, <c>/api/v1/integrations/teams</c>, <c>/api/v1/integrations/slack</c>).</summary>
internal static class CalendarChatEndpoints
{
    public static void MapCalendarChatEndpoints(this RouteGroupBuilder v1)
    {
        var feed = v1.MapGroup("/me/calendar-feed").WithTags("Calendar");
        feed.MapGet("/", (ICalendarFeedService s, CancellationToken ct) => s.GetMineAsync(ct));
        feed.MapPost("/", (ICalendarFeedService s, CancellationToken ct) => s.EnableAsync(ct));
        feed.MapPost("/rotate", (ICalendarFeedService s, CancellationToken ct) => s.RotateAsync(ct));
        feed.MapDelete("/", async (ICalendarFeedService s, CancellationToken ct) =>
        {
            await s.RevokeAsync(ct);
            return Results.NoContent();
        });

        var mine = v1.MapGroup("/me/teams").WithTags("Notifications");
        mine.MapGet("/", (ITeamsNotificationService s, CancellationToken ct) => s.GetMineAsync(ct));
        mine.MapPut("/", (SaveChatChoicesRequest r, ITeamsNotificationService s, CancellationToken ct) => s.SetMineAsync(r, ct));

        var reminders = v1.MapGroup("/me/reminders").WithTags("Notifications");
        reminders.MapGet("/", (IReminderSettingsService s, CancellationToken ct) => s.GetMineAsync(ct));
        reminders.MapPut("/", (SaveReminderSettingsRequest r, IReminderSettingsService s, CancellationToken ct) => s.SetMineAsync(r, ct));

        var admin = v1.MapGroup("/integrations/teams").WithTags("Integrations");
        admin.MapGet("/", (ITeamsNotificationService s, CancellationToken ct) => s.GetChannelAsync(ct));
        admin.MapPut("/", (ConnectChatChannelRequest r, ITeamsNotificationService s, CancellationToken ct) => s.ConnectAsync(r, ct));
        admin.MapPost("/test", (ITeamsNotificationService s, CancellationToken ct) => s.SendTestAsync(ct));
        admin.MapDelete("/", async (ITeamsNotificationService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(ct);
            return Results.NoContent();
        });

        var mineSlack = v1.MapGroup("/me/slack").WithTags("Notifications");
        mineSlack.MapGet("/", (ISlackNotificationService s, CancellationToken ct) => s.GetMineAsync(ct));
        mineSlack.MapPut("/", (SaveChatChoicesRequest r, ISlackNotificationService s, CancellationToken ct) => s.SetMineAsync(r, ct));

        var adminSlack = v1.MapGroup("/integrations/slack").WithTags("Integrations");
        adminSlack.MapGet("/", (ISlackNotificationService s, CancellationToken ct) => s.GetChannelAsync(ct));
        adminSlack.MapPut("/", (ConnectChatChannelRequest r, ISlackNotificationService s, CancellationToken ct) => s.ConnectAsync(r, ct));
        adminSlack.MapPost("/test", (ISlackNotificationService s, CancellationToken ct) => s.SendTestAsync(ct));
        adminSlack.MapDelete("/", async (ISlackNotificationService s, CancellationToken ct) =>
        {
            await s.DisconnectAsync(ct);
            return Results.NoContent();
        });
    }
}

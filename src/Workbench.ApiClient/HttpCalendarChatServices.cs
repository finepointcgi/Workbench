using System.Net.Http.Json;
using Workbench.Contracts.Calendar;
using Workbench.Contracts.Notifications;

namespace Workbench.ApiClient;

public sealed class HttpCalendarFeedService(HttpClient http) : ICalendarFeedService
{
    private const string Base = "api/v1/me/calendar-feed";

    public async Task<CalendarFeedDto> GetMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadAsync<CalendarFeedDto>(cancellationToken);

    public async Task<CalendarFeedDto> EnableAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync(Base, null, cancellationToken)).ReadAsync<CalendarFeedDto>(cancellationToken);

    public async Task<CalendarFeedDto> RotateAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base}/rotate", null, cancellationToken)).ReadAsync<CalendarFeedDto>(cancellationToken);

    public async Task RevokeAsync(CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync(Base, cancellationToken)).EnsureOkAsync(cancellationToken);
}

public sealed class HttpTeamsNotificationService(HttpClient http) : ITeamsNotificationService
{
    public async Task<ChatChannelDto> GetChannelAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/integrations/teams", cancellationToken)).ReadAsync<ChatChannelDto>(cancellationToken);

    public async Task<ChatChannelDto> ConnectAsync(ConnectChatChannelRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/integrations/teams", request, cancellationToken)).ReadAsync<ChatChannelDto>(cancellationToken);

    public async Task DisconnectAsync(CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync("api/v1/integrations/teams", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<ChatChannelDto> SendTestAsync(CancellationToken cancellationToken = default) =>
        await (await http.PostAsync("api/v1/integrations/teams/test", null, cancellationToken)).ReadAsync<ChatChannelDto>(cancellationToken);

    public async Task<ChatChoicesDto> GetMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/me/teams", cancellationToken)).ReadAsync<ChatChoicesDto>(cancellationToken);

    public async Task<ChatChoicesDto> SetMineAsync(SaveChatChoicesRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/me/teams", request, cancellationToken)).ReadAsync<ChatChoicesDto>(cancellationToken);
}

public sealed class HttpReminderSettingsService(HttpClient http) : IReminderSettingsService
{
    private const string Base = "api/v1/me/reminders";

    public async Task<ReminderSettingsDto> GetMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base, cancellationToken)).ReadAsync<ReminderSettingsDto>(cancellationToken);

    public async Task<ReminderSettingsDto> SetMineAsync(SaveReminderSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync(Base, request, cancellationToken)).ReadAsync<ReminderSettingsDto>(cancellationToken);
}

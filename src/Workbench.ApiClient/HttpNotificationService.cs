using System.Net.Http.Json;
using Workbench.Contracts.Notifications;

namespace Workbench.ApiClient;

public sealed class HttpNotificationService(HttpClient http) : INotificationService
{
    public async Task<NotificationPage> ListMineAsync(int skip = 0, int take = 20, bool unreadOnly = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync(
            $"api/v1/me/notifications?skip={skip}&take={take}&unreadOnly={(unreadOnly ? "true" : "false")}", cancellationToken);
        return await response.ReadAsync<NotificationPage>(cancellationToken);
    }

    public async Task<int> GetUnreadCountAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/me/notifications/unread-count", cancellationToken);
        return await response.ReadAsync<int>(cancellationToken);
    }

    public async Task MarkReadAsync(Guid notificationId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/me/notifications/{notificationId}/read", null, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task MarkAllReadAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync("api/v1/me/notifications/read-all", null, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<NotificationPreferenceDto> GetPreferenceAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/me/notifications/preference", cancellationToken);
        return await response.ReadAsync<NotificationPreferenceDto>(cancellationToken);
    }

    public async Task<NotificationPreferenceDto> SetPreferenceAsync(NotificationPreferenceDto preference, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/me/notifications/preference", preference, cancellationToken);
        return await response.ReadAsync<NotificationPreferenceDto>(cancellationToken);
    }
}

using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Notifications;

namespace Workbench.ApiClient;

public sealed class HttpEmailDeliverabilityService(HttpClient http) : IEmailDeliverabilityService
{
    public async Task<IReadOnlyList<EmailAddressIssueDto>> ListAsync(bool includeCleared = false, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/email-issues?includeCleared={(includeCleared ? "true" : "false")}", cancellationToken);
        return await response.ReadAsync<List<EmailAddressIssueDto>>(cancellationToken);
    }

    public async Task<EmailAddressIssueDto?> GetMineAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/email-issues/mine", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadAsync<EmailAddressIssueDto>(cancellationToken);
    }

    public async Task ClearAsync(string address, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/email-issues/clear", new { address }, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}

using Workbench.Contracts.Collaboration;

namespace Workbench.ApiClient;

public sealed class HttpActivityService(HttpClient http) : IActivityService
{
    public async Task<IReadOnlyList<ActivityDto>> ListAsync(Guid sheetId, Guid? itemId = null, int limit = 100, CancellationToken cancellationToken = default)
    {
        var url = $"api/v1/sheets/{sheetId}/activity?limit={limit}" + (itemId is { } id ? $"&itemId={id}" : "");
        using var response = await http.GetAsync(url, cancellationToken);
        return await response.ReadAsync<List<ActivityDto>>(cancellationToken);
    }
}

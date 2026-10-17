using System.Net;
using System.Net.Http.Json;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetService(HttpClient http) : ISheetService
{
    public async Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<SheetSummary>>("api/v1/sheets", cancellationToken) ?? [];

    public async Task<SheetDetail?> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}", cancellationToken);
        return response.StatusCode == HttpStatusCode.NotFound ? null : await response.ReadAsync<SheetDetail>(cancellationToken);
    }

    public async Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/sheets", request, cancellationToken);
        return await response.ReadAsync<SheetSummary>(cancellationToken);
    }
}

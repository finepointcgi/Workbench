using System.Net.Http.Json;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetService(HttpClient http) : ISheetService
{
    public async Task<IReadOnlyList<SheetSummary>> ListAsync(CancellationToken cancellationToken = default) =>
        await http.GetFromJsonAsync<List<SheetSummary>>("api/v1/sheets", cancellationToken) ?? [];

    public async Task<SheetSummary> CreateAsync(CreateSheetRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync("api/v1/sheets", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<SheetSummary>(cancellationToken)
            ?? throw new InvalidOperationException("Server returned an empty sheet.");
    }
}

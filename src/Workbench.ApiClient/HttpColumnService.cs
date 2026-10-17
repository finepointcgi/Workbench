using System.Net.Http.Json;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpColumnService(HttpClient http) : IColumnService
{
    public async Task<ColumnDto> CreateAsync(Guid sheetId, CreateColumnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/columns", request, cancellationToken);
        return await response.ReadAsync<ColumnDto>(cancellationToken);
    }

    public async Task<ColumnDto> UpdateAsync(Guid sheetId, Guid columnId, UpdateColumnRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/columns/{columnId}", request, cancellationToken);
        return await response.ReadAsync<ColumnDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid columnId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/columns/{columnId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}

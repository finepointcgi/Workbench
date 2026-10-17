using System.Net.Http.Json;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpItemService(HttpClient http) : IItemService
{
    public async Task<IReadOnlyList<ItemDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/items", cancellationToken);
        return await response.ReadAsync<List<ItemDto>>(cancellationToken);
    }

    public async Task<ItemDto> CreateAsync(Guid sheetId, CreateItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/items", request, cancellationToken);
        return await response.ReadAsync<ItemDto>(cancellationToken);
    }

    public async Task<ItemDto> UpdateAsync(Guid sheetId, Guid itemId, UpdateItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/items/{itemId}", request, cancellationToken);
        return await response.ReadAsync<ItemDto>(cancellationToken);
    }

    public async Task<ItemDto> MoveAsync(Guid sheetId, Guid itemId, MoveItemRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/items/{itemId}/move", request, cancellationToken);
        return await response.ReadAsync<ItemDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/items/{itemId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }
}

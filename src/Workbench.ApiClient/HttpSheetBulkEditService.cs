using System.Net.Http.Json;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetBulkEditService(HttpClient http) : ISheetBulkEditService
{
    public async Task<SheetBulkEditResult> ApplyAsync(Guid sheetId, SheetBulkEditRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/items/bulk", request, cancellationToken);
        return await response.ReadAsync<SheetBulkEditResult>(cancellationToken);
    }
}

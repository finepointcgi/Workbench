using System.Net.Http.Json;
using Workbench.Contracts.Reports;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetViewService(HttpClient http) : ISheetViewService
{
    public async Task<SheetViewsDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/views", cancellationToken);
        return await response.ReadAsync<SheetViewsDto>(cancellationToken);
    }

    public async Task<SheetViewDto> CreateAsync(Guid sheetId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/views", request, cancellationToken);
        return await response.ReadAsync<SheetViewDto>(cancellationToken);
    }

    public async Task<SheetViewDto> UpdateAsync(Guid sheetId, Guid viewId, SaveSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/views/{viewId}", request, cancellationToken);
        return await response.ReadAsync<SheetViewDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid viewId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/views/{viewId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task SetMyDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/views/my-default", request, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task SetSheetDefaultAsync(Guid sheetId, SetDefaultSheetViewRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/views/sheet-default", request, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<ReportFileDto> ExportAsync(Guid sheetId, SheetExportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/export", request, cancellationToken);
        return await response.ReadAsync<ReportFileDto>(cancellationToken);
    }
}

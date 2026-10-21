using System.Net.Http.Json;
using Workbench.Contracts.Scheduling;

namespace Workbench.ApiClient;

public sealed class HttpPlanScheduleService(HttpClient http) : IPlanScheduleService
{
    public async Task<PlanScheduleDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/plan", cancellationToken);
        return await response.ReadAsync<PlanScheduleDto>(cancellationToken);
    }

    public async Task IndentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/sheets/{sheetId}/plan/items/{itemId}/indent", null, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task OutdentAsync(Guid sheetId, Guid itemId, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsync($"api/v1/sheets/{sheetId}/plan/items/{itemId}/outdent", null, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task SetDurationAsync(Guid sheetId, Guid itemId, int days, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/plan/items/{itemId}/duration", new { Days = days }, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task SetPredecessorsAsync(Guid sheetId, Guid itemId, string? predecessors, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/plan/items/{itemId}/predecessors", new { Predecessors = predecessors }, cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<PlanFile> ExportAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/plan/export", cancellationToken);
        return await response.ReadAsync<PlanFile>(cancellationToken);
    }

    public async Task<PlanFile> TemplateAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/plan-template", cancellationToken);
        return await response.ReadAsync<PlanFile>(cancellationToken);
    }

    public async Task<PlanImportPreview> PreviewImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/plan/import/preview", request, cancellationToken);
        return await response.ReadAsync<PlanImportPreview>(cancellationToken);
    }

    public async Task<PlanImportPreview> ApplyImportAsync(Guid sheetId, PlanImportRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/plan/import", request, cancellationToken);
        return await response.ReadAsync<PlanImportPreview>(cancellationToken);
    }
}

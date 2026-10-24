using System.Net.Http.Json;
using Workbench.Contracts.Scheduling;
using Workbench.Contracts.Sheets;

namespace Workbench.ApiClient;

public sealed class HttpSheetFormatRuleService(HttpClient http) : ISheetFormatRuleService
{
    public async Task<SheetFormatRulesDto> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/format-rules", cancellationToken);
        return await response.ReadAsync<SheetFormatRulesDto>(cancellationToken);
    }

    public async Task<SheetFormatRulesDto> SaveAsync(Guid sheetId, SaveSheetFormatRulesRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/format-rules", request, cancellationToken);
        return await response.ReadAsync<SheetFormatRulesDto>(cancellationToken);
    }
}

public sealed class HttpPlanFloatService(HttpClient http) : IPlanFloatService
{
    public async Task<PlanFloatDto> GetAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/float", cancellationToken);
        return await response.ReadAsync<PlanFloatDto>(cancellationToken);
    }
}

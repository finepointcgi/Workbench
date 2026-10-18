using System.Net.Http.Json;
using Workbench.Contracts.Automation;

namespace Workbench.ApiClient;

public sealed class HttpRuleService(HttpClient http) : IRuleService
{
    public async Task<IReadOnlyList<RuleDto>> ListAsync(Guid sheetId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/rules", cancellationToken);
        return await response.ReadAsync<List<RuleDto>>(cancellationToken);
    }

    public async Task<RuleDto> GetAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/rules/{ruleId}", cancellationToken);
        return await response.ReadAsync<RuleDto>(cancellationToken);
    }

    public async Task<RuleDto> CreateAsync(Guid sheetId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/rules", request, cancellationToken);
        return await response.ReadAsync<RuleDto>(cancellationToken);
    }

    public async Task<RuleDto> UpdateAsync(Guid sheetId, Guid ruleId, SaveRuleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/rules/{ruleId}", request, cancellationToken);
        return await response.ReadAsync<RuleDto>(cancellationToken);
    }

    public async Task<RuleDto> SetEnabledAsync(Guid sheetId, Guid ruleId, bool enabled, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/sheets/{sheetId}/rules/{ruleId}/enabled", new { enabled }, cancellationToken);
        return await response.ReadAsync<RuleDto>(cancellationToken);
    }

    public async Task DeleteAsync(Guid sheetId, Guid ruleId, CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync($"api/v1/sheets/{sheetId}/rules/{ruleId}", cancellationToken);
        await response.EnsureOkAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RuleRunDto>> ListRunsAsync(Guid sheetId, Guid? ruleId = null, int limit = 50, CancellationToken cancellationToken = default)
    {
        var query = ruleId is { } id ? $"?ruleId={id}&limit={limit}" : $"?limit={limit}";
        using var response = await http.GetAsync($"api/v1/sheets/{sheetId}/rules/runs{query}", cancellationToken);
        return await response.ReadAsync<List<RuleRunDto>>(cancellationToken);
    }

    public async Task<RuleTestResult> TestAsync(Guid sheetId, TestRuleRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PostAsJsonAsync($"api/v1/sheets/{sheetId}/rules/test", request, cancellationToken);
        return await response.ReadAsync<RuleTestResult>(cancellationToken);
    }
}

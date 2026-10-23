using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Automations;

namespace Workbench.ApiClient;

public sealed class HttpAutomationService(HttpClient http) : IAutomationService
{
    private const string Root = "api/v1/automations";

    public async Task<IReadOnlyList<AutomationSummaryDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Root, cancellationToken)).ReadAsync<List<AutomationSummaryDto>>(cancellationToken);

    public async Task<AutomationDto> GetAsync(Guid automationId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{automationId}", cancellationToken)).ReadAsync<AutomationDto>(cancellationToken);

    public async Task<AutomationDefinition> GetVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{automationId}/versions/{version}", cancellationToken)).ReadAsync<AutomationDefinition>(cancellationToken);

    public async Task<AutomationDto> CreateAsync(SaveAutomationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync(Root, request, cancellationToken)).ReadAsync<AutomationDto>(cancellationToken);

    public async Task<AutomationDto> UpdateAsync(Guid automationId, SaveAutomationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{automationId}", request, cancellationToken)).ReadAsync<AutomationDto>(cancellationToken);

    public async Task<AutomationDto> SetEnabledAsync(Guid automationId, bool enabled, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{automationId}/enabled", new { enabled }, cancellationToken)).ReadAsync<AutomationDto>(cancellationToken);

    public async Task<AutomationDto> RestoreVersionAsync(Guid automationId, int version, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{automationId}/versions/{version}/restore", null, cancellationToken)).ReadAsync<AutomationDto>(cancellationToken);

    public async Task DeleteAsync(Guid automationId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Root}/{automationId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<AutomationRunDto> DryRunAsync(AutomationDryRunRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/dry-run", request, cancellationToken)).ReadAsync<AutomationRunDto>(cancellationToken);

    public async Task<AutomationRunDto> RunNowAsync(Guid automationId, AutomationRunNowRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{automationId}/run", request, cancellationToken)).ReadAsync<AutomationRunDto>(cancellationToken);

    public async Task<IReadOnlyList<AutomationRunDto>> ListRunsAsync(AutomationRunQuery query, CancellationToken cancellationToken = default)
    {
        var parts = new List<string> { $"take={query.Take}" };
        if (query.AutomationId is { } id)
        {
            parts.Add($"automationId={id}");
        }

        if (query.Status is { } status)
        {
            parts.Add($"status={status}");
        }

        if (query.EntityType is { } type)
        {
            parts.Add($"entityType={Uri.EscapeDataString(type)}");
        }

        if (query.RecordId is { } recordId)
        {
            parts.Add($"recordId={recordId}");
        }

        return await (await http.GetAsync($"{Root}/runs?{string.Join("&", parts)}", cancellationToken)).ReadAsync<List<AutomationRunDto>>(cancellationToken);
    }

    public async Task<AutomationRunDto> GetRunAsync(Guid runId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/runs/{runId}", cancellationToken)).ReadAsync<AutomationRunDto>(cancellationToken);

    public async Task<AutomationCatalogDto> GetCatalogAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/catalog", cancellationToken)).ReadAsync<AutomationCatalogDto>(cancellationToken);

    public async Task<IReadOnlyList<AutomationOptionDto>> ListRecordsAsync(string entityType, string? search = null, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/records/{Uri.EscapeDataString(entityType)}{(string.IsNullOrWhiteSpace(search) ? "" : $"?search={Uri.EscapeDataString(search)}")}", cancellationToken))
            .ReadAsync<List<AutomationOptionDto>>(cancellationToken);

    public async Task<SaveAutomationRequest> BuildRecipeAsync(string key, Guid? sheetId, IReadOnlyDictionary<string, string?> blanks, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/templates/{Uri.EscapeDataString(key)}/build", new { sheetId, blanks }, cancellationToken)).ReadAsync<SaveAutomationRequest>(cancellationToken);

    public async Task<IReadOnlyList<AutomationSummaryDto>> ListForSheetAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/sheets/{sheetId}", cancellationToken)).ReadAsync<List<AutomationSummaryDto>>(cancellationToken);

    public async Task<SheetAutomationCatalogDto> GetSheetCatalogAsync(Guid sheetId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/sheets/{sheetId}/catalog", cancellationToken)).ReadAsync<SheetAutomationCatalogDto>(cancellationToken);

    /// <summary>Templates are static; the desktop app reads them once from the server.</summary>
    public IReadOnlyList<AutomationTemplateDto> ListTemplates() =>
        http.GetFromJsonAsync<List<AutomationTemplateDto>>($"{Root}/templates").GetAwaiter().GetResult() ?? [];

    public async Task<AutomationSettingsDto> GetSettingsAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/settings", cancellationToken)).ReadAsync<AutomationSettingsDto>(cancellationToken);

    public async Task<AutomationSettingsDto> SaveSettingsAsync(SaveAutomationSettingsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/settings", request, cancellationToken)).ReadAsync<AutomationSettingsDto>(cancellationToken);
}

public sealed class HttpAutomationTaskService(HttpClient http) : IAutomationTaskService
{
    private const string Root = "api/v1/automations/tasks";

    public async Task<IReadOnlyList<AutomationTaskDto>> ListMineAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/mine", cancellationToken)).ReadAsync<List<AutomationTaskDto>>(cancellationToken);

    public async Task<AutomationTaskDto> CompleteAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{taskId}/complete", null, cancellationToken)).ReadAsync<AutomationTaskDto>(cancellationToken);
}

public static class AutomationBuilderClients
{
    public static IServiceCollection AddAutomationBuilderClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IAutomationService, HttpAutomationService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IAutomationTaskService, HttpAutomationTaskService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

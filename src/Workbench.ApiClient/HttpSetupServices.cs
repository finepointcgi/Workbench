using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.ProjectAccounting;

namespace Workbench.ApiClient;

public sealed class HttpPlanSyncService(HttpClient http) : IPlanSyncService
{
    public async Task<ProjectDto> CreatePlanSheetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/projects/{projectId}/plan", null, cancellationToken)).ReadAsync<ProjectDto>(cancellationToken);

    public async Task<PlanSyncReport> DiffAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/projects/{projectId}/plan/diff", cancellationToken)).ReadAsync<PlanSyncReport>(cancellationToken);

    public async Task<PlanSyncReport> ApplyAsync(Guid projectId, ApplyPlanSyncRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/projects/{projectId}/plan/apply", request, cancellationToken)).ReadAsync<PlanSyncReport>(cancellationToken);
}

public sealed class HttpSetupAdminService(HttpClient http) : ISetupAdminService
{
    public async Task<OrgUnitDto> UpdateOrgUnitAsync(Guid orgUnitId, CreateOrgUnitRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/setup/org-units/{orgUnitId}", request, cancellationToken)).ReadAsync<OrgUnitDto>(cancellationToken);

    public async Task<CustomerDto> UpdateCustomerAsync(Guid customerId, SaveCustomerRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/setup/customers/{customerId}", request, cancellationToken)).ReadAsync<CustomerDto>(cancellationToken);

    public async Task<IReadOnlyList<AuditEventDto>> SearchAuditAsync(AuditQuery query, CancellationToken cancellationToken = default)
    {
        var url = $"api/v1/setup/audit?limit={query.Limit}"
            + (string.IsNullOrWhiteSpace(query.EntityType) ? "" : $"&entityType={Uri.EscapeDataString(query.EntityType)}")
            + (query.EntityId is { } id ? $"&entityId={id}" : "")
            + (query.From is { } from ? $"&from={from:yyyy-MM-dd}" : "")
            + (query.To is { } to ? $"&to={to:yyyy-MM-dd}" : "");
        return await http.GetAsync(url, cancellationToken).ReadListAsync<AuditEventDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<string>> ListAuditEntityTypesAsync(CancellationToken cancellationToken = default) =>
        await http.GetAsync("api/v1/setup/audit/entity-types", cancellationToken).ReadListAsync<string>(cancellationToken);

    public async Task<SetupChecklistDto> GetChecklistAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/setup/checklist", cancellationToken)).ReadAsync<SetupChecklistDto>(cancellationToken);
}

public sealed class HttpProjectStartService(HttpClient http) : IProjectStartService
{
    public async Task<IReadOnlyList<ProjectTemplateDto>> ListTemplatesAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/project-start/templates", cancellationToken)).ReadAsync<List<ProjectTemplateDto>>(cancellationToken);

    public async Task<string> SuggestCodeAsync(CancellationToken cancellationToken = default) =>
        (await (await http.GetAsync("api/v1/project-start/suggested-code", cancellationToken)).ReadAsync<SuggestedCode>(cancellationToken)).Code;

    public async Task<Workbench.Contracts.Scheduling.PlanImportPreview> CheckImportAsync(Workbench.Contracts.Scheduling.PlanImportRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/project-start/check-import", request, cancellationToken)).ReadAsync<Workbench.Contracts.Scheduling.PlanImportPreview>(cancellationToken);

    public async Task<ProjectDto> StartAsync(StartProjectRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/project-start", request, cancellationToken)).ReadAsync<ProjectDto>(cancellationToken);

    public async Task<ProjectDto> FinishSetupAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/project-start/{projectId}/finish", null, cancellationToken)).ReadAsync<ProjectDto>(cancellationToken);

    private sealed record SuggestedCode(string Code);
}

public sealed class HttpProjectPlanService(HttpClient http) : IProjectPlanService
{
    private const string Root = "api/v1/project-plans";

    public async Task<ProjectPlanDto> GetAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/{projectId}", cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectTermsDto> TermsFromContractAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"{Root}/from-contract/{contractId}", cancellationToken)).ReadAsync<ProjectTermsDto>(cancellationToken);

    public async Task<ProjectPlanDto> SaveTermsAsync(Guid projectId, SaveProjectTermsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{projectId}/terms", request, cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectPlanDto> SaveCategoriesAsync(Guid projectId, IReadOnlyList<Guid> laborCategoryIds, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Root}/{projectId}/categories", new SaveProjectCategoriesRequest(laborCategoryIds), cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectPlanDto> HandToManagerAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Root}/{projectId}/hand-to-manager", null, cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectPlanDto> SubmitAsync(Guid projectId, string? note = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{projectId}/submit", new PlanReviewRequest(note), cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectPlanDto> ApproveAsync(Guid projectId, string? comment = null, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{projectId}/approve", new PlanReviewRequest(comment), cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);

    public async Task<ProjectPlanDto> ReturnAsync(Guid projectId, string comment, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Root}/{projectId}/return", new PlanReviewRequest(comment), cancellationToken)).ReadAsync<ProjectPlanDto>(cancellationToken);
}

public static class SetupClientRegistration
{
    public static IServiceCollection AddSetupClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPlanSyncService, HttpPlanSyncService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectStartService, HttpProjectStartService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IProjectPlanService, HttpProjectPlanService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISetupAdminService, HttpSetupAdminService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

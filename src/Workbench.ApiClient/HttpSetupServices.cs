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

public static class SetupClientRegistration
{
    public static IServiceCollection AddSetupClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IPlanSyncService, HttpPlanSyncService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISetupAdminService, HttpSetupAdminService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

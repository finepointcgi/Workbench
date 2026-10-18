using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Ceilings;
using Workbench.Contracts.Closeout;

namespace Workbench.ApiClient;

public sealed class HttpCeilingService(HttpClient http) : ICeilingService
{
    public async Task<ContractCeilingStatusDto> GetContractStatusAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/contracts/{contractId}/ceilings", cancellationToken)).ReadAsync<ContractCeilingStatusDto>(cancellationToken);

    public async Task<ProjectCeilingStatusDto> GetProjectStatusAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/projects/{projectId}/ceilings", cancellationToken)).ReadAsync<ProjectCeilingStatusDto>(cancellationToken);

    public async Task<CeilingStatusDto> GetAsync(Guid ceilingId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/ceilings/{ceilingId}", cancellationToken)).ReadAsync<CeilingStatusDto>(cancellationToken);

    public async Task<CeilingDto> CreateAsync(SaveCeilingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/ceilings", request, cancellationToken)).ReadAsync<CeilingDto>(cancellationToken);

    public async Task<CeilingDto> UpdateAsync(Guid ceilingId, SaveCeilingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/ceilings/{ceilingId}", request, cancellationToken)).ReadAsync<CeilingDto>(cancellationToken);

    public async Task<CeilingDto> DeactivateAsync(Guid ceilingId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/ceilings/{ceilingId}/deactivate", null, cancellationToken)).ReadAsync<CeilingDto>(cancellationToken);

    public async Task<IReadOnlyList<CeilingOverrideDto>> ListOverridesAsync(Guid ceilingId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/ceilings/{ceilingId}/overrides", cancellationToken)).ReadAsync<List<CeilingOverrideDto>>(cancellationToken);

    public async Task<CeilingOverrideDto> GrantOverrideAsync(Guid ceilingId, GrantCeilingOverrideRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/ceilings/{ceilingId}/overrides", request, cancellationToken)).ReadAsync<CeilingOverrideDto>(cancellationToken);

    public async Task<CeilingOverrideDto> RevokeOverrideAsync(Guid overrideId, CeilingReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/ceiling-overrides/{overrideId}/revoke", request, cancellationToken)).ReadAsync<CeilingOverrideDto>(cancellationToken);

    public async Task<IReadOnlyList<RateCeilingDto>> ListRateCeilingsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/contracts/{contractId}/rate-ceilings", cancellationToken)).ReadAsync<List<RateCeilingDto>>(cancellationToken);

    public async Task<RateCeilingDto> CreateRateCeilingAsync(Guid contractId, SaveRateCeilingRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/contracts/{contractId}/rate-ceilings", request, cancellationToken)).ReadAsync<RateCeilingDto>(cancellationToken);

    public async Task<RateCeilingDto> DeactivateRateCeilingAsync(Guid rateCeilingId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/rate-ceilings/{rateCeilingId}/deactivate", null, cancellationToken)).ReadAsync<RateCeilingDto>(cancellationToken);

    public async Task<int> CheckAlertsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/contracts/{contractId}/ceilings/check-alerts", null, cancellationToken)).ReadAsync<int>(cancellationToken);
}

public sealed class HttpCloseoutService(HttpClient http) : ICloseoutService
{
    private static string Base(CloseoutScope scope, Guid id) => $"api/v1/closeout/{(scope == CloseoutScope.Contract ? "contract" : "project")}/{id}";

    public async Task<CloseoutDto> GetAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync(Base(scope, id), cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> MarkPhysicallyCompleteAsync(CloseoutScope scope, Guid id, PhysicallyCompleteRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base(scope, id)}/physically-complete", request, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> StartClosingAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base(scope, id)}/start-closing", null, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> CloseAsync(CloseoutScope scope, Guid id, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base(scope, id)}/close", null, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> ReopenAsync(CloseoutScope scope, Guid id, CloseoutReasonRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base(scope, id)}/reopen", request, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> SaveRecordsAsync(CloseoutScope scope, Guid id, SaveCloseoutRecordsRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base(scope, id)}/records", request, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> WaiveAsync(CloseoutScope scope, Guid id, WaiveCloseoutItemRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"{Base(scope, id)}/waivers", request, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> RemoveWaiverAsync(CloseoutScope scope, Guid id, CloseoutCheck check, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"{Base(scope, id)}/waivers/{check}", cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> MarkFinalInvoiceAsync(Guid contractId, MarkFinalInvoiceRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"{Base(CloseoutScope.Contract, contractId)}/final-invoice", request, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);

    public async Task<CloseoutDto> RecordDeobligationAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"{Base(CloseoutScope.Contract, contractId)}/deobligation", null, cancellationToken)).ReadAsync<CloseoutDto>(cancellationToken);
}

public static class CeilingClientRegistration
{
    public static IServiceCollection AddCeilingClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICeilingService, HttpCeilingService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICloseoutService, HttpCloseoutService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

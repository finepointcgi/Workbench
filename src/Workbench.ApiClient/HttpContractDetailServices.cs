using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Contracts;

namespace Workbench.ApiClient;

public sealed class HttpClinService(HttpClient http) : IClinService
{
    public async Task<IReadOnlyList<ClinDto>> ListClinsAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/contracts/{contractId}/clins", cancellationToken).ReadListAsync<ClinDto>(cancellationToken);

    public Task<ClinDto?> GetClinAsync(Guid clinId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ClinDto>($"api/v1/clins/{clinId}", cancellationToken);

    public async Task<ClinDto> CreateClinAsync(Guid contractId, SaveClinRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/contracts/{contractId}/clins", request, cancellationToken)).ReadAsync<ClinDto>(cancellationToken);

    public async Task<ClinDto> UpdateClinAsync(Guid clinId, SaveClinRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/clins/{clinId}", request, cancellationToken)).ReadAsync<ClinDto>(cancellationToken);

    public async Task<IReadOnlyList<ClinDto>> ListClinsForProjectAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/clins", cancellationToken).ReadListAsync<ClinDto>(cancellationToken);

    public async Task<IReadOnlyList<WbsClinMappingDto>> ListWbsMappingsAsync(Guid projectId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/projects/{projectId}/wbs-clins", cancellationToken).ReadListAsync<WbsClinMappingDto>(cancellationToken);

    public async Task<WbsClinMappingDto> SetWbsClinAsync(Guid wbsElementId, Guid? clinId, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/wbs/{wbsElementId}/clin", new { ClinId = clinId }, cancellationToken)).ReadAsync<WbsClinMappingDto>(cancellationToken);
}

public sealed class HttpContractModificationService(HttpClient http) : IContractModificationService
{
    public async Task<IReadOnlyList<ContractModificationDto>> ListAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/contracts/{contractId}/modifications", cancellationToken).ReadListAsync<ContractModificationDto>(cancellationToken);

    public Task<ContractModificationDto?> GetAsync(Guid modificationId, CancellationToken cancellationToken = default) =>
        http.GetOrNullAsync<ContractModificationDto>($"api/v1/modifications/{modificationId}", cancellationToken);

    public async Task<ContractModificationDto> CreateDraftAsync(Guid contractId, SaveModificationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/contracts/{contractId}/modifications", request, cancellationToken)).ReadAsync<ContractModificationDto>(cancellationToken);

    public async Task<ContractModificationDto> UpdateDraftAsync(Guid modificationId, SaveModificationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/modifications/{modificationId}", request, cancellationToken)).ReadAsync<ContractModificationDto>(cancellationToken);

    public async Task DeleteDraftAsync(Guid modificationId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/modifications/{modificationId}", cancellationToken)).EnsureOkAsync(cancellationToken);

    public async Task<ContractModificationDto> ExecuteAsync(Guid modificationId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/modifications/{modificationId}/execute", null, cancellationToken)).ReadAsync<ContractModificationDto>(cancellationToken);
}

public sealed class HttpFundingService(HttpClient http) : IFundingService
{
    public async Task<IReadOnlyList<FundingActionDto>> ListFundingHistoryAsync(Guid contractId, Guid? clinId = null, CancellationToken cancellationToken = default) =>
        await http.GetAsync($"api/v1/contracts/{contractId}/funding-history{(clinId is { } c ? $"?clinId={c}" : "")}", cancellationToken)
            .ReadListAsync<FundingActionDto>(cancellationToken);

    public async Task<ContractFundingStatusDto> GetFundingStatusAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/contracts/{contractId}/clin-funding", cancellationToken)).ReadAsync<ContractFundingStatusDto>(cancellationToken);

    public async Task<ContractFundingStatusDto?> GetProjectFundingStatusAsync(Guid projectId, CancellationToken cancellationToken = default)
    {
        var response = await http.GetAsync($"api/v1/projects/{projectId}/clin-funding", cancellationToken);
        return response.StatusCode == HttpStatusCode.NoContent ? null : await response.ReadAsync<ContractFundingStatusDto>(cancellationToken);
    }

    public async Task<IReadOnlyList<FundingNoticeDto>> CheckNoticesAsync(Guid contractId, CancellationToken cancellationToken = default) =>
        await (await http.PostAsync($"api/v1/contracts/{contractId}/funding-notices/check", null, cancellationToken)).ReadAsync<List<FundingNoticeDto>>(cancellationToken);
}

public static class ContractDetailClientRegistration
{
    public static IServiceCollection AddContractDetailClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IClinService, HttpClinService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IContractModificationService, HttpContractModificationService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IFundingService, HttpFundingService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

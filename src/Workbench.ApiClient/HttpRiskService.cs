using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Risks;

namespace Workbench.ApiClient;

public sealed class HttpRiskService(HttpClient http) : IRiskService
{
    public async Task<RiskRegisterDto> GetRegisterAsync(Guid projectId, bool includeClosed = false, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/risks/projects/{projectId}?includeClosed={includeClosed.ToString().ToLowerInvariant()}", cancellationToken)).ReadAsync<RiskRegisterDto>(cancellationToken);

    public async Task<RiskDto> GetAsync(Guid riskId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/risks/{riskId}", cancellationToken)).ReadAsync<RiskDto>(cancellationToken);

    public async Task<RiskDto> CreateAsync(Guid projectId, SaveRiskRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/risks/projects/{projectId}", request, cancellationToken)).ReadAsync<RiskDto>(cancellationToken);

    public async Task<RiskDto> UpdateAsync(Guid riskId, SaveRiskRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/risks/{riskId}", request, cancellationToken)).ReadAsync<RiskDto>(cancellationToken);

    public async Task DeleteAsync(Guid riskId, CancellationToken cancellationToken = default) =>
        await (await http.DeleteAsync($"api/v1/risks/{riskId}", cancellationToken)).EnsureOkAsync(cancellationToken);
}

public static class RiskClientRegistration
{
    public static IServiceCollection AddRiskClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<IRiskService, HttpRiskService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

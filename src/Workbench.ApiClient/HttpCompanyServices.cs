using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Companies;

namespace Workbench.ApiClient;

public sealed class HttpCompanyService(HttpClient http) : ICompanyService
{
    public async Task<CompanyContextDto> GetContextAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/companies/context", cancellationToken)).ReadAsync<CompanyContextDto>(cancellationToken);

    public async Task<CompanyContextDto> SelectAsync(SelectCompanyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/companies/context", request, cancellationToken)).ReadAsync<CompanyContextDto>(cancellationToken);

    public async Task<IReadOnlyList<CompanyDto>> ListAsync(bool includeInactive = true, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/companies?includeInactive={includeInactive}", cancellationToken)).ReadAsync<List<CompanyDto>>(cancellationToken);

    public async Task<CompanyDto> CreateAsync(SaveCompanyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/companies", request, cancellationToken)).ReadAsync<CompanyDto>(cancellationToken);

    public async Task<CompanyDto> UpdateAsync(Guid companyId, SaveCompanyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/companies/{companyId}", request, cancellationToken)).ReadAsync<CompanyDto>(cancellationToken);

    public async Task<IReadOnlyList<UserCompanyAccessDto>> ListUserAccessAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/companies/access", cancellationToken)).ReadAsync<List<UserCompanyAccessDto>>(cancellationToken);

    public async Task<UserCompanyAccessDto> SetUserAccessAsync(Guid userId, SetUserCompanyAccessRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/companies/access/{userId}", request, cancellationToken)).ReadAsync<UserCompanyAccessDto>(cancellationToken);

    public async Task<IReadOnlyList<CompanyRecordDto>> ListRecordsAsync(CompanyRecordKind kind, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/companies/records/{kind}", cancellationToken)).ReadAsync<List<CompanyRecordDto>>(cancellationToken);

    public async Task<CompanyRecordDto> AssignAsync(AssignCompanyRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync("api/v1/companies/records/assign", request, cancellationToken)).ReadAsync<CompanyRecordDto>(cancellationToken);

    public async Task<IReadOnlyList<CompanyPeriodDto>> ListPeriodsAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/companies/periods/{fiscalPeriodId}", cancellationToken)).ReadAsync<List<CompanyPeriodDto>>(cancellationToken);

    public async Task<CompanyPeriodDto> SetPeriodClosedAsync(Guid companyId, Guid fiscalPeriodId, SetCompanyPeriodRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync($"api/v1/companies/{companyId}/periods/{fiscalPeriodId}", request, cancellationToken)).ReadAsync<CompanyPeriodDto>(cancellationToken);
}

public sealed class HttpIntercompanyService(HttpClient http) : IIntercompanyService
{
    public async Task<IReadOnlyList<IntercompanyAccountDto>> ListAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/companies/intercompany", cancellationToken)).ReadAsync<List<IntercompanyAccountDto>>(cancellationToken);

    public async Task<IntercompanyAccountDto> SaveAsync(SaveIntercompanyAccountRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/companies/intercompany", request, cancellationToken)).ReadAsync<IntercompanyAccountDto>(cancellationToken);

    public async Task<IReadOnlyList<IntercompanyBalanceDto>> ListBalancesAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default) =>
        await (await http.GetAsync($"api/v1/companies/intercompany/balances/{fiscalPeriodId}", cancellationToken)).ReadAsync<List<IntercompanyBalanceDto>>(cancellationToken);
}

public sealed class HttpConsolidationService(HttpClient http) : IConsolidationService
{
    public async Task<ConsolidationDto> GetAsync(ConsolidatedReport report, ConsolidationRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PostAsJsonAsync($"api/v1/companies/consolidation/{report}", request, cancellationToken)).ReadAsync<ConsolidationDto>(cancellationToken);
}

public static class CompanyClientRegistration
{
    public static IServiceCollection AddCompanyClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ICompanyService, HttpCompanyService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IIntercompanyService, HttpIntercompanyService>(c => c.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IConsolidationService, HttpConsolidationService>(c => c.BaseAddress = serverBaseAddress);
        return services;
    }
}

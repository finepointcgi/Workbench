using System.Net.Http.Json;
using Workbench.Contracts.Company;

namespace Workbench.ApiClient;

public sealed class HttpCompanyProfileService(HttpClient http) : ICompanyProfileService
{
    public async Task<CompanyProfileDto> GetAsync(CancellationToken cancellationToken = default) =>
        await (await http.GetAsync("api/v1/company-profile", cancellationToken)).ReadAsync<CompanyProfileDto>(cancellationToken);

    public async Task<CompanyProfileDto> UpdateAsync(UpdateCompanyProfileRequest request, CancellationToken cancellationToken = default) =>
        await (await http.PutAsJsonAsync("api/v1/company-profile", request, cancellationToken)).ReadAsync<CompanyProfileDto>(cancellationToken);
}

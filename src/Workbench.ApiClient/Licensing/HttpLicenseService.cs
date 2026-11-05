using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Licensing;

namespace Workbench.ApiClient.Licensing;

public sealed class HttpLicenseService(HttpClient http) : ILicenseService
{
    public async Task<LicenseStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/license", cancellationToken);
        return await response.ReadAsync<LicenseStatusDto>(cancellationToken);
    }

    public async Task<LicenseStatusDto> ActivateAsync(ActivateLicenseRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync("api/v1/license", request, cancellationToken);
        return await response.ReadAsync<LicenseStatusDto>(cancellationToken);
    }

    public async Task<LicenseStatusDto> RemoveAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.DeleteAsync("api/v1/license", cancellationToken);
        return await response.ReadAsync<LicenseStatusDto>(cancellationToken);
    }
}

public static class LicenseClientRegistration
{
    public static IServiceCollection AddLicenseClient(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ILicenseService, HttpLicenseService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

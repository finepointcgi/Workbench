using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;

namespace Workbench.ApiClient.Security;

public sealed class HttpSignInSettingsService(HttpClient http) : ISignInSettingsService
{
    public async Task<SignInSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/admin/sign-in", cancellationToken);
        return await response.ReadAsync<SignInSettingsDto>(cancellationToken);
    }

    public Task<SignInSettingsDto> SaveProviderAsync(SaveSsoProviderRequest request, CancellationToken cancellationToken = default) => PutAsync("provider", request, cancellationToken);

    public Task<SignInSettingsDto> SaveMfaPolicyAsync(SaveMfaPolicyRequest request, CancellationToken cancellationToken = default) => PutAsync("mfa", request, cancellationToken);

    public Task<SignInSettingsDto> SaveProvisioningAsync(SaveProvisioningRequest request, CancellationToken cancellationToken = default) => PutAsync("provisioning", request, cancellationToken);

    public Task<SignInSettingsDto> SaveSessionPolicyAsync(SaveSessionPolicyRequest request, CancellationToken cancellationToken = default) => PutAsync("sessions", request, cancellationToken);

    private async Task<SignInSettingsDto> PutAsync<T>(string part, T body, CancellationToken cancellationToken)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/sign-in/{part}", body, cancellationToken);
        return await response.ReadAsync<SignInSettingsDto>(cancellationToken);
    }
}

/// <summary>The desktop app reads the status; a refused action comes back as a 403 with the message, so the event never fires here.</summary>
public sealed class HttpReauthenticationService(HttpClient http) : IReauthenticationService
{
    public event Action<ReauthenticationRequiredException>? Required { add { } remove { } }

    public async Task<ReauthenticationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("api/v1/users/me/reauthentication", cancellationToken);
        return await response.ReadAsync<ReauthenticationStatusDto>(cancellationToken);
    }
}

public sealed class HttpComplianceChecklistService(HttpClient http) : IComplianceChecklistService
{
    public async Task<ChecklistDto> GetAsync(string checklistKey, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync($"api/v1/admin/compliance/{Uri.EscapeDataString(checklistKey)}", cancellationToken);
        return await response.ReadAsync<ChecklistDto>(cancellationToken);
    }

    public async Task<ChecklistItemDto> SaveAnswerAsync(string checklistKey, string itemKey, SaveChecklistAnswerRequest request, CancellationToken cancellationToken = default)
    {
        using var response = await http.PutAsJsonAsync($"api/v1/admin/compliance/{Uri.EscapeDataString(checklistKey)}/items/{Uri.EscapeDataString(itemKey)}", request, cancellationToken);
        return await response.ReadAsync<ChecklistItemDto>(cancellationToken);
    }
}

public static class SignInSettingsClientRegistration
{
    public static IServiceCollection AddSignInSettingsClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.AddHttpClient<ISignInSettingsService, HttpSignInSettingsService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IReauthenticationService, HttpReauthenticationService>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IComplianceChecklistService, HttpComplianceChecklistService>(client => client.BaseAddress = serverBaseAddress);
        return services;
    }
}

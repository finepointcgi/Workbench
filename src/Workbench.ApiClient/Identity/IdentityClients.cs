using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.ApiClient.Identity;
using Workbench.Contracts.Identity;

namespace Workbench.ApiClient;

public static class IdentityClients
{
    /// <summary>
    /// Users, memberships and permissions over HTTP, and a bearer token on every Workbench HTTP client. Register an
    /// <see cref="IAccessTokenProvider"/> (e.g. <see cref="OidcAccessTokenProvider"/>) to sign in; without one,
    /// requests go anonymously.
    /// </summary>
    public static IServiceCollection AddIdentityClients(this IServiceCollection services, Uri serverBaseAddress)
    {
        services.TryAddSingleton<IAccessTokenProvider, NoAccessTokenProvider>();
        services.AddSingleton(new WorkbenchServer(serverBaseAddress));
        services.AddTransient<BearerTokenHandler>();
        services.ConfigureHttpClientDefaults(client => client.AddHttpMessageHandler<BearerTokenHandler>());
        services.AddHttpClient<IUserDirectory, HttpUserDirectory>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ICurrentUserContext, HttpCurrentUserContext>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<ISheetPermissions, HttpSheetPermissions>(client => client.BaseAddress = serverBaseAddress);
        services.AddHttpClient<IWorkspaceMembershipService, HttpWorkspaceMembershipService>(client => client.BaseAddress = serverBaseAddress);
        services.TryAddSingleton<ISignOutService, AccessTokenSignOutService>();
        return services;
    }
}

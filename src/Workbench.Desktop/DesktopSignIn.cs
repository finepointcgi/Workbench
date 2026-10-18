using Microsoft.Extensions.DependencyInjection;
using Workbench.ApiClient.Identity;

namespace Workbench.Desktop;

/// <summary>
/// System-browser OIDC sign-in for the desktop app. The protocol work lives in Workbench.ApiClient
/// (<see cref="OidcAccessTokenProvider"/>, <see cref="LoopbackBrowser"/>); this file only supplies the
/// platform pieces: opening the browser and keeping the refresh token in the OS credential store.
/// </summary>
internal static class DesktopSignIn
{
    /// <summary>
    /// Reads <c>WORKBENCH_OIDC_AUTHORITY</c>, <c>WORKBENCH_OIDC_CLIENT_ID</c> and optional <c>WORKBENCH_OIDC_SCOPE</c>
    /// (add the server API's scope for Entra ID, e.g. <c>api://workbench/access_as_user</c>). Without them the app
    /// calls the server anonymously, which a server with authentication turned on refuses.
    /// Call before AddWorkbenchApiClient, which only adds a token provider when none is registered.
    /// </summary>
    public static IServiceCollection AddDesktopSignIn(this IServiceCollection services)
    {
        var authority = Environment.GetEnvironmentVariable("WORKBENCH_OIDC_AUTHORITY");
        var clientId = Environment.GetEnvironmentVariable("WORKBENCH_OIDC_CLIENT_ID");
        if (string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId))
        {
            return services;
        }

        var scope = Environment.GetEnvironmentVariable("WORKBENCH_OIDC_SCOPE");
        var settings = new DesktopOidcSettings(authority, clientId,
            string.IsNullOrWhiteSpace(scope) ? DesktopOidcSettings.DefaultScope : $"{DesktopOidcSettings.DefaultScope} {scope}");
        services.AddSingleton<IRefreshTokenStore, SecureStorageRefreshTokenStore>();
        services.AddSingleton<IAccessTokenProvider>(sp => new OidcAccessTokenProvider(
            settings,
            uri => MainThread.InvokeOnMainThreadAsync(() => Launcher.Default.OpenAsync(uri)),
            sp.GetRequiredService<IRefreshTokenStore>()));
        return services;
    }
}

/// <summary>
/// Keeps the refresh token in the Keychain (macOS) or Credential Locker (Windows). If secure storage isn't
/// available (for example a Mac Catalyst build without keychain access), the token lives for this run only.
/// </summary>
internal sealed class SecureStorageRefreshTokenStore : IRefreshTokenStore
{
    private const string Key = "workbench.oidc.refresh_token";
    private string? _fallback;

    public async Task<string?> LoadAsync()
    {
        try
        {
            return await SecureStorage.Default.GetAsync(Key) ?? _fallback;
        }
        catch (Exception)
        {
            return _fallback;
        }
    }

    public async Task SaveAsync(string? refreshToken)
    {
        _fallback = refreshToken;
        try
        {
            if (refreshToken is null)
            {
                SecureStorage.Default.Remove(Key);
            }
            else
            {
                await SecureStorage.Default.SetAsync(Key, refreshToken);
            }
        }
        catch (Exception)
        {
            // Keep the in-memory copy.
        }
    }
}

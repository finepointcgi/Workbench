using Duende.IdentityModel.OidcClient;

namespace Workbench.ApiClient.Identity;

/// <summary>Settings for the desktop app's sign-in. The client must be a public client that allows loopback redirects.</summary>
public sealed record DesktopOidcSettings(string Authority, string ClientId, string Scope)
{
    /// <summary><c>offline_access</c> gets a refresh token so the user doesn't sign in every hour.</summary>
    public const string DefaultScope = "openid profile email offline_access";
}

/// <summary>Where the refresh token survives restarts. The desktop app uses the OS keychain / credential store.</summary>
public interface IRefreshTokenStore
{
    Task<string?> LoadAsync();

    Task SaveAsync(string? refreshToken);
}

public sealed class InMemoryRefreshTokenStore : IRefreshTokenStore
{
    private string? _token;

    public Task<string?> LoadAsync() => Task.FromResult(_token);

    public Task SaveAsync(string? refreshToken)
    {
        _token = refreshToken;
        return Task.CompletedTask;
    }
}

/// <summary>
/// OpenID Connect authorization code + PKCE in the system browser (Duende.IdentityModel.OidcClient), with silent
/// refresh. Works with Entra ID and any other OIDC provider. Tokens are cached in memory; only the refresh token is
/// persisted.
/// </summary>
public sealed class OidcAccessTokenProvider(
    DesktopOidcSettings settings,
    Func<Uri, Task> openSystemBrowser,
    IRefreshTokenStore store,
    TimeProvider? clock = null) : IAccessTokenProvider
{
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromMinutes(2);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAt;

    public async ValueTask<string?> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_accessToken is not null && _clock.GetUtcNow() < _expiresAt - RefreshMargin)
            {
                return _accessToken;
            }

            if (await store.LoadAsync() is { Length: > 0 } refreshToken)
            {
                var refreshed = await CreateClient(new LoopbackBrowser(openSystemBrowser, 0)).RefreshTokenAsync(refreshToken, cancellationToken: cancellationToken);
                if (!refreshed.IsError)
                {
                    return await AcceptAsync(refreshed.AccessToken, refreshed.AccessTokenExpiration, refreshed.RefreshToken ?? refreshToken);
                }
            }

            var browser = new LoopbackBrowser(openSystemBrowser, LoopbackBrowser.GetFreePort());
            var login = await CreateClient(browser).LoginAsync(new LoginRequest(), cancellationToken);
            if (login.IsError)
            {
                throw new HttpRequestException($"Sign-in failed: {login.Error} {login.ErrorDescription}".Trim());
            }

            return await AcceptAsync(login.AccessToken, login.AccessTokenExpiration, login.RefreshToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _accessToken = null;
            await store.SaveAsync(null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string> AcceptAsync(string accessToken, DateTimeOffset expiresAt, string? refreshToken)
    {
        _accessToken = accessToken;
        _expiresAt = expiresAt;
        await store.SaveAsync(refreshToken);
        return accessToken;
    }

    private OidcClient CreateClient(LoopbackBrowser browser) => new(new OidcClientOptions
    {
        Authority = settings.Authority,
        ClientId = settings.ClientId,
        Scope = settings.Scope,
        RedirectUri = browser.RedirectUri,
        Browser = browser,
        // Entra ID's issuer for multi-tenant authorities differs from the authority URL; the server validates tokens anyway.
        Policy = new Policy { Discovery = new Duende.IdentityModel.Client.DiscoveryPolicy { ValidateIssuerName = false } },
    });
}

/// <summary>Desktop sign-out: forget the tokens; the next server call opens the sign-in page again.</summary>
public sealed class AccessTokenSignOutService(IAccessTokenProvider tokens) : Workbench.Contracts.Identity.ISignOutService
{
    public Task SignOutAsync() => tokens.SignOutAsync(CancellationToken.None);
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Workbench.Contracts.AccountingExport;

namespace Workbench.Application.AccountingLive;

public sealed record TokenSet(string AccessToken, string RefreshToken, int AccessExpiresInSeconds, int? RefreshExpiresInSeconds);

public sealed record ProviderOrganisation(string Id, string Name);

/// <summary>The OAuth 2.0 details of one provider: where to send the user, and how to trade, renew and revoke tokens.</summary>
public interface IAccountingOAuth
{
    AccountingTarget Target { get; }
    string Name { get; }

    Uri AuthorizeUrl(ProviderApp app, string state);

    Task<TokenSet> ExchangeAsync(ProviderApp app, string code, CancellationToken ct);

    Task<TokenSet> RefreshAsync(ProviderApp app, string refreshToken, CancellationToken ct);

    Task RevokeAsync(ProviderApp app, string refreshToken, CancellationToken ct);

    /// <summary>The organisations the user connected in this flow when the provider lets them pick several (Xero). QuickBooks returns none: the company is the realmId on the redirect.</summary>
    Task<IReadOnlyList<ProviderOrganisation>> OrganisationsAsync(string accessToken, CancellationToken ct);
}

/// <summary>Shared token-endpoint plumbing: HTTP Basic client credentials, form body, error mapping.</summary>
public abstract class OAuthBase(IAccountingHttp http)
{
    protected static AuthenticationHeaderValue Basic(ProviderApp app) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{app.ClientId}:{app.ClientSecret}")));

    protected async Task<TokenSet> PostTokenAsync(Uri endpoint, ProviderApp app, Dictionary<string, string> form, bool intuitHeaders, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        request.Headers.Authorization = Basic(app);
        request.Headers.Accept.ParseAdd("application/json");
        if (intuitHeaders)
        {
            request.Headers.TryAddWithoutValidation("x-include-refresh-token-hard-expires-in", "true");
        }

        HttpResponseMessage response;
        try
        {
            response = await http.Client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new AccountingOAuthException($"Couldn't reach the sign-in server: {ex.Message}", false);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                var (error, description) = ReadError(body);
                var invalid = error == "invalid_grant" || (int)response.StatusCode == 400 && error is "invalid_grant";
                throw new AccountingOAuthException(
                    invalid ? "The provider no longer accepts this connection (the authorisation expired or was revoked). Connect again." : $"The provider refused the request ({(int)response.StatusCode}{(error is null ? "" : " " + error)}){(description is null ? "" : ": " + description)}.",
                    invalid, (int)response.StatusCode);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var access = root.GetProperty("access_token").GetString() ?? throw new AccountingOAuthException("The provider's answer had no access token.", false);
            var refresh = root.TryGetProperty("refresh_token", out var r) ? r.GetString() : null;
            if (string.IsNullOrEmpty(refresh))
            {
                throw new AccountingOAuthException("The provider's answer had no refresh token, so Workbench couldn't stay connected.", false);
            }

            var expires = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 1800;
            int? refreshExpires = root.TryGetProperty("x_refresh_token_expires_in", out var re) && re.TryGetInt32(out var rs) ? rs : null;
            return new TokenSet(access, refresh, expires, refreshExpires);
        }
    }

    private static (string? Error, string? Description) ReadError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            return (root.TryGetProperty("error", out var e) ? e.GetString() : null, root.TryGetProperty("error_description", out var d) ? d.GetString() : null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    protected static string Query(IEnumerable<KeyValuePair<string, string>> parts) =>
        string.Join("&", parts.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}

/// <summary>
/// QuickBooks Online (Intuit OAuth 2.0). Source: developer.intuit.com › QuickBooks Online › Set up OAuth 2.0
/// (https://developer.intuit.com/app/developer/qbo/docs/develop/authentication-and-authorization/oauth-2.0).
/// Authorize: https://appcenter.intuit.com/connect/oauth2 (client_id, response_type=code, scope=com.intuit.quickbooks.accounting, redirect_uri, state);
/// the redirect carries code, state and realmId. Token and refresh: POST https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer with HTTP Basic
/// client credentials and a form body. Revoke: POST https://developer.api.intuit.com/v2/oauth2/tokens/revoke with {"token": ...}.
/// The same endpoints serve sandbox and production; which one you reach depends on whose keys (development or production) are used.
/// Access tokens last 3,600 s, refresh tokens have a rolling 100-day life ("always store the latest refresh_token value"). PKCE isn't documented
/// for this flow, so none is used; the one-time <c>state</c> is the CSRF defence.
/// </summary>
public sealed class QuickBooksOAuth(IAccountingHttp http) : OAuthBase(http), IAccountingOAuth
{
    public const string DefaultScope = "com.intuit.quickbooks.accounting";
    private static readonly Uri TokenEndpoint = new("https://oauth.platform.intuit.com/oauth2/v1/tokens/bearer");
    private static readonly Uri RevokeEndpoint = new("https://developer.api.intuit.com/v2/oauth2/tokens/revoke");
    private readonly IAccountingHttp _http = http;

    public AccountingTarget Target => AccountingTarget.QuickBooks;
    public string Name => "QuickBooks Online";

    public Uri AuthorizeUrl(ProviderApp app, string state) => new("https://appcenter.intuit.com/connect/oauth2?" + Query(
    [
        new("client_id", app.ClientId!), new("response_type", "code"), new("scope", string.IsNullOrWhiteSpace(app.Scopes) ? DefaultScope : app.Scopes),
        new("redirect_uri", app.RedirectUri!), new("state", state),
    ]));

    public Task<TokenSet> ExchangeAsync(ProviderApp app, string code, CancellationToken ct) =>
        PostTokenAsync(TokenEndpoint, app, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = app.RedirectUri! }, true, ct);

    public Task<TokenSet> RefreshAsync(ProviderApp app, string refreshToken, CancellationToken ct) =>
        PostTokenAsync(TokenEndpoint, app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, true, ct);

    public Task<IReadOnlyList<ProviderOrganisation>> OrganisationsAsync(string accessToken, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProviderOrganisation>>([]);

    public async Task RevokeAsync(ProviderApp app, string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new StringContent(JsonSerializer.Serialize(new { token = refreshToken }), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = Basic(app);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountingOAuthException($"QuickBooks didn't confirm the disconnect ({(int)response.StatusCode}).", false, (int)response.StatusCode);
        }
    }
}

/// <summary>
/// Xero (OAuth 2.0 standard authorization code flow, for a web app that keeps a client secret). Source: developer.xero.com › OAuth 2.0 ›
/// "The standard authorization code flow" (https://developer.xero.com/documentation/guides/oauth2/auth-flow) and "Scopes"
/// (https://developer.xero.com/documentation/guides/oauth2/scopes). Authorize: https://login.xero.com/identity/connect/authorize; token and refresh:
/// POST https://identity.xero.com/connect/token (Basic client credentials, form body); revoke: POST https://identity.xero.com/connect/revocation.
/// Access tokens last 30 min and refresh tokens 60 days; every refresh returns a new refresh token that must be saved, and the previous one still
/// works for a 30-minute grace period. Xero documents PKCE only for the secret-less (native app) flow, so this flow uses the one-time <c>state</c>.
/// Scopes: since March 2026 Web apps hold the granular scopes (accounting.invoices, accounting.manualjournals, ...); the broad accounting.transactions
/// is deprecated until September 2027. Override with <c>Accounting:Xero:Scopes</c> for an app that still holds the broad ones.
/// </summary>
public sealed class XeroOAuth(IAccountingHttp http) : OAuthBase(http), IAccountingOAuth
{
    public const string DefaultScope = "offline_access accounting.invoices accounting.manualjournals accounting.contacts accounting.settings";
    private static readonly Uri TokenEndpoint = new("https://identity.xero.com/connect/token");
    private static readonly Uri RevokeEndpoint = new("https://identity.xero.com/connect/revocation");
    private readonly IAccountingHttp _http = http;

    public AccountingTarget Target => AccountingTarget.Xero;
    public string Name => "Xero";

    public Uri AuthorizeUrl(ProviderApp app, string state) => new("https://login.xero.com/identity/connect/authorize?" + Query(
    [
        new("response_type", "code"), new("client_id", app.ClientId!), new("redirect_uri", app.RedirectUri!),
        new("scope", string.IsNullOrWhiteSpace(app.Scopes) ? DefaultScope : app.Scopes), new("state", state),
    ]));

    public Task<TokenSet> ExchangeAsync(ProviderApp app, string code, CancellationToken ct) =>
        PostTokenAsync(TokenEndpoint, app, new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = app.RedirectUri! }, false, ct);

    public Task<TokenSet> RefreshAsync(ProviderApp app, string refreshToken, CancellationToken ct) =>
        PostTokenAsync(TokenEndpoint, app, new() { ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken }, false, ct);

    public async Task RevokeAsync(ProviderApp app, string refreshToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, RevokeEndpoint) { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["token"] = refreshToken }) };
        request.Headers.Authorization = Basic(app);
        using var response = await _http.Client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountingOAuthException($"Xero didn't confirm the disconnect ({(int)response.StatusCode}).", false, (int)response.StatusCode);
        }
    }

    public Task<IReadOnlyList<ProviderOrganisation>> OrganisationsAsync(string accessToken, CancellationToken ct) => ListOrganisationsAsync(accessToken, ct);

    /// <summary>GET https://api.xero.com/connections: the organisations the user connected (tenantType ORGANISATION).</summary>
    private async Task<IReadOnlyList<ProviderOrganisation>> ListOrganisationsAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.xero.com/connections");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await _http.Client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            throw new AccountingOAuthException($"Xero wouldn't list the connected organisations ({(int)response.StatusCode}).", false, (int)response.StatusCode);
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.EnumerateArray()
            .Where(c => string.Equals(c.GetProperty("tenantType").GetString(), "ORGANISATION", StringComparison.OrdinalIgnoreCase))
            .Select(c => new ProviderOrganisation(c.GetProperty("tenantId").GetString()!, c.TryGetProperty("tenantName", out var n) ? n.GetString() ?? "Xero organisation" : "Xero organisation"))
            .ToList();
    }
}

using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Workbench.Tests.Identity;

/// <summary>
/// A local OpenID Connect provider for tests, served as an <see cref="HttpMessageHandler"/> (the OIDC handler's backchannel):
/// discovery, signing keys and the token endpoint, with authorization codes issued by <see cref="Authorize"/> instead of a
/// login page. It checks PKCE and the client secret like a real provider and signs ID tokens with RS256, echoing the nonce.
/// </summary>
internal sealed class MockOidcProvider : HttpMessageHandler
{
    private readonly RsaSecurityKey _key = new(RSA.Create(2048)) { KeyId = "test-key" };
    private readonly ConcurrentDictionary<string, Pending> _codes = new();

    public MockOidcProvider(string issuer, string clientId, string clientSecret)
    {
        Issuer = issuer.TrimEnd('/');
        ClientId = clientId;
        ClientSecret = clientSecret;
    }

    public string Issuer { get; }
    public string ClientId { get; }
    public string ClientSecret { get; }

    /// <summary>The query of the last authorization request (prompt, max_age, acr_values, claims...).</summary>
    public Dictionary<string, string> LastAuthorizeQuery { get; private set; } = [];

    public int TokenRequests { get; private set; }

    /// <summary>The person the next sign-in authenticates as.</summary>
    public sealed record User(string Email, string Name, string[] Amr, string[]? Groups = null, string? Acr = null, DateTimeOffset? AuthTime = null);

    private sealed record Pending(string Nonce, string CodeChallenge, string RedirectUri, User User);

    /// <summary>Plays the provider's login page: takes the authorization URL Workbench redirected to and returns where the browser posts back.</summary>
    public (string RedirectUri, Dictionary<string, string> Form) Authorize(Uri authorizeUrl, User user)
    {
        Assert.StartsWith($"{Issuer}/authorize", authorizeUrl.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(authorizeUrl.Query).ToDictionary(p => p.Key, p => p.Value.ToString());
        LastAuthorizeQuery = query;
        Assert.Equal(ClientId, query["client_id"]);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        var code = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Pending(query["nonce"], query["code_challenge"], query["redirect_uri"], user);
        return (query["redirect_uri"], new Dictionary<string, string> { ["code"] = code, ["state"] = query["state"] });
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (url == $"{Issuer}/.well-known/openid-configuration")
        {
            return Json(new Dictionary<string, object>
            {
                ["issuer"] = Issuer,
                ["authorization_endpoint"] = $"{Issuer}/authorize",
                ["token_endpoint"] = $"{Issuer}/token",
                ["jwks_uri"] = $"{Issuer}/keys",
                ["end_session_endpoint"] = $"{Issuer}/logout",
                ["response_types_supported"] = new[] { "code" },
                ["subject_types_supported"] = new[] { "public" },
                ["id_token_signing_alg_values_supported"] = new[] { "RS256" },
                ["code_challenge_methods_supported"] = new[] { "S256" },
            });
        }

        if (url == $"{Issuer}/keys")
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(_key);
            return Json(new { keys = new[] { new { kty = jwk.Kty, kid = jwk.Kid, use = "sig", alg = "RS256", n = jwk.N, e = jwk.E } } });
        }

        if (url == $"{Issuer}/token" && request.Method == HttpMethod.Post)
        {
            TokenRequests++;
            var form = QueryHelpers.ParseQuery(await request.Content!.ReadAsStringAsync(cancellationToken)).ToDictionary(p => p.Key, p => p.Value.ToString());
            if (!form.TryGetValue("code", out var code) || !_codes.TryRemove(code, out var pending))
            {
                return Error("invalid_grant");
            }

            var verifierHash = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(form.GetValueOrDefault("code_verifier") ?? "")));
            var secret = form.GetValueOrDefault("client_secret") ?? BasicSecret(request);
            if (verifierHash != pending.CodeChallenge || form.GetValueOrDefault("redirect_uri") != pending.RedirectUri || secret != ClientSecret)
            {
                return Error("invalid_grant");
            }

            var accessToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
            return Json(new { access_token = accessToken, token_type = "Bearer", expires_in = 3600, id_token = IdToken(pending, accessToken) });
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    private string IdToken(Pending pending, string accessToken)
    {
        var now = DateTimeOffset.UtcNow;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = pending.User.Email,
            ["email"] = pending.User.Email,
            ["name"] = pending.User.Name,
            ["nonce"] = pending.Nonce,
            ["amr"] = pending.User.Amr,
            ["auth_time"] = (pending.User.AuthTime ?? now).ToUnixTimeSeconds(),
            ["at_hash"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(accessToken))[..16]),
        };
        if (pending.User.Groups is { } groups)
        {
            claims["groups"] = groups;
        }

        if (pending.User.Acr is { } acr)
        {
            claims["acr"] = acr;
        }

        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime.AddMinutes(-1),
            Expires = now.UtcDateTime.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(_key, SecurityAlgorithms.RsaSha256),
        });
    }

    private static string? BasicSecret(HttpRequestMessage request)
    {
        if (request.Headers.Authorization is not { Scheme: "Basic", Parameter: { } value })
        {
            return null;
        }

        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(value));
        return Uri.UnescapeDataString(decoded[(decoded.IndexOf(':') + 1)..]);
    }

    private static HttpResponseMessage Json(object body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Error(string error) =>
        new(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error }), Encoding.UTF8, "application/json") };
}

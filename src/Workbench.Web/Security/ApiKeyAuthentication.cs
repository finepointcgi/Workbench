using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Workbench.Application.Integrations;
using Workbench.Application.Security;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;

namespace Workbench.Web.Security;

/// <summary>
/// Authenticates REST API calls made with an integration API key (<c>Authorization: Bearer wbk_…</c> or <c>X-Api-Key: wbk_…</c>).
/// The principal is the key's service user (its email and name), so <c>ICurrentUser</c>, <c>AccessControl</c> and every
/// permission decorator treat it like any signed-in user with the key's organization roles. Keys work only under <c>/api/</c>:
/// pages, hubs and SCIM never accept them. Which API areas a key may call is checked by <see cref="ApiKeyScopeEnforcement"/>.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";

    /// <summary>Whether the request carries something that looks like a Workbench API key (the selector sends it here).</summary>
    public static bool HasApiKey(HttpRequest request) => ApiKeyAuthenticator.LooksLikeKey(Presented(request));

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Presented(Request) is not { } presented || !ApiKeyAuthenticator.LooksLikeKey(presented))
        {
            return AuthenticateResult.NoResult();
        }

        if (!Request.Path.StartsWithSegments("/api"))
        {
            return AuthenticateResult.Fail("API keys only work for the REST API.");
        }

        var authenticator = Context.RequestServices.GetRequiredService<ApiKeyAuthenticator>();
        var (identity, failure) = await authenticator.AuthenticateAsync(presented, Context.Connection.RemoteIpAddress?.ToString(), Context.RequestAborted);
        if (identity is null)
        {
            return AuthenticateResult.Fail(failure ?? "Invalid API key.");
        }

        var claims = new List<Claim>
        {
            new("email", identity.Email),
            new("name", identity.DisplayName),
            new(ApiKeyPrincipal.KeyIdClaim, identity.KeyId.ToString()),
        };
        claims.AddRange(identity.Scopes.Select(s => new Claim(ApiKeyPrincipal.ScopesClaim, s)));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, SchemeName, "name", "role"));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"workbench-api\", error=\"invalid_token\"";
        return Task.CompletedTask;
    }

    private static string? Presented(HttpRequest request)
    {
        var header = request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return header["Bearer ".Length..].Trim();
        }

        var apiKey = request.Headers[HeaderName].ToString().Trim();
        return apiKey.Length > 0 ? apiKey : null;
    }
}

/// <summary>
/// Refuses API key requests outside the key's scopes (403, recorded as <c>apikey.rejected</c>), before any endpoint runs.
/// This only narrows: an allowed request still goes through the permission decorators as the key's service user.
/// </summary>
public static class ApiKeyScopeEnforcement
{
    public static IApplicationBuilder UseWorkbenchApiKeyScopes(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (context.User.FindFirst(ApiKeyPrincipal.KeyIdClaim) is not { } keyClaim)
            {
                await next();
                return;
            }

            var scopes = context.User.FindAll(ApiKeyPrincipal.ScopesClaim).Select(c => c.Value).ToList();
            var (allowed, reason) = ApiKeyScopes.Check(scopes, context.Request.Path.Value ?? "", context.Request.Method);
            if (allowed)
            {
                await next();
                return;
            }

            var log = context.RequestServices.GetRequiredService<SecurityEventLog>();
            await log.RecordAsync(SecurityEventTypes.ApiKeyRejected, $"{context.Request.Method} {context.Request.Path}", SecurityOutcomes.Denied,
                $"Key {keyClaim.Value}: {reason}", cancellationToken: context.RequestAborted);
            context.Items[HttpSecurityEventContext.DenialRecordedKey] = true;
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await Results.Problem(reason, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
        });
}

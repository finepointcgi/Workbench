using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Workbench.Application.Security;
using Workbench.Contracts.Security;

namespace Workbench.Web.Security;

/// <summary>
/// Authenticates the identity provider's SCIM calls by the shared bearer token in <c>Scim:BearerToken</c>, compared in
/// constant time. The resulting principal has no email, so it can't use any other part of Workbench.
/// </summary>
internal sealed class ScimAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IConfiguration configuration)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Scim";
    public const string PolicyName = "Scim";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var expected = configuration[$"{ScimSettings.SectionName}:{nameof(ScimSettings.BearerToken)}"]?.Trim();
        if (string.IsNullOrEmpty(expected) || !FixedTimeEquals(header["Bearer ".Length..].Trim(), expected))
        {
            return Task.FromResult(AuthenticateResult.Fail("Invalid SCIM token."));
        }

        var identity = new ClaimsIdentity([new Claim("scim_client", "identity-provider")], SchemeName);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        var log = Context.RequestServices.GetRequiredService<SecurityEventLog>();
        await log.RecordAsync(SecurityEventTypes.Scim, $"{Request.Method} {Request.Path}", SecurityOutcomes.Denied, "Missing or invalid bearer token", actor: UserLifecycleActor);
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"scim\"";
        await ScimEndpoints.WriteErrorAsync(Response, StatusCodes.Status401Unauthorized, "Missing or invalid bearer token.");
    }

    private const string UserLifecycleActor = Application.Identity.UserLifecycle.ScimActor;

    // Hashing first makes the comparison length-independent.
    private static bool FixedTimeEquals(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(presented)), SHA256.HashData(Encoding.UTF8.GetBytes(expected)));
}

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Security;
using Workbench.Web.Security;

namespace Workbench.Web.Identity;

/// <summary>
/// <see cref="ISignInContext"/> from the signed-in principal's claims: the cookie's <c>wb_auth_time</c>, <c>wb_mfa</c> and
/// <c>wb_method</c> (set at sign-in), or a bearer token's <c>auth_time</c> and <c>amr</c>. A bearer token without
/// <c>auth_time</c> has no authentication time, so sensitive actions need the browser.
/// </summary>
internal sealed class WebSignInContext(CurrentPrincipal current, IHttpContextAccessor http) : ISignInContext
{
    public SignInFacts? Current => From(current.Principal ?? http.HttpContext?.User);

    internal static SignInFacts? From(ClaimsPrincipal? principal)
    {
        if (ClaimsUser.EmailOf(principal) is null)
        {
            return null;
        }

        var method = principal!.FindFirst(OrganizationSso.MethodClaim)?.Value;
        if (Millis(principal, OrganizationSso.AuthTimeClaim) is { } authenticatedAt)
        {
            return new SignInFacts(authenticatedAt, principal.FindFirst(OrganizationSso.MfaClaim)?.Value == "true", method);
        }

        // A bearer token (desktop app): only its auth_time says when the person authenticated.
        var authTime = long.TryParse(principal.FindFirst("auth_time")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime
            : (DateTime?)null;
        var amr = principal.FindAll("amr").Select(c => c.Value).ToList();
        return new SignInFacts(authTime, amr.Any(a => a is "mfa" or "otp" or "hwk" or "swk" or "fido"), method ?? (principal.HasClaim(c => c.Type == "iss") ? "bearer" : null));
    }

    private static DateTime? Millis(ClaimsPrincipal principal, string type) =>
        long.TryParse(principal.FindFirst(type)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime
            : null;
}

/// <summary>The organization's stricter session limit (<c>SignInSettings.SessionMaxHours</c>), checked on every cookie request.</summary>
internal static class OrganizationSessionPolicy
{
    public static async Task ValidateCookieAsync(CookieValidatePrincipalContext context)
    {
        if (context.Principal is null)
        {
            return; // already rejected
        }

        var services = context.HttpContext.RequestServices;
        if (TenantScope.Current is null && services.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            return;
        }

        var policy = await services.GetRequiredService<SignInSettingsStore>().GetAsync(context.HttpContext.RequestAborted);
        if (policy.Settings.SessionMaxHours is not { } hours || SessionValidation.SignedInAtUtc(context.Principal) is not { } signedInAt
            || services.GetRequiredService<TimeProvider>().GetUtcNow().UtcDateTime - signedInAt <= TimeSpan.FromHours(hours))
        {
            return;
        }

        var email = ClaimsUser.EmailOf(context.Principal);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        await services.GetRequiredService<SecurityEventLog>().RecordAsync(SecurityEventTypes.SessionRejected, email, SecurityOutcomes.Denied,
            $"The session reached the organization's {hours}-hour limit.", actor: email);
    }
}

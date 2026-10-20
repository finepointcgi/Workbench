using System.Security.Claims;

namespace Workbench.Web.Identity;

/// <summary>Reads the user's email and name from claims issued by Entra ID, other OIDC providers or the dev sign-in.</summary>
public static class ClaimsUser
{
    // Entra ID often leaves out "email" but always sends preferred_username (v2) or upn (v1) for work accounts.
    private static readonly string[] EmailClaims = ["email", ClaimTypes.Email, "preferred_username", "upn", ClaimTypes.Upn, "unique_name"];
    private static readonly string[] NameClaims = ["name", ClaimTypes.Name, "given_name"];

    /// <summary>The lower-case email, or null when the principal is anonymous or has no email-like claim.</summary>
    public static string? EmailOf(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || IsSupplier(principal))
        {
            return null;
        }

        foreach (var type in EmailClaims)
        {
            var value = principal.FindFirst(type)?.Value?.Trim();
            if (!string.IsNullOrEmpty(value) && value.Contains('@'))
            {
                return value.ToLowerInvariant();
            }
        }

        return null;
    }

    public static string? NameOf(ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true || IsSupplier(principal))
        {
            return null;
        }

        return NameClaims.Select(t => principal.FindFirst(t)?.Value?.Trim()).FirstOrDefault(v => !string.IsNullOrEmpty(v));
    }

    /// <summary>A supplier portal contact is never an internal user, whatever claims their cookie might carry.</summary>
    private static bool IsSupplier(ClaimsPrincipal principal) =>
        principal.Identities.Any(i => i.AuthenticationType == Supplier.SupplierPortalAuthentication.Scheme);
}

using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Workbench.Application.Purchasing;
using Workbench.Application.Tenancy;
using Workbench.Web.Security;

namespace Workbench.Web.Supplier;

/// <summary>
/// The supplier portal's own sign-in kind, apart from internal users in every way: its own cookie scheme
/// (<see cref="Scheme"/>) and cookie (<c>.Workbench.Supplier</c>, path <c>/supplier</c>, so the browser never sends it to
/// the internal app, its API or the Blazor circuit), its own claim types (no email, name or role claims, so nothing that
/// reads an internal user sees a vendor contact), and the authentication selector hands every <c>/supplier</c> request to
/// it, so an internal user's cookie isn't even read there. Sessions idle out after 30 minutes and end after 12 hours, when
/// the contact or vendor is deactivated, the password changes, or (MultiTenant) the tenant differs.
/// </summary>
public static class SupplierPortalAuthentication
{
    public const string Scheme = "SupplierPortal";
    public const string Policy = "SupplierPortal";
    public const string ContactClaim = "wb_supplier_contact";
    public const string VendorClaim = "wb_supplier_vendor";
    public const string NameClaim = "wb_supplier_name";
    public const string VersionClaim = "wb_supplier_session";
    private const string SignedInAtClaim = "wb_supplier_signed_in";

    public static readonly TimeSpan IdleTimeout = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromHours(12);

    public static bool IsPortalPath(PathString path) => path.StartsWithSegments(SupplierPortalPaths.Base);

    public static WebApplicationBuilder AddWorkbenchSupplierPortal(this WebApplicationBuilder builder)
    {
        var multiTenant = builder.Configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>()?.IsMultiTenant ?? false;
        builder.Services.AddAuthentication().AddCookie(Scheme, o =>
        {
            o.Cookie.Name = ".Workbench.Supplier";
            o.Cookie.Path = SupplierPortalPaths.Base;
            o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.ExpireTimeSpan = IdleTimeout;
            o.SlidingExpiration = true;
            o.LoginPath = SupplierPortalPaths.SignIn;
            o.LogoutPath = SupplierPortalPaths.Base + "/signout";
            o.AccessDeniedPath = SupplierPortalPaths.SignIn;
            o.ReturnUrlParameter = "returnUrl";
            o.Events.OnValidatePrincipal = async context =>
            {
                var principal = context.Principal;
                var reason = await InvalidReasonAsync(context.HttpContext, principal, multiTenant);
                if (reason is not null)
                {
                    context.RejectPrincipal();
                    await context.HttpContext.SignOutAsync(Scheme);
                }
            };
        });
        builder.Services.AddAuthorizationBuilder().AddPolicy(Policy, p => p
            .AddAuthenticationSchemes(Scheme)
            .RequireAuthenticatedUser()
            .RequireClaim(ContactClaim)
            .RequireClaim(VendorClaim));
        builder.Services.AddScoped<ISupplierPrincipal, HttpSupplierPrincipal>();
        return builder;
    }

    public static ClaimsPrincipal Principal(SupplierSignInResult result, DateTimeOffset now)
    {
        var claims = new List<Claim>
        {
            new(ContactClaim, result.ContactId.ToString("D")),
            new(VendorClaim, result.VendorId.ToString("D")),
            new(NameClaim, result.Name),
            new(VersionClaim, result.SessionVersion.ToString(CultureInfo.InvariantCulture)),
            new(SignedInAtClaim, now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)),

            // MultiTenant: the cookie is only good for the tenant it was issued for.
            new(SessionValidation.TenantClaim, SessionValidation.CurrentTenantClaimValue()),
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme, NameClaim, null));
    }

    /// <summary>The signed-in contact, only from this scheme's identity.</summary>
    public static Guid? ContactId(ClaimsPrincipal? principal) =>
        principal?.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == Scheme)?.FindFirst(ContactClaim)?.Value is { } v && Guid.TryParse(v, out var id) ? id : null;

    public static Guid? VendorId(ClaimsPrincipal? principal) =>
        principal?.Identities.FirstOrDefault(i => i.IsAuthenticated && i.AuthenticationType == Scheme)?.FindFirst(VendorClaim)?.Value is { } v && Guid.TryParse(v, out var id) ? id : null;

    private static async Task<string?> InvalidReasonAsync(HttpContext http, ClaimsPrincipal? principal, bool multiTenant)
    {
        if (ContactId(principal) is not { } contactId || VendorId(principal) is not { } vendorId
            || !int.TryParse(principal!.FindFirst(VersionClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version))
        {
            return "The portal session is incomplete.";
        }

        if (!long.TryParse(principal.FindFirst(SignedInAtClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
            || DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(ms) > AbsoluteLifetime)
        {
            return "The portal session reached its maximum lifetime.";
        }

        if (multiTenant && principal.FindFirst(SessionValidation.TenantClaim)?.Value != SessionValidation.CurrentTenantClaimValue())
        {
            return "The portal session belongs to another organization.";
        }

        var signIn = http.RequestServices.GetRequiredService<SupplierSignIn>();
        return await signIn.IsSessionValidAsync(contactId, vendorId, version, http.RequestAborted) ? null : "The contact's portal access ended.";
    }
}

/// <summary><see cref="ISupplierPrincipal"/> from the request's portal cookie (only the portal scheme's identity counts).</summary>
internal sealed class HttpSupplierPrincipal(IHttpContextAccessor http) : ISupplierPrincipal
{
    public Guid? ContactId => SupplierPortalAuthentication.ContactId(http.HttpContext?.User);

    public string? IpAddress => http.HttpContext?.Connection.RemoteIpAddress?.ToString();
}

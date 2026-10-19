using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Web.Identity;

/// <summary>
/// Development-only sign-in: pick a seeded user and get the auth cookie, no identity provider needed. The
/// endpoints are only mapped when <see cref="IsEnabled"/> is true, so they don't exist in any other environment.
/// </summary>
public static class DevSignIn
{
    public const string Path = "/account/dev-signin";

    public static bool IsEnabled(IHostEnvironment environment, OidcSettings oidc) =>
        environment.IsDevelopment() && !oidc.IsConfigured;

    internal static void Map(IEndpointRouteBuilder app)
    {
        app.MapGet(Path, async (HttpContext http, IAntiforgery antiforgery, IWorkbenchDbContextFactory dbFactory, TimeProvider clock, Application.Tenancy.ITenantContext tenants,
            Application.Tenancy.TenancyOptions tenancy, SignInSettingsStore signInSettings, string? returnUrl, string? reauth) =>
        {
            if (tenants.IsHost)
            {
                // The host admin site has no user table: offer the configured host admins.
                var hostAdmins = tenancy.HostAdmins.Select(e => new AppUser { Email = e.Trim().ToLowerInvariant(), DisplayName = e.Trim(), CreatedAtUtc = DateTime.UtcNow }).ToList();
                return Results.Content(RenderPage(hostAdmins, [], antiforgery.GetAndStoreTokens(http), AccountEndpoints.SafeReturnUrl(returnUrl), host: true), "text/html; charset=utf-8");
            }

            await using var db = await dbFactory.CreateAsync(http.RequestAborted);
            var users = await db.Set<AppUser>().AsNoTracking().OrderByDescending(u => u.IsActive).ThenByDescending(u => u.IsAdmin).ThenBy(u => u.Email).ToListAsync(http.RequestAborted);
            var now = clock.GetUtcNow().UtcDateTime;

            // Pending invites stand in for "first sign-in at the identity provider" in Development.
            var invites = await db.Set<UserInvite>().AsNoTracking()
                .Where(i => i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
                .OrderBy(i => i.Email)
                .ToListAsync(http.RequestAborted);
            var known = users.Select(u => u.Email).ToHashSet();
            var tokens = antiforgery.GetAndStoreTokens(http);
            var policy = await signInSettings.GetAsync(http.RequestAborted);
            var organizationIdp = policy.OrganizationIdpReady ? policy.Settings.DisplayName ?? "your organization's identity provider" : null;
            return Results.Content(RenderPage(users, invites.Where(i => !known.Contains(i.Email)).ToList(), tokens, AccountEndpoints.SafeReturnUrl(returnUrl),
                organizationIdp: organizationIdp, reauth: reauth == "1"), "text/html; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapPost(Path, async (HttpContext http, SignInGate gate, TimeProvider clock, [Microsoft.AspNetCore.Mvc.FromForm] string email, [Microsoft.AspNetCore.Mvc.FromForm] string? returnUrl) =>
        {
            email = (email ?? string.Empty).Trim().ToLowerInvariant();

            // The same gate as OIDC: deactivated users are refused; new emails need a pending invite.
            var decision = await gate.EvaluateAsync(email, null, "dev", interactive: true, http.RequestAborted);
            if (!decision.Allowed || decision.User is not { } user)
            {
                return Results.Redirect($"{AccountEndpoints.AccessDeniedPath}?reason=signin");
            }

            var now = clock.GetUtcNow();
            var identity = new ClaimsIdentity(
                [new Claim("sub", user.Email), new Claim("email", user.Email), new Claim("name", user.DisplayName), Security.SessionValidation.SignedInAt(now),
                    new Claim(OrganizationSso.AuthTimeClaim, now.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
                    new Claim(OrganizationSso.MfaClaim, "false"), new Claim(OrganizationSso.MethodClaim, "dev")],
                "dev", "name", "role");
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(AccountEndpoints.SafeReturnUrl(returnUrl));
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static string RoleLabel(AppUser user, bool host)
    {
        if (host)
        {
            return "Host admin";
        }

        if (!user.IsActive)
        {
            return "Deactivated";
        }

        var roles = string.Join(", ", new[] { user.IsAdmin ? "Admin" : null, user.IsFinance ? "Finance" : null, user.IsTimekeeper ? "Timekeeper" : null, user.IsResourceManager ? "ResourceManager" : null, user.IsProjectManager ? "Project manager" : null, user.IsBusinessDevelopment ? "Business development" : null }.OfType<string>());
        return roles.Length == 0 ? "Employee" : roles;
    }

    private static string RenderPage(IEnumerable<AppUser> users, IReadOnlyList<UserInvite> invites, AntiforgeryTokenSet tokens, string returnUrl, bool host = false,
        string? organizationIdp = null, bool reauth = false)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var html = new StringBuilder("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Dev sign-in · Workbench</title>
            <style>
              body { font-family: system-ui, sans-serif; background: #f5f6f8; margin: 0; padding: 48px 16px; color: #1f2937; }
              main { max-width: 420px; margin: 0 auto; background: #fff; border-radius: 8px; padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
              h1 { font-size: 1.4rem; margin: 0 0 4px; } h2 { font-size: 1rem; margin: 20px 0 4px; } p { color: #6b7280; margin: 0 0 20px; }
              button { display: block; width: 100%; text-align: left; margin: 8px 0; padding: 12px 14px; border: 1px solid #d1d5db; border-radius: 6px; background: #fff; cursor: pointer; font: inherit; }
              button:hover { border-color: #2563eb; background: #eff6ff; } small { display: block; color: #6b7280; }
              .admin { font-size: .75rem; color: #2563eb; margin-left: 6px; } .note { color: #92400e; background: #fffbeb; padding: 8px; border-radius: 6px; }
            </style></head><body><main>
            <h1>Sign in (development)</h1>
            <p>No identity provider is configured, so pick a seeded user. This page doesn't exist outside Development.</p>
            """);
        if (reauth)
        {
            html.Append("<p class=\"note\"><strong>Confirm it's you.</strong> A sensitive action needs a recent sign-in: pick your user again.</p>");
        }

        if (organizationIdp is not null)
        {
            html.Append($"""<p><a href="{AccountEndpoints.SignInPath}?returnUrl={E(Uri.EscapeDataString(returnUrl))}">Sign in with {E(organizationIdp)}</a></p>""");
        }

        foreach (var user in users)
        {
            html.Append($"""
                <form method="post" action="{Path}">
                  <input type="hidden" name="{E(tokens.FormFieldName)}" value="{E(tokens.RequestToken)}" />
                  <input type="hidden" name="returnUrl" value="{E(returnUrl)}" />
                  <button type="submit" name="email" value="{E(user.Email)}">{E(user.DisplayName)}<span class="admin">{E(RoleLabel(user, host))}</span><small>{E(user.Email)}</small></button>
                </form>
                """);
        }

        if (invites.Count > 0)
        {
            html.Append("<h2>Invited (first sign-in accepts the invite)</h2>");
            foreach (var invite in invites)
            {
                html.Append($"""
                    <form method="post" action="{Path}">
                      <input type="hidden" name="{E(tokens.FormFieldName)}" value="{E(tokens.RequestToken)}" />
                      <input type="hidden" name="returnUrl" value="{E(returnUrl)}" />
                      <button type="submit" name="email" value="{E(invite.Email)}">{E(invite.DisplayName)}<span class="admin">Invited</span><small>{E(invite.Email)}</small></button>
                    </form>
                    """);
            }
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }
}

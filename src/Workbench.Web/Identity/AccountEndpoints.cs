using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts.Security;

namespace Workbench.Web.Identity;

public static class AccountEndpoints
{
    public const string SignInPath = "/account/signin";
    public const string SignOutPath = "/account/signout";
    public const string AccessDeniedPath = "/account/access-denied";
    public const string SessionPingPath = "/account/session/ping";
    public const string ReauthenticatePath = "/account/reauthenticate";

    /// <summary>Sign-in and sign-out endpoints, and the dev sign-in page when it's enabled.</summary>
    public static IEndpointRouteBuilder MapWorkbenchAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<AccountOptions>();
        if (options.DevSignIn)
        {
            DevSignIn.Map(app);
        }

        // The organization's own IdP when it has one; else the server's; else (Development) the dev sign-in page.
        // ?provider=server picks the server's IdP explicitly (refused at sign-in when the organization requires its own).
        app.MapGet(SignInPath, async (HttpContext http, SignInSettingsStore store, Application.Tenancy.ITenantContext tenants, string? returnUrl, string? provider) =>
            {
                var properties = new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) };
                if (provider != "server" && !tenants.IsHost && (await store.GetAsync(http.RequestAborted)).OrganizationIdpReady)
                {
                    return Results.Challenge(properties, [OrganizationSso.Scheme]);
                }

                return options.Oidc
                    ? Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.LocalRedirect($"{DevSignIn.Path}?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}");
            })
            .AllowAnonymous().ExcludeFromDescription();

        // Sensitive actions need a recent sign-in: send the user back to the IdP they used, which must ask for credentials again.
        app.MapGet(ReauthenticatePath, (HttpContext http, TimeProvider clock, string? returnUrl) =>
            {
                var target = SafeReturnUrl(returnUrl);
                var method = http.User.FindFirst(OrganizationSso.MethodClaim)?.Value;
                var properties = new AuthenticationProperties { RedirectUri = target };
                properties.Items[OrganizationSso.ReauthItem] = clock.GetUtcNow().ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                return method switch
                {
                    "sso" => Results.Challenge(properties, [OrganizationSso.Scheme]),
                    "oidc" when options.Oidc => Results.Challenge(properties, [OpenIdConnectDefaults.AuthenticationScheme]),
                    _ when options.DevSignIn => Results.LocalRedirect($"{DevSignIn.Path}?returnUrl={Uri.EscapeDataString(target)}&reauth=1"),
                    _ => Results.LocalRedirect($"{SignInPath}?returnUrl={Uri.EscapeDataString(target)}"),
                };
            })
            .ExcludeFromDescription();

        // A GET so the shared Blazor UI can navigate to it; the cookie is SameSite=Lax and signing out is harmless.
        app.MapGet(SignOutPath, async (HttpContext http, SecurityEventLog log) =>
            {
                if (ClaimsUser.EmailOf(http.User) is { } email)
                {
                    await log.RecordAsync(SecurityEventTypes.SignOut, email, actor: email);
                }

                // Also end the session at the IdP the user signed in with.
                var method = http.User.FindFirst(OrganizationSso.MethodClaim)?.Value;
                var properties = new AuthenticationProperties { RedirectUri = "/" };
                return method switch
                {
                    "sso" => Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme, OrganizationSso.Scheme]),
                    _ when options.Oidc && method != "dev" => Results.SignOut(properties,
                        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]),
                    _ => Results.SignOut(properties, [CookieAuthenticationDefaults.AuthenticationScheme]),
                };
            })
            .AllowAnonymous().ExcludeFromDescription();

        // Activity in a Blazor circuit doesn't touch the cookie, so session.js pings this (at most every few minutes
        // while the user is active) to slide the idle timeout. A 401 tells the page the session is over.
        app.MapGet(SessionPingPath, () => Results.NoContent()).ExcludeFromDescription();

        app.MapGet(AccessDeniedPath, (string? reason) => Results.Content(AccessDeniedPage(reason), "text/html; charset=utf-8"))
            .AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    private static string AccessDeniedPage(string? reason)
    {
        var message = reason switch
        {
            "signin" => "Your sign-in wasn't accepted. Your account may not be invited yet, may be deactivated, or the invitation may have expired. Ask a Workbench administrator.",
            "mfa" => "Your organization requires multi-factor authentication, and your identity provider reported that this sign-in didn't use it. Sign in again and complete the second step (authenticator app, security key or code). If you weren't asked for one, ask your administrator to turn on MFA for Workbench at your identity provider.",
            "org_idp" => "Your organization only allows sign-in through its own identity provider. Use the organization sign-in instead.",
            "reauth" => "Your identity provider didn't ask for your credentials again, so the re-authentication didn't count. Try again, or ask your administrator.",
            _ => "You don't have access to that page.",
        };
        return $$"""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Access denied · Workbench</title>
            <style>
              body { font-family: system-ui, sans-serif; background: #f5f6f8; margin: 0; padding: 48px 16px; color: #1f2937; }
              main { max-width: 480px; margin: 0 auto; background: #fff; border-radius: 8px; padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
              h1 { font-size: 1.4rem; margin: 0 0 8px; } a { color: #2563eb; }
            </style></head><body><main>
            <h1>Access denied</h1>
            <p>{{System.Net.WebUtility.HtmlEncode(message)}}</p>
            <p><a href="{{SignInPath}}">Try signing in again</a></p>
            </main></body></html>
            """;
    }

    /// <summary>Only same-site paths, so a crafted link can't bounce a fresh sign-in to another site.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}

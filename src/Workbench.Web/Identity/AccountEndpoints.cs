using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Workbench.Application.Security;
using Workbench.Contracts.Security;

namespace Workbench.Web.Identity;

public static class AccountEndpoints
{
    public const string SignInPath = "/account/signin";
    public const string SignOutPath = "/account/signout";
    public const string AccessDeniedPath = "/account/access-denied";
    public const string SessionPingPath = "/account/session/ping";

    /// <summary>Sign-in and sign-out endpoints, and the dev sign-in page when it's enabled.</summary>
    public static IEndpointRouteBuilder MapWorkbenchAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var options = app.ServiceProvider.GetRequiredService<AccountOptions>();
        if (options.DevSignIn)
        {
            DevSignIn.Map(app);
        }

        app.MapGet(SignInPath, (string? returnUrl) => options.Oidc
                ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturnUrl(returnUrl) }, [OpenIdConnectDefaults.AuthenticationScheme])
                : Results.LocalRedirect($"{DevSignIn.Path}?returnUrl={Uri.EscapeDataString(SafeReturnUrl(returnUrl))}"))
            .AllowAnonymous().ExcludeFromDescription();

        // A GET so the shared Blazor UI can navigate to it; the cookie is SameSite=Lax and signing out is harmless.
        app.MapGet(SignOutPath, async (HttpContext http, SecurityEventLog log) =>
            {
                if (ClaimsUser.EmailOf(http.User) is { } email)
                {
                    await log.RecordAsync(SecurityEventTypes.SignOut, email, actor: email);
                }

                return options.Oidc
                    ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                    : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme]);
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
        var message = reason == "signin"
            ? "Your sign-in wasn't accepted. Your account may not be invited yet, may be deactivated, or the invitation may have expired. Ask a Workbench administrator."
            : "You don't have access to that page.";
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

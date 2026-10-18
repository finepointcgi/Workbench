using System.Net;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using Workbench.Application;
using Workbench.Domain.Identity;

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
        app.MapGet(Path, async (HttpContext http, IAntiforgery antiforgery, IWorkbenchDbContextFactory dbFactory, string? returnUrl) =>
        {
            await using var db = await dbFactory.CreateAsync(http.RequestAborted);
            var users = await db.Set<AppUser>().AsNoTracking().OrderByDescending(u => u.IsAdmin).ThenBy(u => u.Email).ToListAsync(http.RequestAborted);
            var tokens = antiforgery.GetAndStoreTokens(http);
            return Results.Content(RenderPage(users, tokens, AccountEndpoints.SafeReturnUrl(returnUrl)), "text/html; charset=utf-8");
        }).AllowAnonymous().ExcludeFromDescription();

        app.MapPost(Path, async (HttpContext http, IWorkbenchDbContextFactory dbFactory, [Microsoft.AspNetCore.Mvc.FromForm] string email, [Microsoft.AspNetCore.Mvc.FromForm] string? returnUrl) =>
        {
            email = (email ?? string.Empty).Trim().ToLowerInvariant();
            await using var db = await dbFactory.CreateAsync(http.RequestAborted);
            var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == email, http.RequestAborted);
            if (user is null)
            {
                return Results.BadRequest("Unknown user.");
            }

            var identity = new ClaimsIdentity(
                [new Claim("sub", user.Email), new Claim("email", user.Email), new Claim("name", user.DisplayName)],
                "dev", "name", "role");
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
            return Results.LocalRedirect(AccountEndpoints.SafeReturnUrl(returnUrl));
        }).AllowAnonymous().ExcludeFromDescription();
    }

    private static string RenderPage(IEnumerable<AppUser> users, AntiforgeryTokenSet tokens, string returnUrl)
    {
        static string E(string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var html = new StringBuilder("""
            <!DOCTYPE html>
            <html lang="en"><head><meta charset="utf-8" /><meta name="viewport" content="width=device-width, initial-scale=1" />
            <title>Dev sign-in · Workbench</title>
            <style>
              body { font-family: system-ui, sans-serif; background: #f5f6f8; margin: 0; padding: 48px 16px; color: #1f2937; }
              main { max-width: 420px; margin: 0 auto; background: #fff; border-radius: 8px; padding: 24px; box-shadow: 0 1px 3px rgba(0,0,0,.12); }
              h1 { font-size: 1.4rem; margin: 0 0 4px; } p { color: #6b7280; margin: 0 0 20px; }
              button { display: block; width: 100%; text-align: left; margin: 8px 0; padding: 12px 14px; border: 1px solid #d1d5db; border-radius: 6px; background: #fff; cursor: pointer; font: inherit; }
              button:hover { border-color: #2563eb; background: #eff6ff; } small { display: block; color: #6b7280; }
              .admin { font-size: .75rem; color: #2563eb; margin-left: 6px; }
            </style></head><body><main>
            <h1>Sign in (development)</h1>
            <p>No identity provider is configured, so pick a seeded user. This page doesn't exist outside Development.</p>
            """);
        foreach (var user in users)
        {
            html.Append($"""
                <form method="post" action="{Path}">
                  <input type="hidden" name="{E(tokens.FormFieldName)}" value="{E(tokens.RequestToken)}" />
                  <input type="hidden" name="returnUrl" value="{E(returnUrl)}" />
                  <button type="submit" name="email" value="{E(user.Email)}">{E(user.DisplayName)}{(user.IsAdmin ? "<span class=\"admin\">admin</span>" : "")}<small>{E(user.Email)}</small></button>
                </form>
                """);
        }

        html.Append("</main></body></html>");
        return html.ToString();
    }
}

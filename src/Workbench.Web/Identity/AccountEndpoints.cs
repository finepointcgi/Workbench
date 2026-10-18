using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

namespace Workbench.Web.Identity;

public static class AccountEndpoints
{
    public const string SignInPath = "/account/signin";
    public const string SignOutPath = "/account/signout";

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
        app.MapGet(SignOutPath, () => options.Oidc
                ? Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
                    [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme])
                : Results.SignOut(new AuthenticationProperties { RedirectUri = "/" }, [CookieAuthenticationDefaults.AuthenticationScheme]))
            .AllowAnonymous().ExcludeFromDescription();
        return app;
    }

    /// <summary>Only same-site paths, so a crafted link can't bounce a fresh sign-in to another site.</summary>
    internal static string SafeReturnUrl(string? returnUrl) =>
        !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") && !returnUrl.StartsWith("/\\")
            ? returnUrl
            : "/";
}

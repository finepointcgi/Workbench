using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Workbench.Application.Identity;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;

namespace Workbench.Web.Identity;

public static class AuthenticationSetup
{
    /// <summary>Picks the bearer handler for requests with a bearer token and the cookie handler for everything else.</summary>
    public const string SelectorScheme = "Workbench";

    /// <summary>
    /// Production: OpenID Connect sign-in (config <see cref="OidcSettings.SectionName"/>) into an auth cookie, plus
    /// JWT bearer tokens from the same authority for the desktop app. Development without an authority: the cookie
    /// only, issued by the dev sign-in page. Every endpoint requires a signed-in user unless it opts out.
    /// </summary>
    public static WebApplicationBuilder AddWorkbenchAuthentication(this WebApplicationBuilder builder)
    {
        var services = builder.Services;
        var oidc = builder.Configuration.GetSection(OidcSettings.SectionName).Get<OidcSettings>() ?? new OidcSettings();
        var devSignIn = DevSignIn.IsEnabled(builder.Environment, oidc);
        if (!devSignIn && !oidc.IsConfigured)
        {
            throw new InvalidOperationException(
                $"Set {OidcSettings.SectionName}:Authority and :ClientId. The dev sign-in page is only available in Development.");
        }

        var admins = builder.Configuration.GetSection("Authentication:Admins").Get<string[]>() ?? [];
        var bootstrapAdmins = admins.Select(a => a.Trim().ToLowerInvariant()).ToHashSet();

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = SelectorScheme;
                o.DefaultChallengeScheme = SelectorScheme;
            })
            .AddPolicyScheme(SelectorScheme, null, o =>
                o.ForwardDefaultSelector = context => oidc.IsConfigured && HasBearerToken(context.Request)
                    ? JwtBearerDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = ".Workbench.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                o.ExpireTimeSpan = TimeSpan.FromHours(8);
                o.SlidingExpiration = true;
                // Pages redirect to the sign-in endpoint (which starts OIDC, or shows the dev page); API and hub
                // callers get a bare 401 instead, without contacting the identity provider.
                o.LoginPath = devSignIn ? DevSignIn.Path : AccountEndpoints.SignInPath;
                o.LogoutPath = AccountEndpoints.SignOutPath;

                o.Events.OnRedirectToLogin = context => RespondOrRedirect(context, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = context => RespondOrRedirect(context, StatusCodes.Status403Forbidden);
                o.Events.OnSigningIn = async context =>
                {
                    var email = ClaimsUser.EmailOf(context.Principal) ?? throw new InvalidOperationException("The sign-in has no email claim.");
                    var provisioner = context.HttpContext.RequestServices.GetRequiredService<UserProvisioner>();
                    await provisioner.EnsureUserAsync(email, ClaimsUser.NameOf(context.Principal), bootstrapAdmins.Contains(email), context.HttpContext.RequestAborted);
                };
            });

        if (oidc.IsConfigured)
        {
            auth.AddOpenIdConnect(o =>
            {
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                o.ResponseType = "code";
                o.UsePkce = true;
                o.MapInboundClaims = false;
                o.GetClaimsFromUserInfoEndpoint = true;
                o.SaveTokens = false;
                o.TokenValidationParameters.NameClaimType = "name";
                o.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email" }.Concat(oidc.Scopes).Distinct())
                {
                    o.Scope.Add(scope);
                }

                o.Events.OnRedirectToIdentityProvider = context =>
                {
                    // API and hub callers can't follow a redirect to the sign-in page.
                    if (IsApiRequest(context.Request))
                    {
                        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                        context.HandleResponse();
                    }

                    return Task.CompletedTask;
                };
                o.Events.OnTokenValidated = context =>
                {
                    if (ClaimsUser.EmailOf(context.Principal) is null)
                    {
                        context.Fail("The identity provider didn't send an email, preferred_username or upn claim.");
                    }

                    return Task.CompletedTask;
                };
            });

            auth.AddJwtBearer(o =>
            {
                o.Authority = oidc.Authority;
                o.Audience = string.IsNullOrWhiteSpace(oidc.Audience) ? oidc.ClientId : oidc.Audience;
                o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                o.MapInboundClaims = false;
                o.TokenValidationParameters.NameClaimType = "name";
                o.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        // Browsers' WebSocket API can't set headers, so SignalR sends the token in the query string.
                        var token = context.Request.Query["access_token"];
                        if (!string.IsNullOrEmpty(token) && context.Request.Path.StartsWithSegments("/hubs"))
                        {
                            context.Token = token;
                        }

                        return Task.CompletedTask;
                    },
                    OnTokenValidated = async context =>
                    {
                        if (ClaimsUser.EmailOf(context.Principal) is not { } email)
                        {
                            context.Fail("The token has no email, preferred_username or upn claim.");
                            return;
                        }

                        var provisioner = context.HttpContext.RequestServices.GetRequiredService<UserProvisioner>();
                        await provisioner.EnsureUserAsync(email, ClaimsUser.NameOf(context.Principal), bootstrapAdmins.Contains(email), context.HttpContext.RequestAborted);
                    },
                };
            });
        }

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentPrincipal>();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, PrincipalCircuitHandler>());
        services.AddScoped<ISignOutService, WebSignOutService>();
        services.AddSingleton(new AccountOptions(devSignIn, oidc.IsConfigured));
        return builder;
    }

    internal static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hubs");

    private static bool HasBearerToken(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || (request.Path.StartsWithSegments("/hubs") && request.Query.ContainsKey("access_token"));

    private static Task RespondOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int apiStatus)
    {
        if (IsApiRequest(context.Request))
        {
            context.Response.StatusCode = apiStatus;
        }
        else
        {
            context.Response.Redirect(context.RedirectUri);
        }

        return Task.CompletedTask;
    }
}

internal sealed record AccountOptions(bool DevSignIn, bool Oidc);

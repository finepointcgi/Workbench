using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.AspNetCore.Components.Authorization;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Security;
using Workbench.Web.Security;
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
        var devSignIn = DevSignIn.IsEnabled(builder.Environment, oidc, builder.Configuration);
        if (!devSignIn && !oidc.IsConfigured)
        {
            throw new InvalidOperationException(
                $"Set {OidcSettings.SectionName}:Authority and :ClientId. The dev sign-in page is only available in Development and the public demo (Demo:Enabled).");
        }

        var tenancy = builder.Configuration.GetSection(TenancyOptions.SectionName).Get<TenancyOptions>() ?? new TenancyOptions();

        // MultiTenant: a tenant's first admin comes from provisioning; a global admin list would be an admin of every tenant.
        var admins = tenancy.IsMultiTenant ? [] : builder.Configuration.GetSection("Authentication:Admins").Get<string[]>() ?? [];
        var signInPolicy = new SignInPolicyOptions
        {
            // Outside Development only invited people (or SCIM-provisioned and bootstrap admins) may sign in.
            RequireInvite = builder.Configuration.GetValue("Authentication:RequireInvite", !builder.Environment.IsDevelopment()),
            BootstrapAdmins = admins.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase),
        };
        var session = builder.Configuration.GetSection(SessionSettings.SectionName).Get<SessionSettings>() ?? new SessionSettings();
        // SCIM has one token for the whole server, so it stays SingleTenant-only for now.
        var scimEnabled = !tenancy.IsMultiTenant && (builder.Configuration.GetSection(ScimSettings.SectionName).Get<ScimSettings>()?.Enabled ?? true);

        var auth = services.AddAuthentication(o =>
            {
                o.DefaultScheme = SelectorScheme;
                o.DefaultChallengeScheme = SelectorScheme;
            })
            .AddPolicyScheme(SelectorScheme, null, o =>
                o.ForwardDefaultSelector = context =>
                    context.Request.Path.StartsWithSegments(ScimEndpoints.BasePath) ? ScimAuthenticationHandler.SchemeName
                    : Supplier.SupplierPortalAuthentication.IsPortalPath(context.Request.Path) ? Supplier.SupplierPortalAuthentication.Scheme
                    : oidc.IsConfigured && HasBearerToken(context.Request) ? JwtBearerDefaults.AuthenticationScheme
                    : CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(o =>
            {
                o.Cookie.Name = ".Workbench.Auth";
                o.Cookie.HttpOnly = true;
                o.Cookie.SameSite = SameSiteMode.Lax;
                o.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;

                // Idle timeout (A.8.5): the cookie slides while the user is active and expires after this much inactivity.
                // The absolute lifetime and deactivation are checked in OnValidatePrincipal.
                o.ExpireTimeSpan = session.IdleTimeout;
                o.SlidingExpiration = true;
                // Pages redirect to the sign-in endpoint (which starts OIDC, or shows the dev page); API and hub
                // callers get a bare 401 instead, without contacting the identity provider.
                o.LoginPath = devSignIn ? DevSignIn.Path : AccountEndpoints.SignInPath;
                o.LogoutPath = AccountEndpoints.SignOutPath;
                o.AccessDeniedPath = AccountEndpoints.AccessDeniedPath;

                o.Events.OnRedirectToLogin = context => RespondOrRedirect(context, StatusCodes.Status401Unauthorized);
                o.Events.OnRedirectToAccessDenied = context => RespondOrRedirect(context, StatusCodes.Status403Forbidden);
                o.Events.OnSigningIn = context =>
                {
                    // The sign-in gate (OIDC OnTokenValidated or the dev sign-in) has already admitted the user.
                    if (context.Principal?.Identity is ClaimsIdentity identity && !identity.HasClaim(c => c.Type == SessionValidation.SignedInAtClaim))
                    {
                        identity.AddClaim(SessionValidation.SignedInAt(context.HttpContext.RequestServices.GetRequiredService<TimeProvider>().GetUtcNow()));
                    }

                    // MultiTenant: the cookie is only good for the tenant (or the host site) it was issued for.
                    if (tenancy.IsMultiTenant && context.Principal?.Identity is ClaimsIdentity tenantIdentity)
                    {
                        foreach (var old in tenantIdentity.FindAll(SessionValidation.TenantClaim).ToList())
                        {
                            tenantIdentity.RemoveClaim(old);
                        }

                        tenantIdentity.AddClaim(new Claim(SessionValidation.TenantClaim, SessionValidation.CurrentTenantClaimValue()));
                    }

                    return Task.CompletedTask;
                };
                o.Events.OnValidatePrincipal = async context =>
                {
                    await SessionValidation.ValidateCookieAsync(context);
                    await OrganizationSessionPolicy.ValidateCookieAsync(context);
                };
            });

        if (scimEnabled)
        {
            auth.AddScheme<AuthenticationSchemeOptions, ScimAuthenticationHandler>(ScimAuthenticationHandler.SchemeName, null);
        }

        // The organization's own IdP (per tenant, from Admin › Users and security › Sign-in), whatever the server config.
        auth.AddOrganizationSso(services);

        if (oidc.IsConfigured)
        {
            auth.AddOpenIdConnect(o =>
            {
                o.Authority = oidc.Authority;
                o.ClientId = oidc.ClientId;
                o.ClientSecret = oidc.ClientSecret;
                o.RequireHttpsMetadata = oidc.RequireHttpsMetadata;
                o.GetClaimsFromUserInfoEndpoint = true;
                OrganizationSso.ConfigureProtocol(o, "oidc");
                o.Scope.Clear();
                foreach (var scope in new[] { "openid", "profile", "email" }.Concat(oidc.Scopes).Distinct())
                {
                    o.Scope.Add(scope);
                }
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

                        // Every API call: deactivated users and revoked sessions are refused (A.8.5). Only refusals are logged.
                        var services = context.HttpContext.RequestServices;
                        var decision = await services.GetRequiredService<SignInGate>()
                            .EvaluateAsync(email, ClaimsUser.NameOf(context.Principal), "bearer", interactive: false, context.HttpContext.RequestAborted,
                                OrganizationSso.ReadClaims(context.Principal!, "groups", groupsCount: false));
                        if (!decision.Allowed)
                        {
                            context.Fail(decision.Reason ?? "Refused.");
                            return;
                        }

                        var reason = await SessionValidation.CheckAsync(context.Principal, services.GetRequiredService<UserStatusCache>(), session,
                            services.GetRequiredService<TimeProvider>(), enforceLifetime: false, context.HttpContext.RequestAborted);
                        if (reason is not null)
                        {
                            await services.GetRequiredService<SecurityEventLog>().RecordAsync(SecurityEventTypes.SessionRejected, email, SecurityOutcomes.Denied, $"bearer: {reason}", actor: email);
                            context.Fail(reason);
                        }
                    },
                };
            });
        }

        services.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
        services.AddCascadingAuthenticationState();
        services.AddScoped<CircuitActivity>();
        services.AddScoped<AuthenticationStateProvider, RevalidatingAuthenticationStateProvider>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, SessionCircuitHandler>());
        services.AddSingleton(session);
        services.AddSingleton(signInPolicy);
        services.AddScoped<ISecurityEventContext, HttpSecurityEventContext>();

        services.AddHttpContextAccessor();
        services.AddScoped<CurrentPrincipal>();
        services.AddScoped<ICurrentUser, ClaimsCurrentUser>();
        services.TryAddEnumerable(ServiceDescriptor.Scoped<CircuitHandler, PrincipalCircuitHandler>());
        services.AddScoped<ISignOutService, WebSignOutService>();
        services.AddSingleton(new AccountOptions(devSignIn, oidc.IsConfigured));
        services.AddScoped<ISignInContext, WebSignInContext>();
        services.AddSingleton(new SignInServerInfo
        {
            ServerOidcConfigured = oidc.IsConfigured,
            IdleTimeoutMinutes = (int)session.IdleTimeout.TotalMinutes,
            AbsoluteLifetimeHours = (int)session.AbsoluteLifetime.TotalHours,
        });
        return builder;
    }

    internal static bool IsApiRequest(HttpRequest request) =>
        request.Path.StartsWithSegments("/api") || request.Path.StartsWithSegments("/hubs")
        || request.Path.StartsWithSegments(ScimEndpoints.BasePath) || request.Path.StartsWithSegments("/health");

    private static bool HasBearerToken(HttpRequest request) =>
        request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        || (request.Path.StartsWithSegments("/hubs") && request.Query.ContainsKey("access_token"));

    /// <summary>
    /// Whether the browser is loading a page (a top-level navigation, a frame, or Blazor's enhanced navigation), as
    /// opposed to an image, script or other subresource. Only page loads are redirected to sign-in: a subresource that
    /// lands on the sign-in page renders it in parallel with the real one, minting a second antiforgery cookie that
    /// replaces the first and makes the sign-in form fail ("cookie token and request token do not match").
    /// </summary>
    internal static bool IsPageNavigation(HttpRequest request)
    {
        if (request.Headers.ContainsKey("blazor-enhanced-nav"))
        {
            return true;
        }

        // Browsers say what they're loading (Fetch Metadata). Clients that don't are treated as page loads, as before.
        var dest = request.Headers["Sec-Fetch-Dest"].ToString();
        return dest.Length == 0 || dest is "document" or "iframe" or "frame";
    }

    private static Task RespondOrRedirect(RedirectContext<CookieAuthenticationOptions> context, int apiStatus)
    {
        if (IsApiRequest(context.Request) || !IsPageNavigation(context.Request))
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

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;

namespace Workbench.Web.Identity;

/// <summary>
/// The organization's own identity provider (Admin › Users and security › Sign-in): one OpenID Connect scheme whose
/// options are built per tenant from its <see cref="SignInSettings"/>, so in MultiTenant mode every tenant signs in with
/// its own Entra ID, Okta or other IdP at <c>https://{tenant}.{base}/signin-sso</c>. The server-wide scheme
/// (<c>Authentication:Oidc</c>) stays as it was: the bootstrap and self-hosted sign-in.
/// </summary>
public static class OrganizationSso
{
    public const string Scheme = "OrganizationSso";

    /// <summary>When the user last authenticated at the IdP (Unix ms): the re-authentication clock.</summary>
    public const string AuthTimeClaim = "wb_auth_time";

    /// <summary><c>true</c> when the IdP reported MFA that this organization accepts.</summary>
    public const string MfaClaim = "wb_mfa";

    /// <summary><c>sso</c>, <c>oidc</c> or <c>dev</c>.</summary>
    public const string MethodClaim = "wb_method";

    /// <summary>Authentication property set on a re-authentication challenge: when it was asked for (Unix ms).</summary>
    internal const string ReauthItem = "wb_reauth";

    private const string DenialItem = "wb_denial";

    internal static void AddOrganizationSso(this AuthenticationBuilder auth, IServiceCollection services)
    {
        auth.AddOpenIdConnect(Scheme, "Organization sign-in", o =>
        {
            o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            o.CallbackPath = SignInSettingsDto.RedirectPath;
            o.SignedOutCallbackPath = SignInSettingsDto.SignedOutCallbackPath;
            o.RemoteSignOutPath = "/signout-sso";

            // Until a tenant's settings fill them in: valid, inert options (nothing is fetched, every sign-in fails).
            o.ClientId = "unconfigured";
            o.Configuration = new OpenIdConnectConfiguration { Issuer = "urn:workbench:unconfigured" };
            ConfigureProtocol(o, "sso");
        });
        services.AddOptions<OpenIdConnectOptions>(Scheme).Configure<OrganizationSsoBackchannel>((o, b) =>
        {
            if (b.Handler is not null)
            {
                o.BackchannelHttpHandler = b.Handler;
            }
        });
        services.AddSingleton<OrganizationSsoBackchannel>();
        services.AddSingleton<IOptionsMonitor<OpenIdConnectOptions>, OrganizationSsoOptionsMonitor>();
    }

    /// <summary>The settings both OIDC schemes share: code flow with PKCE, our claim handling and the sign-in rules.</summary>
    internal static void ConfigureProtocol(OpenIdConnectOptions o, string method)
    {
        o.ResponseType = "code";
        o.UsePkce = true;
        o.MapInboundClaims = false;
        o.SaveTokens = false;
        o.TokenValidationParameters.NameClaimType = "name";
        o.Events.OnRedirectToIdentityProvider = context => OnRedirectToIdentityProviderAsync(context, method);
        o.Events.OnTokenValidated = context => OnTokenValidatedAsync(context, method);
        o.Events.OnRemoteFailure = context =>
        {
            // A refused or failed sign-in shows a plain page instead of an exception.
            var reason = context.HttpContext.Items[DenialItem] as string ?? SignInDenials.General;
            context.Response.Redirect($"{AccountEndpoints.AccessDeniedPath}?reason={Uri.EscapeDataString(reason)}");
            context.HandleResponse();
            return Task.CompletedTask;
        };
    }

    private static async Task OnRedirectToIdentityProviderAsync(RedirectContext context, string method)
    {
        // API and hub callers can't follow a redirect to the sign-in page.
        if (AuthenticationSetup.IsApiRequest(context.Request))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.HandleResponse();
            return;
        }

        if (context.Properties.Items.ContainsKey(ReauthItem))
        {
            // Re-authentication: the IdP must ask for credentials again, not reuse its own session.
            context.ProtocolMessage.Prompt = "login";
            context.ProtocolMessage.MaxAge = "0";
            if (ClaimsUser.EmailOf(context.HttpContext.User) is { } email)
            {
                context.ProtocolMessage.LoginHint = email;
            }
        }

        if (TenantScope.Current is null && context.HttpContext.RequestServices.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            return;
        }

        var policy = await context.HttpContext.RequestServices.GetRequiredService<SignInSettingsStore>().GetAsync(context.HttpContext.RequestAborted);
        var requested = SignInRules.SplitList(policy.Settings.RequestedAcrValues);
        if (requested.Count == 0 || (method == "oidc" && policy.OrganizationIdpReady))
        {
            return;
        }

        if (policy.Settings.ProviderKind == SsoProviderKind.EntraId && method == "sso")
        {
            // Entra ID asks for Conditional Access authentication contexts (c1..c99) through the claims parameter.
            context.ProtocolMessage.SetParameter("claims", JsonSerializer.Serialize(new
            {
                id_token = new { acrs = new { essential = true, values = requested } },
            }));
        }
        else
        {
            context.ProtocolMessage.AcrValues = string.Join(' ', requested);
        }
    }

    private static async Task OnTokenValidatedAsync(TokenValidatedContext context, string method)
    {
        var services = context.HttpContext.RequestServices;
        var log = services.GetRequiredService<SecurityEventLog>();
        if (ClaimsUser.EmailOf(context.Principal) is not { } email)
        {
            await log.RecordAsync(SecurityEventTypes.SignInFailure, null, SecurityOutcomes.Failure, $"{method}: no email claim", actor: "anonymous");
            context.Fail("The identity provider didn't send an email, preferred_username or upn claim.");
            return;
        }

        var tenancy = services.GetRequiredService<TenancyOptions>();
        var hostSite = tenancy.IsMultiTenant && TenantScope.Current is null;
        var policy = hostSite ? SignInPolicySnapshot.Default : await services.GetRequiredService<SignInSettingsStore>().GetAsync(context.HttpContext.RequestAborted);
        var clock = services.GetRequiredService<TimeProvider>();
        var now = clock.GetUtcNow();
        var authTime = AuthTimeOf(context.Principal) ?? now;

        if (context.Properties?.Items.TryGetValue(ReauthItem, out var requestedAt) == true
            && long.TryParse(requestedAt, NumberStyles.Integer, CultureInfo.InvariantCulture, out var requestedMs)
            && authTime < DateTimeOffset.FromUnixTimeMilliseconds(requestedMs).AddMinutes(-2))
        {
            // prompt=login was ignored: the IdP vouched for an old sign-in.
            await log.RecordAsync(SecurityEventTypes.SignInFailure, email, SecurityOutcomes.Failure, $"{method}: re-authentication not performed by the identity provider", actor: email);
            context.HttpContext.Items[DenialItem] = "reauth";
            context.Fail("The identity provider didn't ask for your credentials again.");
            return;
        }

        // Groups decide roles only when they come from the organization's own IdP (in SingleTenant, the server's IdP is it).
        var claims = ReadClaims(context.Principal!, policy.Settings.GroupsClaim, groupsCount: method == "sso" || !tenancy.IsMultiTenant);
        var decision = await services.GetRequiredService<SignInGate>()
            .EvaluateAsync(email, ClaimsUser.NameOf(context.Principal), method, interactive: true, context.HttpContext.RequestAborted, claims);
        if (!decision.Allowed)
        {
            context.HttpContext.Items[DenialItem] = decision.Code ?? SignInDenials.General;
            context.Fail(decision.Reason ?? "Sign-in refused.");
            return;
        }

        if (context.Principal?.Identity is ClaimsIdentity identity)
        {
            // Keep the cookie small and free of IdP internals: groups were used above and aren't needed again.
            foreach (var claim in identity.FindAll(c => c.Type == policy.Settings.GroupsClaim || c.Type is "groups" or "_claim_names" or "_claim_sources" or "wids").ToList())
            {
                identity.RemoveClaim(claim);
            }

            identity.AddClaim(new Claim(AuthTimeClaim, authTime.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64));
            identity.AddClaim(new Claim(MfaClaim, SignInRules.MfaSatisfied(policy.Settings, claims) ? "true" : "false"));
            identity.AddClaim(new Claim(MethodClaim, method));
        }

        if (context.Properties?.Items.ContainsKey(ReauthItem) == true)
        {
            await log.RecordAsync(SecurityEventTypes.Reauthenticated, email, detail: method, actor: email);
        }
    }

    /// <summary>The amr, acr/acrs and (when they count) groups claims of an IdP token.</summary>
    internal static SignInClaims ReadClaims(ClaimsPrincipal principal, string groupsClaim, bool groupsCount)
    {
        static IReadOnlyCollection<string> Values(ClaimsPrincipal p, params string[] types) =>
            types.SelectMany(t => p.FindAll(t)).SelectMany(c => SplitJsonArray(c.Value)).Where(v => v.Length > 0).Distinct(StringComparer.Ordinal).ToList();

        var groups = groupsCount ? Values(principal, string.IsNullOrWhiteSpace(groupsClaim) ? "groups" : groupsClaim) : null;
        return new SignInClaims(Values(principal, "amr"), Values(principal, "acr", "acrs"), groups);
    }

    /// <summary>A claim value as one or more strings: JSON arrays (some handlers keep them whole) are split.</summary>
    private static IEnumerable<string> SplitJsonArray(string value)
    {
        value = value.Trim();
        if (!value.StartsWith('['))
        {
            return [value];
        }

        try
        {
            return JsonSerializer.Deserialize<string[]>(value) ?? [];
        }
        catch (JsonException)
        {
            return [value];
        }
    }

    private static DateTimeOffset? AuthTimeOf(ClaimsPrincipal? principal) =>
        long.TryParse(principal?.FindFirst("auth_time")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
            ? DateTimeOffset.FromUnixTimeSeconds(seconds)
            : null;
}

/// <summary>The HTTP handler for discovery, keys and token requests to tenant IdPs. Null (the default) in production; tests set a mock IdP.</summary>
public sealed class OrganizationSsoBackchannel
{
    public HttpMessageHandler? Handler { get; set; }
}

/// <summary>
/// <see cref="OpenIdConnectOptions"/> for <see cref="OrganizationSso.Scheme"/> per tenant, built from the tenant's sign-in
/// settings and cached until they change; every other scheme is served by the normal options monitor. The handler reads its
/// options on every request, so a tenant's new settings apply within <see cref="SignInSettingsStore.Ttl"/>.
/// </summary>
internal sealed class OrganizationSsoOptionsMonitor : IOptionsMonitor<OpenIdConnectOptions>
{
    private readonly IOptionsFactory<OpenIdConnectOptions> _factory;
    private readonly OptionsMonitor<OpenIdConnectOptions> _inner;
    private readonly IServiceProvider _services;
    private readonly ILogger<OrganizationSsoOptionsMonitor> _logger;
    private readonly ConcurrentDictionary<string, (SignInPolicySnapshot Snapshot, OpenIdConnectOptions Options)> _byTenant = new();
    private readonly Lazy<OpenIdConnectOptions> _unconfigured;

    public OrganizationSsoOptionsMonitor(IOptionsFactory<OpenIdConnectOptions> factory, IEnumerable<IOptionsChangeTokenSource<OpenIdConnectOptions>> sources,
        IOptionsMonitorCache<OpenIdConnectOptions> cache, IServiceProvider services, ILogger<OrganizationSsoOptionsMonitor> logger)
    {
        _factory = factory;
        _inner = new OptionsMonitor<OpenIdConnectOptions>(factory, sources, cache);
        _services = services;
        _logger = logger;
        _unconfigured = new Lazy<OpenIdConnectOptions>(() => factory.Create(OrganizationSso.Scheme));
    }

    public OpenIdConnectOptions CurrentValue => _inner.CurrentValue;

    public OpenIdConnectOptions Get(string? name) => name == OrganizationSso.Scheme ? ForCurrentTenant() : _inner.Get(name);

    public IDisposable? OnChange(Action<OpenIdConnectOptions, string?> listener) => _inner.OnChange(listener);

    private OpenIdConnectOptions ForCurrentTenant()
    {
        if (TenantScope.Current is null && _services.GetRequiredService<TenancyOptions>().IsMultiTenant)
        {
            return _unconfigured.Value; // the host admin site has no organization IdP
        }

        SignInPolicySnapshot snapshot;
        try
        {
            var store = _services.GetRequiredService<SignInSettingsStore>();
            // Usually cached; otherwise one small read per tenant every few seconds, on the request that needs it.
            snapshot = store.Peek() ?? store.GetAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogWarning(ex, "Couldn't read the organization's sign-in settings");
            return _unconfigured.Value;
        }

        if (!snapshot.OrganizationIdpReady)
        {
            return _unconfigured.Value;
        }

        var key = TenantScope.Key;
        if (_byTenant.TryGetValue(key, out var cached) && ReferenceEquals(cached.Snapshot, snapshot))
        {
            return cached.Options;
        }

        var options = Build(snapshot.Settings, snapshot.GetClientSecret());
        _byTenant[key] = (snapshot, options);
        return options;
    }

    private OpenIdConnectOptions Build(SignInSettings settings, string? clientSecret)
    {
        var o = _factory.Create(OrganizationSso.Scheme);
        var authority = settings.Authority!.TrimEnd('/');
        o.Authority = authority;
        o.ClientId = settings.ClientId;
        o.ClientSecret = clientSecret;
        o.RequireHttpsMetadata = authority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
        o.MetadataAddress = $"{authority}/.well-known/openid-configuration";
        o.Configuration = null;
        o.ConfigurationManager = new ConfigurationManager<OpenIdConnectConfiguration>(o.MetadataAddress, new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever(o.Backchannel) { RequireHttps = o.RequireHttpsMetadata });
        o.TokenValidationParameters = o.TokenValidationParameters.Clone();
        o.TokenValidationParameters.ValidAudience = settings.ClientId;
        o.TokenValidationParameters.NameClaimType = "name";

        // Okta's org authorization server and most IdPs put everything in the ID token; the userinfo call adds nothing.
        o.GetClaimsFromUserInfoEndpoint = false;
        o.Scope.Clear();
        foreach (var scope in new[] { "openid", "profile", "email" }.Concat(SignInRules.SplitList(settings.Scopes)).Distinct(StringComparer.Ordinal))
        {
            o.Scope.Add(scope);
        }

        return o;
    }
}

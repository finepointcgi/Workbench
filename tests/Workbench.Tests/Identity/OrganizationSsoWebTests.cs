using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application;
using Workbench.Application.Identity;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;
using Workbench.Tests.Tenancy;
using Workbench.Web.Identity;

namespace Workbench.Tests.Identity;

/// <summary>
/// Single sign-on end to end through the real web host and a local mock OpenID Connect provider: the browser's redirects,
/// the code exchange with PKCE, MFA enforcement, re-authentication for sensitive actions, just-in-time provisioning and
/// per-tenant identity providers in MultiTenant mode.
/// </summary>
public sealed class OrganizationSsoWebTests
{
    private const string Admin = "admin@acme.example";
    private const string Secret = "acme-client-secret";

    [Fact]
    public async Task People_sign_in_through_the_organization_idp_with_mfa()
    {
        await using var web = await ProductionAsync(s => s.MfaRequirement = MfaRequirementLevel.Everyone);
        using var browser = web.Browser();

        var page = await browser.GetStringAsync("/account/signin?returnUrl=/sheets");
        var challenge = await browser.GetAsync("/account/signin?provider=sso&returnUrl=%2Fsheets");
        var (redirectUri, form) = web.Idp.Authorize(challenge.Headers.Location!, new MockOidcProvider.User(Admin, "Ada Admin", ["pwd", "mfa"]));
        using var callback = await browser.PostAsync(redirectUri, new FormUrlEncodedContent(form));
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Contains("Sign in with Acme Okta", page); // next to the email and password form
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.Equal("https://localhost/signin-sso", redirectUri);
        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/sheets", callback.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.Contains(Admin, await me.Content.ReadAsStringAsync());
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.SignInSuccess && e.Actor == Admin && e.Detail!.StartsWith("sso; amr=pwd,mfa"));
    }

    [Fact]
    public async Task A_sign_in_without_mfa_is_refused_with_a_clear_message()
    {
        await using var web = await ProductionAsync(s => s.MfaRequirement = MfaRequirementLevel.Everyone);
        using var browser = web.Browser();

        using var callback = await web.SignInAsync(browser, new MockOidcProvider.User(Admin, "Ada Admin", ["pwd"]));
        var page = await browser.GetStringAsync(callback.Headers.Location!);
        using var me = await browser.GetAsync("/api/v1/users/me/context");

        Assert.Equal(HttpStatusCode.Redirect, callback.StatusCode);
        Assert.Equal("/account/access-denied?reason=mfa", callback.Headers.Location!.OriginalString);
        Assert.Contains("requires multi-factor authentication", page);
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.MfaRequired && e.Target == Admin);
    }

    [Fact]
    public async Task Sensitive_actions_ask_for_a_fresh_sign_in_at_the_idp()
    {
        await using var web = await ProductionAsync(s => s.ReauthenticationMinutes = 5);
        using var browser = web.Browser();
        var employee = Guid.NewGuid();

        // Signed in, but the IdP says the person authenticated 30 minutes ago (its own session).
        await web.SignInAsync(browser, new MockOidcProvider.User(Admin, "Ada", ["pwd"], AuthTime: DateTimeOffset.UtcNow.AddMinutes(-30)));
        using var stale = await browser.PostAsync($"/api/v1/payroll/profiles/{employee}/reveal", null);
        var status = await browser.GetFromJsonAsync<ReauthenticationStatusDto>("/api/v1/users/me/reauthentication");

        Assert.Equal(HttpStatusCode.Forbidden, stale.StatusCode);
        Assert.Contains("sign in again to show pay rates", await stale.Content.ReadAsStringAsync());
        Assert.False(status!.IsRecent);

        // An IdP that ignores prompt=login doesn't count.
        using var ignored = await web.SignInAsync(browser, new MockOidcProvider.User(Admin, "Ada", ["pwd"], AuthTime: DateTimeOffset.UtcNow.AddMinutes(-30)),
            "/account/reauthenticate?returnUrl=/setup");
        Assert.Equal("/account/access-denied?reason=reauth", ignored.Headers.Location!.OriginalString);

        using var again = await web.SignInAsync(browser, new MockOidcProvider.User(Admin, "Ada", ["pwd"]), "/account/reauthenticate?returnUrl=/setup");
        using var fresh = await browser.PostAsync($"/api/v1/payroll/profiles/{employee}/reveal", null);

        Assert.Equal("login", web.Idp.LastAuthorizeQuery["prompt"]);
        Assert.Equal("0", web.Idp.LastAuthorizeQuery["max_age"]);
        Assert.Equal(Admin, web.Idp.LastAuthorizeQuery["login_hint"]);
        Assert.Equal("/setup", again.Headers.Location!.OriginalString);
        Assert.NotEqual(HttpStatusCode.Forbidden, fresh.StatusCode);
        Assert.Contains(await web.EventsAsync(), e => e.EventType == SecurityEventTypes.Reauthenticated && e.Actor == Admin);
    }

    [Fact]
    public async Task New_people_from_an_allowed_domain_are_created_with_their_group_roles()
    {
        await using var web = await ProductionAsync(s =>
        {
            s.JitMode = JitProvisioning.AllowedDomains;
            s.AllowedEmailDomains = "acme.example";
        }, new SsoGroupMapping { GroupValue = "wb-finance", Roles = (int)OrgRoles.Finance });
        using var browser = web.Browser();

        using var callback = await web.SignInAsync(browser, new MockOidcProvider.User("new.hire@acme.example", "New Hire", ["pwd"], ["wb-finance"]));
        using var me = await browser.GetAsync("/api/v1/users/me/context");
        var user = await web.UserAsync("new.hire@acme.example");
        using var outsider = await web.SignInAsync(web.Browser(), new MockOidcProvider.User("someone@other.example", "Someone", ["pwd"]));

        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        Assert.True((await web.RolesAsync("new.hire@acme.example")).HasFlag(OrgRoles.Finance));
        Assert.False((await web.RolesAsync("new.hire@acme.example")).HasFlag(OrgRoles.Admin));
        Assert.Equal("/account/access-denied?reason=signin", outsider.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Each_tenant_signs_in_with_its_own_identity_provider()
    {
        await using var web = await MultiTenantWeb.DevelopmentAsync();
        var idp = new MockOidcProvider("https://idp.test/acme", "acme-workbench", Secret);
        web.Services.GetRequiredService<OrganizationSsoBackchannel>().Handler = idp;
        await web.InTenantAsync("acme", async services =>
        {
            await ConfigureAsync(services, idp, _ => { });
            return 0;
        });
        using var acme = web.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://acme.localhost/") });
        using var beta = web.Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://beta.localhost/") });

        var acmeChallenge = await acme.GetAsync("/account/signin?provider=sso");
        var betaPage = await beta.GetStringAsync("/account/signin?provider=sso");
        var (redirectUri, form) = idp.Authorize(acmeChallenge.Headers.Location!, new MockOidcProvider.User(MultiTenantWeb.DevAdmin, "Dev", ["mfa"]));

        // Beta has no IdP of its own: acme's authorization response is worthless there and never reaches the IdP.
        using var replayed = await beta.PostAsync("/signin-sso", new FormUrlEncodedContent(form));
        Assert.Equal(0, idp.TokenRequests);
        using var signedIn = await acme.PostAsync(redirectUri, new FormUrlEncodedContent(form));
        using var acmeMe = await acme.GetAsync("/api/v1/users/me/context");
        using var betaMe = await beta.GetAsync("/api/v1/users/me/context");

        Assert.Equal("https://acme.localhost/signin-sso", redirectUri);
        Assert.StartsWith("https://idp.test/acme/authorize", acmeChallenge.Headers.Location!.ToString());
        Assert.DoesNotContain("Sign in with", betaPage); // beta has only its built-in accounts (and the dev picker)
        Assert.Contains(DevSignIn.Path, betaPage);
        Assert.NotEqual(HttpStatusCode.OK, replayed.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, acmeMe.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, betaMe.StatusCode);
    }

    private static async Task<SsoWeb> ProductionAsync(Action<SignInSettings> configure, params SsoGroupMapping[] mappings)
    {
        var factory = new WorkbenchWebFactory("Production", oidcAuthority: "https://login.example.test/server/v2.0");
        var idp = new MockOidcProvider("https://idp.test/acme", "acme-workbench", Secret);
        factory.Services.GetRequiredService<OrganizationSsoBackchannel>().Handler = idp;
        await using var scope = factory.Services.CreateAsyncScope();
        await using (var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync())
        {
            await RoleStore.AddUserAsync(db, new AppUser { Email = Admin, DisplayName = "Ada Admin", CreatedAtUtc = DateTime.UtcNow }, OrgRoles.Admin);
            await db.SaveChangesAsync();
        }

        await ConfigureAsync(scope.ServiceProvider, idp, configure, mappings);
        return new SsoWeb(factory, idp);
    }

    private static async Task ConfigureAsync(IServiceProvider services, MockOidcProvider idp, Action<SignInSettings> configure, params SsoGroupMapping[] mappings)
    {
        await using (var db = await services.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync())
        {
            var settings = new SignInSettings
            {
                SsoEnabled = true,
                ProviderKind = SsoProviderKind.Okta,
                DisplayName = "Acme Okta",
                Authority = idp.Issuer,
                ClientId = idp.ClientId,
                ProtectedClientSecret = services.GetRequiredService<SsoSecretProtector>().Protect(Secret),
                UpdatedAtUtc = DateTime.UtcNow,
            };
            configure(settings);
            db.Set<SignInSettings>().Add(settings);
            db.Set<SsoGroupMapping>().AddRange(mappings);
            await db.SaveChangesAsync();
        }

        services.GetRequiredService<SignInSettingsStore>().Invalidate();
    }

    private sealed class SsoWeb(WorkbenchWebFactory factory, MockOidcProvider idp) : IAsyncDisposable
    {
        public MockOidcProvider Idp { get; } = idp;

        public HttpClient Browser() =>
            factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = true, BaseAddress = new Uri("https://localhost/") });

        /// <summary>Follows a sign-in from <paramref name="start"/> through the IdP; returns Workbench's response to the IdP's post-back.</summary>
        public async Task<HttpResponseMessage> SignInAsync(HttpClient browser, MockOidcProvider.User user, string start = "/account/signin?provider=sso")
        {
            using var challenge = await browser.GetAsync(start);
            Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
            var (redirectUri, form) = Idp.Authorize(challenge.Headers.Location!, user);
            return await browser.PostAsync(redirectUri, new FormUrlEncodedContent(form));
        }

        public async Task<List<SecurityEvent>> EventsAsync()
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return await db.Set<SecurityEvent>().AsNoTracking().ToListAsync();
        }

        public async Task<AppUser> UserAsync(string email)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return await db.Set<AppUser>().AsNoTracking().SingleAsync(u => u.Email == email);
        }

        /// <summary>The built-in roles the user holds.</summary>
        public async Task<OrgRoles> RolesAsync(string email)
        {
            await using var scope = factory.Services.CreateAsyncScope();
            await using var db = await scope.ServiceProvider.GetRequiredService<IWorkbenchDbContextFactory>().CreateAsync();
            return BuiltInRoles.FlagsOf(await RoleStore.RoleIdsOfAsync(db, (await UserAsync(email)).Id, default));
        }

        public ValueTask DisposeAsync() => factory.DisposeAsync();
    }
}

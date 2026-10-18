using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.FileProviders;
using Workbench.Contracts.ProjectAccounting;
using Workbench.Contracts.Security;
using Workbench.Contracts.Sheets;
using Workbench.Tests.Identity;
using Workbench.Web.Identity;
using Workbench.Web.Security;

namespace Workbench.Tests.Security;

/// <summary>The ISO 27001 controls through the real web host: headers, rate limits, startup checks, SCIM, sessions and the event log.</summary>
public sealed partial class WebSecurityTests
{
    private const string Admin = "dev@workbench.local";

    [Fact]
    public async Task Responses_carry_the_security_headers_and_a_strict_csp()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClientAs(null);

        using var page = await client.GetAsync(DevSignIn.Path);

        var csp = page.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("default-src 'self'", csp);
        Assert.Contains("script-src 'self';", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
        Assert.DoesNotContain("script-src 'self' 'unsafe-inline'", csp);
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("object-src 'none'", csp);
        Assert.Equal("nosniff", page.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("strict-origin-when-cross-origin", page.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal("DENY", page.Headers.GetValues("X-Frame-Options").Single());
        Assert.Contains("camera=()", page.Headers.GetValues("Permissions-Policy").Single());
    }

    [Fact]
    public async Task Production_sends_hsts_and_upgrades_insecure_requests()
    {
        await using var factory = new WorkbenchWebFactory("Production", testAuth: true, oidcAuthority: "https://login.example.test/tenant/v2.0");
        using var client = factory.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://workbench.example.test") });

        using var response = await client.GetAsync("/health/live");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("max-age=31536000", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Contains("includeSubDomains", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.Contains("upgrade-insecure-requests", response.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Fact]
    public async Task Health_endpoints_are_anonymous_and_ready_checks_the_database()
    {
        await using var factory = new WorkbenchWebFactory("Development");
        using var client = factory.CreateClientAs(null);

        using var live = await client.GetAsync("/health/live");
        using var ready = await client.GetAsync("/health/ready");

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal("Healthy", await ready.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Api_and_sign_in_requests_are_rate_limited()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true, settings: new Dictionary<string, string>
        {
            ["RateLimiting:ApiPerMinute"] = "3",
            ["RateLimiting:SignInPerMinute"] = "2",
        });
        using var viewer = factory.CreateClientAs("viewer@example.com");
        using var other = factory.CreateClientAs("analyst@example.com");
        using var anonymous = factory.CreateClientAs(null);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
        {
            using var response = await viewer.GetAsync("/api/v1/sheets");
            statuses.Add(response.StatusCode);
        }

        using var otherUser = await other.GetAsync("/api/v1/sheets");
        var signIns = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            using var response = await anonymous.GetAsync(DevSignIn.Path);
            signIns.Add(response.StatusCode);
        }

        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests], statuses);
        Assert.Equal(HttpStatusCode.OK, otherUser.StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, signIns[^1]);
        Assert.Contains(await EventsAsync(factory, SecurityEventTypes.RateLimited), e => e.Target == "/api/v1/sheets" && e.Actor == "viewer@example.com");
    }

    [Fact]
    public void Weak_production_configuration_is_refused()
    {
        var weak = Problems("Production", new()
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["urls"] = "http://0.0.0.0:8080",
            ["Scim:BearerToken"] = "short",
        });
        var strong = Problems("Production", new()
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["urls"] = "https://0.0.0.0:443",
            ["Scim:BearerToken"] = new string('x', 40),
            ["Database:Provider"] = "PostgreSQL",
        });
        var scimOff = Problems("Staging", new()
        {
            ["Authentication:Oidc:Authority"] = "https://login.example.test/",
            ["Security:TlsTerminatedByProxy"] = "true",
            ["Scim:Enabled"] = "false",
        });

        Assert.Contains(weak, p => p.Contains("plain HTTP"));
        Assert.Contains(weak, p => p.Contains("Scim:BearerToken"));
        Assert.Contains(weak, p => p.Contains("Sqlite in Production"));
        Assert.Empty(strong);
        Assert.Empty(scimOff);
        Assert.Contains(Problems("Production", new()), p => p.Contains("Authentication:Oidc"));
        Assert.Empty(Problems("Development", new()));
    }

    [Fact]
    public void The_host_refuses_to_start_with_an_empty_scim_token()
    {
        using var factory = new WorkbenchWebFactory("Production", oidcAuthority: "https://login.example.test/tenant/v2.0", settings: new Dictionary<string, string>
        {
            ["Scim:BearerToken"] = "",
        });

        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Scim:BearerToken", error.ToString());
    }

    [Fact]
    public async Task Scim_creates_filters_patches_and_deactivates_users_with_the_token()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var scim = factory.CreateClientAs(null);
        scim.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", WorkbenchWebFactory.ScimToken);
        using var noToken = factory.CreateClientAs(null);
        using var wrongToken = factory.CreateClientAs(null);
        wrongToken.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "wrong-token-wrong-token-wrong-token");
        using var signedInUser = factory.CreateClientAs("viewer@example.com");

        Assert.Equal(HttpStatusCode.Unauthorized, (await noToken.GetAsync("/scim/v2/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrongToken.GetAsync("/scim/v2/Users")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await signedInUser.GetAsync("/scim/v2/Users")).StatusCode);

        using var created = await PostScimAsync(scim, "/scim/v2/Users", """
            {"schemas":["urn:ietf:params:scim:schemas:core:2.0:User"],"userName":"Jordan.Price@example.com","externalId":"entra-123",
             "name":{"givenName":"Jordan","familyName":"Price"},"emails":[{"value":"jordan.price@example.com","primary":true}],"active":true}
            """);
        var user = JsonNode.Parse(await created.Content.ReadAsStringAsync())!;
        var id = user["id"]!.GetValue<string>();
        using var duplicate = await PostScimAsync(scim, "/scim/v2/Users", """{"userName":"jordan.price@example.com"}""");

        var filtered = JsonNode.Parse(await scim.GetStringAsync("/scim/v2/Users?filter=" + Uri.EscapeDataString("userName eq \"jordan.price@example.com\"")))!;
        var none = JsonNode.Parse(await scim.GetStringAsync("/scim/v2/Users?filter=" + Uri.EscapeDataString("userName eq \"nobody@example.com\"")))!;
        using var badFilter = await scim.GetAsync("/scim/v2/Users?filter=" + Uri.EscapeDataString("displayName co \"x\""));

        // Entra ID style: active as a string, and a name change without a path.
        using var patched = await PatchScimAsync(scim, $"/scim/v2/Users/{id}", """
            {"schemas":["urn:ietf:params:scim:api:messages:2.0:PatchOp"],"Operations":[
              {"op":"Replace","path":"active","value":"False"},
              {"op":"replace","value":{"displayName":"Jordan R. Price"}}]}
            """);
        var afterPatch = JsonNode.Parse(await patched.Content.ReadAsStringAsync())!;

        using var givenOnly = await PatchScimAsync(scim, $"/scim/v2/Users/{id}", """{"Operations":[{"op":"replace","path":"name.givenName","value":"Jordy"}]}""");
        var afterGivenName = JsonNode.Parse(await givenOnly.Content.ReadAsStringAsync())!;
        using var reactivated = await PatchScimAsync(scim, $"/scim/v2/Users/{id}", """{"Operations":[{"op":"replace","path":"active","value":true}]}""");
        using var deleted = await scim.DeleteAsync($"/scim/v2/Users/{id}");
        var afterDelete = JsonNode.Parse(await scim.GetStringAsync($"/scim/v2/Users/{id}"))!;
        using var missing = await scim.GetAsync($"/scim/v2/Users/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        Assert.Equal("application/scim+json", created.Content.Headers.ContentType!.MediaType);
        Assert.Equal("jordan.price@example.com", user["userName"]!.GetValue<string>());
        Assert.Equal("Jordan Price", user["displayName"]!.GetValue<string>());
        Assert.Equal("entra-123", user["externalId"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        Assert.DoesNotContain("Parameter", await duplicate.Content.ReadAsStringAsync());
        Assert.Equal(1, filtered["totalResults"]!.GetValue<int>());
        Assert.Equal(id, filtered["Resources"]![0]!["id"]!.GetValue<string>());
        Assert.Equal(0, none["totalResults"]!.GetValue<int>());
        Assert.Equal(HttpStatusCode.BadRequest, badFilter.StatusCode);
        Assert.Equal(HttpStatusCode.OK, patched.StatusCode);
        Assert.False(afterPatch["active"]!.GetValue<bool>());
        Assert.Equal("Jordan R. Price", afterPatch["displayName"]!.GetValue<string>());
        Assert.Equal("Jordy R. Price", afterGivenName["displayName"]!.GetValue<string>());
        Assert.True(JsonNode.Parse(await reactivated.Content.ReadAsStringAsync())!["active"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.False(afterDelete["active"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // SCIM users sign in without an invite; a deactivated one can't.
        using var admin = factory.CreateClientAs(Admin);
        var users = await admin.GetFromJsonAsync<List<UserAdminDto>>("/api/v1/admin/users?query=jordan");
        Assert.True(users!.Single().ScimManaged);
        Assert.False(users.Single().IsActive);

        var scimEvents = await EventsAsync(factory, SecurityEventTypes.Scim);
        Assert.Contains(scimEvents, e => e.Outcome == SecurityOutcomes.Denied && e.Detail!.Contains("bearer token"));
        Assert.Contains(scimEvents, e => e.Target!.StartsWith("POST /scim/v2/Users") && e.Outcome == SecurityOutcomes.Success);
        Assert.Contains(scimEvents, e => e.Target!.StartsWith("DELETE") && e.Actor == "scim");
        Assert.Contains(await EventsAsync(factory, SecurityEventTypes.UserDeactivated), e => e.Target == "jordan.price@example.com" && e.Actor == "scim");
    }

    [Fact]
    public async Task Deactivating_a_user_ends_their_cookie_session_on_the_next_request()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var analyst = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using var admin = factory.CreateClientAs(Admin);

        using var signIn = await DevSignInAsync(analyst, "analyst@example.com");
        using var before = await analyst.GetAsync("/api/v1/sheets");
        using var deactivate = await admin.PostAsJsonAsync("/api/v1/admin/users/analyst@example.com/deactivate", new { Reason = "Offboarded" });
        using var after = await analyst.GetAsync("/api/v1/sheets");
        using var again = await DevSignInAsync(analyst, "analyst@example.com");

        Assert.Equal(HttpStatusCode.Redirect, signIn.StatusCode);
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        Assert.Equal(HttpStatusCode.OK, deactivate.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.StartsWith(AccountEndpoints.AccessDeniedPath, again.Headers.Location!.ToString());

        var rejected = await EventsAsync(factory, SecurityEventTypes.SessionRejected);
        Assert.Contains(rejected, e => e.Target == "analyst@example.com" && e.Detail == "The account is deactivated.");
        Assert.Contains(await EventsAsync(factory, SecurityEventTypes.SignInFailure), e => e.Target == "analyst@example.com");
    }

    [Fact]
    public async Task Sign_out_everywhere_ends_existing_sessions_but_allows_a_new_sign_in()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var pm = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using var admin = factory.CreateClientAs(Admin);

        using var _ = await DevSignInAsync(pm, "pm@example.com");
        using var revoke = await admin.PostAsync("/api/v1/admin/users/pm@example.com/sign-out-everywhere", null);
        using var after = await pm.GetAsync("/api/v1/sheets");
        await Task.Delay(5);
        using var __ = await DevSignInAsync(pm, "pm@example.com");
        using var renewed = await pm.GetAsync("/api/v1/sheets");

        Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal(HttpStatusCode.OK, renewed.StatusCode);
    }

    [Fact]
    public async Task Sessions_end_after_the_absolute_lifetime()
    {
        var principal = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
            [new("email", "pm@example.com"), SessionValidation.SignedInAt(DateTimeOffset.UtcNow.AddHours(-13))], "test"));
        await using var factory = new WorkbenchWebFactory("Development");
        _ = factory.Server;
        var cache = (Workbench.Application.Identity.UserStatusCache)factory.Services.GetService(typeof(Workbench.Application.Identity.UserStatusCache))!;

        var expired = await SessionValidation.CheckAsync(principal, cache, new SessionSettings(), TimeProvider.System, enforceLifetime: true, CancellationToken.None);
        var fresh = await SessionValidation.CheckAsync(principal, cache, new SessionSettings { AbsoluteLifetimeHours = 24 }, TimeProvider.System, enforceLifetime: true, CancellationToken.None);

        Assert.Equal("The session reached its maximum lifetime.", expired);
        Assert.Null(fresh);
        Assert.Equal(TimeSpan.FromMinutes(30), new SessionSettings().IdleTimeout);
    }

    [Fact]
    public async Task Sign_ins_sign_outs_denials_exports_and_period_close_are_logged()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true, settings: new Dictionary<string, string> { ["Authentication:RequireInvite"] = "true" });
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });
        using var pm = factory.CreateClientAs("pm@example.com");
        using var analyst = factory.CreateClientAs("analyst@example.com");

        using var signIn = await DevSignInAsync(browser, "viewer@example.com");
        using var signOut = await browser.GetAsync(AccountEndpoints.SignOutPath);
        using var unknown = await DevSignInAsync(browser, "intruder@example.com");
        using var denied = await analyst.GetAsync("/api/v1/audit");
        using var labor = await pm.GetAsync("/api/v1/time/reports/labor-distribution.csv?from=2026-01-01&to=2026-12-31");
        var project = (await pm.GetFromJsonAsync<List<ProjectDto>>("/api/v1/projects"))!.First();
        using var financial = await pm.GetAsync($"/api/v1/projects/{project.Id}/financials/wbs.csv");
        var period = (await pm.GetFromJsonAsync<List<FiscalPeriodDto>>("/api/v1/fiscal/periods"))!.First();
        using var close = await pm.PutAsJsonAsync($"/api/v1/fiscal/periods/{period.Id}/status", new { Status = FiscalPeriodState.Closed, Reason = "Month end" });

        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Equal(HttpStatusCode.OK, labor.StatusCode);
        Assert.Equal(HttpStatusCode.OK, financial.StatusCode);
        Assert.Equal(HttpStatusCode.OK, close.StatusCode);

        var events = await EventsAsync(factory, null);
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.DevSignIn && e.Actor == "viewer@example.com");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignOut && e.Actor == "viewer@example.com");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.SignInFailure && e.Target == "intruder@example.com");
        Assert.Single(events, e => e.EventType == SecurityEventTypes.PermissionDenied && e.Actor == "analyst@example.com");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.ExportLaborCsv && e.Actor == "pm@example.com");
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.ExportFinancialCsv && e.Target!.EndsWith(".csv"));
        Assert.Contains(events, e => e.EventType == SecurityEventTypes.PeriodStatusChanged && e.Detail == "Closed: Month end");
        Assert.All(events, e => Assert.False(string.IsNullOrEmpty(e.Outcome)));
    }

    [Fact]
    public async Task Admin_security_endpoints_refuse_non_admins()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var pm = factory.CreateClientAs("pm@example.com");

        Assert.Equal(HttpStatusCode.Forbidden, (await pm.GetAsync("/api/v1/admin/users")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync("/api/v1/admin/invites", new CreateInviteRequest("x@example.com", "X", 0))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await pm.PostAsJsonAsync("/api/v1/admin/security-events/search", new SecurityEventQuery())).StatusCode);
    }

    [Fact]
    public async Task Invited_people_can_use_the_dev_sign_in_and_get_their_roles()
    {
        await using var factory = new WorkbenchWebFactory("Development", testAuth: true);
        using var admin = factory.CreateClientAs(Admin);
        using var browser = factory.CreateClient(new() { AllowAutoRedirect = false, HandleCookies = true });

        using var invite = await admin.PostAsJsonAsync("/api/v1/admin/invites", new CreateInviteRequest("sam@example.com", "Sam Ortiz", Workbench.Contracts.Identity.OrgRoles.Timekeeper));
        var page = await browser.GetStringAsync(DevSignIn.Path);
        using var signIn = await DevSignInAsync(browser, "sam@example.com");
        var context = await browser.GetFromJsonAsync<Workbench.Contracts.Identity.CurrentUserInfo>("/api/v1/users/me/context");
        var invites = await admin.GetFromJsonAsync<List<InviteDto>>("/api/v1/admin/invites");

        Assert.Equal(HttpStatusCode.OK, invite.StatusCode);
        Assert.Contains("sam@example.com", page);
        Assert.Equal(Workbench.Contracts.Identity.OrgRoles.Timekeeper, context!.Roles);
        Assert.Equal(InviteStatus.Accepted, invites!.Single().Status);
    }

    private static IReadOnlyList<string> Problems(string environment, Dictionary<string, string?> values) =>
        ProductionConfigurationCheck.FindProblems(new ConfigurationBuilder().AddInMemoryCollection(values).Build(), new FakeEnvironment(environment));

    private static async Task<HttpResponseMessage> DevSignInAsync(HttpClient client, string email)
    {
        var page = await client.GetStringAsync(DevSignIn.Path);
        var token = TokenRegex().Match(page).Groups[1].Value;
        return await client.PostAsync(DevSignIn.Path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["email"] = email,
            ["returnUrl"] = "/",
        }));
    }

    private static Task<HttpResponseMessage> PostScimAsync(HttpClient client, string path, string json) =>
        client.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/scim+json"));

    private static Task<HttpResponseMessage> PatchScimAsync(HttpClient client, string path, string json) =>
        client.PatchAsync(path, new StringContent(json, Encoding.UTF8, "application/scim+json"));

    private static async Task<List<SecurityEventDto>> EventsAsync(WorkbenchWebFactory factory, string? type)
    {
        using var admin = factory.CreateClientAs(Admin);
        using var response = await admin.PostAsJsonAsync("/api/v1/admin/security-events/search", new SecurityEventQuery(EventType: type, Limit: 5000));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<List<SecurityEventDto>>())!;
    }

    [GeneratedRegex("name=\"__RequestVerificationToken\" value=\"([^\"]+)\"")]
    private static partial Regex TokenRegex();

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Workbench.Web";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}

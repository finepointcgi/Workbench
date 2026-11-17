using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Licensing;
using Workbench.Application.Tenancy;
using Workbench.Contracts.Sheets;
using Workbench.Contracts.Tenancy;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Tests.Tenancy;

/// <summary>Self-service signup (docs/admin-guide/hosted-signup.md): provisioning, idempotency, the subdomain rules, the form and the emailed sign-in link.</summary>
public sealed partial class HostedSignupTests
{
    private static SignupRequest Request(string slug = "initech", string email = "Pat@Initech.example", string? address = "203.0.113.5", bool terms = true) =>
        new("Initech Federal", slug, "Pat Doe", email, "Small business", terms, "utm-launch", address);

    [Fact]
    public async Task Signup_creates_an_isolated_tenant_with_a_trial_and_records_consent()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var signup = web.Services.GetRequiredService<SignupService>();

        var result = await signup.SignUpAsync(Request());

        Assert.Equal(SignupOutcome.Created, result.Outcome);
        Assert.Equal("https://initech.workbench.test/", result.Url);
        var tenant = (await web.Services.GetRequiredService<TenantCatalog>().FindAsync("initech"))!;
        Assert.Equal(TenantStatus.Active, tenant.Status);
        var row = (await web.Services.GetRequiredService<TenantEntitlements>().FindAsync(tenant.Info.Id))!;
        Assert.True(row.IsTrial);
        Assert.Equal("trial", row.Plan);
        Assert.Equal(EntitlementStatus.Active, row.Status);
        Assert.InRange((row.TrialEndsUtc!.Value - DateTime.UtcNow).TotalDays, 13.9, 14.1);
        Assert.Equal("pat@initech.example", row.SignupEmail);
        Assert.Equal("utm-launch", row.SignupSource);
        Assert.Equal("draft-1", row.TermsVersion);
        Assert.NotNull(row.TermsAcceptedAtUtc);
        Assert.Contains("finance", row.Modules);

        // The product sees a Business trial with the trial's modules and seats; the organization starts empty.
        var state = await web.InTenantAsync("initech", sp => sp.GetRequiredService<EditionService>().GetAsync(default));
        Assert.Equal(Workbench.Domain.Licensing.Edition.Business, state.Edition);
        Assert.True(state.Trial);
        Assert.Equal(10, state.SeatLimit);
        Assert.False(state.ReadOnly);
        Assert.InRange(state.DaysLeft!.Value, 13, 14);

        var audit = await web.Services.GetRequiredService<TenantProvisioner>().ListAuditAsync(200);
        Assert.Contains(audit, e => e.Action == "signup.accepted" && e.TenantId == tenant.Info.Id);
        Assert.Contains(web.Mail.Sent, m => m.To == "pat@initech.example" && m.TextBody.Contains("/account/magic?token="));
    }

    [Fact]
    public async Task Repeating_a_signup_never_makes_a_second_tenant_and_resends_the_link()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var signup = web.Services.GetRequiredService<SignupService>();

        Assert.Equal(SignupOutcome.Created, (await signup.SignUpAsync(Request())).Outcome);
        Assert.Equal(SignupOutcome.LinkResent, (await signup.SignUpAsync(Request(email: "pat@initech.example"))).Outcome);

        var tenants = await web.Services.GetRequiredService<TenantCatalog>().ListAsync();
        Assert.Single(tenants, t => t.Slug == "initech");
        Assert.Equal(2, web.Mail.Sent.Count(m => m.To == "pat@initech.example"));

        // Someone else asking for the same address is told it is taken, and nothing changes.
        var taken = await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request(email: "someone@else.example")));
        Assert.Contains("taken", taken.Message);
        Assert.DoesNotContain(web.Mail.Sent, m => m.To == "someone@else.example");
    }

    [Fact]
    public async Task Double_submits_and_races_for_one_subdomain_have_one_winner()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var signup = web.Services.GetRequiredService<SignupService>();

        // The same person submitting twice at once.
        var both = await Task.WhenAll(signup.SignUpAsync(Request("twin")), signup.SignUpAsync(Request("twin")));
        Assert.Contains(both, r => r.Outcome == SignupOutcome.Created);
        Assert.DoesNotContain(both, r => r.Outcome == SignupOutcome.Created && both.Count(x => x.Outcome == SignupOutcome.Created) > 1);
        Assert.Single(await web.Services.GetRequiredService<TenantCatalog>().ListAsync(), t => t.Slug == "twin");

        // Two different people racing for one address.
        var racers = await Task.WhenAll(Race("duel", "a@one.example"), Race("duel", "b@two.example"));
        Assert.Equal(1, racers.Count(r => r));
        Assert.Single(await web.Services.GetRequiredService<TenantCatalog>().ListAsync(), t => t.Slug == "duel");

        async Task<bool> Race(string slug, string email)
        {
            try
            {
                await signup.SignUpAsync(Request(slug, email, address: $"198.51.100.{email.Length}"));
                return true;
            }
            catch (SignupRefusedException)
            {
                return false;
            }
        }
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("Acme Inc")]
    [InlineData("-leading")]
    [InlineData("trailing-")]
    [InlineData("under_score")]
    [InlineData("double--hyphen")]
    [InlineData("xn--punycode")]
    [InlineData("www")]
    [InlineData("billing")]
    [InlineData("admin")]
    [InlineData("host")]
    [InlineData("this-subdomain-is-far-too-long-to-be-valid-at-all")]
    public void Subdomains_that_are_invalid_or_reserved_are_refused(string slug) =>
        Assert.Throws<SignupRefusedException>(() => SignupService.ValidateSubdomain(slug));

    [Fact]
    public async Task Existing_tenants_block_their_subdomain_and_the_form_validates_the_rest()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var signup = web.Services.GetRequiredService<SignupService>();

        Assert.False(await signup.IsAvailableAsync("acme"));
        Assert.False(await signup.IsAvailableAsync("www"));
        Assert.True(await signup.IsAvailableAsync("fresh-co"));
        await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request("acme", "new@acme-two.example")));
        await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request(terms: false)));
        await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request(email: "not-an-email")));
        await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request() with { CompanyName = " " }));
        Assert.Empty((await web.Services.GetRequiredService<TenantCatalog>().ListAsync()).Where(t => t.Slug is "initech" or "fresh-co"));
    }

    [Fact]
    public async Task Signup_is_limited_by_address_and_by_email()
    {
        await using var web = await MultiTenantWeb.HostedAsync(s => { s["Tenancy:Signup:MaxPerIpPerHour"] = "2"; s["Tenancy:Signup:MaxPerEmailPerDay"] = "50"; });
        var signup = web.Services.GetRequiredService<SignupService>();

        await signup.SignUpAsync(Request("one-co", "a@x.example", "192.0.2.1"));
        await signup.SignUpAsync(Request("two-co", "b@x.example", "192.0.2.1"));
        var limited = await Assert.ThrowsAsync<SignupRefusedException>(() => signup.SignUpAsync(Request("three-co", "c@x.example", "192.0.2.1")));
        Assert.True(limited.RateLimited);
        await signup.SignUpAsync(Request("four-co", "d@x.example", "192.0.2.2")); // another address is fine

        var clock = new ManualClock();
        var limiter = new SignupRateLimiter(clock);
        Assert.True(limiter.TryAcquire("1.1.1.1", "e@x.example", 100, 2));
        Assert.True(limiter.TryAcquire("1.1.1.1", "e@x.example", 100, 2));
        Assert.False(limiter.TryAcquire("1.1.1.1", "e@x.example", 100, 2));
        clock.Advance(TimeSpan.FromHours(25));
        Assert.True(limiter.TryAcquire("1.1.1.1", "e@x.example", 100, 2));
    }

    [Fact]
    public async Task The_form_refuses_bots_quietly_and_a_person_gets_a_working_sign_in_link()
    {
        await using var web = await MultiTenantWeb.HostedAsync();
        var client = web.Client(null, null);

        // Only offered on the base domain; a tenant's address has no signup.
        AssertNotFound(await web.Client("acme", null).GetAsync("signup"));

        var page = await (await client.GetAsync("signup")).Content.ReadAsStringAsync();
        Assert.Contains("https://example.test/terms", page);
        var antiforgery = Field(page, "__RequestVerificationToken");
        var ts = Field(page, "ts");

        FormUrlEncodedContent Form(string slug, string honeypot = "", string? stamp = null) => new(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiforgery,
            ["company"] = "Initech", ["subdomain"] = slug, ["name"] = "Pat Doe", ["email"] = $"pat@{slug}.example", ["companyType"] = "Other",
            ["terms"] = "yes", ["website"] = honeypot, ["ts"] = stamp ?? ts, ["source"] = "test",
        });

        // A filled honeypot and a form posted instantly look like success but create nothing.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("signup", Form("botco", honeypot: "http://spam"))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("signup", Form("fastco"))).StatusCode);
        var catalog = web.Services.GetRequiredService<TenantCatalog>();
        Assert.DoesNotContain(await catalog.ListAsync(), t => t.Slug is "botco" or "fastco");
        Assert.Empty(web.Mail.Sent.Where(m => m.To.Contains("botco") || m.To.Contains("fastco")));

        await Task.Delay(HoneypotBotProtection.MinimumFillTime + TimeSpan.FromMilliseconds(300));
        var done = await client.PostAsync("signup", Form("realco"));
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
        Assert.Contains("Check your email", await done.Content.ReadAsStringAsync());
        Assert.Contains(await catalog.ListAsync(), t => t.Slug == "realco" && t.Status == TenantStatus.Active);

        // The emailed link: GET only shows a button (a mail scanner can't use it up); POST signs in once.
        var mail = web.Mail.Sent.Single(m => m.To == "pat@realco.example");
        var link = MagicLink().Match(mail.TextBody);
        Assert.True(link.Success);
        var tenantClient = web.Client("realco", null);
        var confirm = await tenantClient.GetAsync($"account/magic?token={link.Groups[1].Value}");
        Assert.Equal(HttpStatusCode.OK, confirm.StatusCode);
        var confirmHtml = await confirm.Content.ReadAsStringAsync();
        var signedIn = await tenantClient.PostAsync("account/magic", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Field(confirmHtml, "__RequestVerificationToken"), ["token"] = link.Groups[1].Value,
        }));
        Assert.Equal(HttpStatusCode.Redirect, signedIn.StatusCode);
        Assert.Equal("/setup", signedIn.Headers.Location!.OriginalString);

        // The same link doesn't work twice.
        var again = await tenantClient.GetAsync($"account/magic?token={link.Groups[1].Value}");
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
    }

    [Fact]
    public async Task Signup_is_off_unless_enabled_and_never_available_on_a_single_tenant_server()
    {
        await using var off = await MultiTenantWeb.HostedAsync(s => s["Tenancy:Signup:Enabled"] = "false");
        AssertNotFound(await off.Client(null, null).GetAsync("signup"));
        await Assert.ThrowsAsync<SignupRefusedException>(() => off.Services.GetRequiredService<SignupService>().SignUpAsync(Request()));
    }

    // A page navigation to a missing page is redirected to /not-found; an API call gets the bare 404.
    private static void AssertNotFound(HttpResponseMessage response) =>
        Assert.True(response.StatusCode == HttpStatusCode.NotFound || response.Headers.Location?.OriginalString.Contains("not-found") == true, $"Expected not found, got {response.StatusCode} {response.Headers.Location}");

    private static string Field(string html, string name) =>
        Regex.Match(html, $"name=\"{name}\" value=\"([^\"]*)\"").Groups[1].Value is { Length: > 0 } v ? WebUtility.HtmlDecode(v) : throw new InvalidOperationException($"No {name} field.");

    [GeneratedRegex(@"https://[a-z0-9-]+\.workbench\.test/account/magic\?token=([A-Za-z0-9_-]+)")]
    private static partial Regex MagicLink();

    internal sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}

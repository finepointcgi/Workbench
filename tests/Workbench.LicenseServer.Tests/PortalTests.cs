using System.Net;
using System.Text.RegularExpressions;

namespace Workbench.LicenseServer.Tests;

public class PortalTests
{
    private static async Task<(HttpClient Client, string Cookie)> SignInAsync(TestServer server, string email)
    {
        var c = server.Plain();
        var res = await c.PostAsync("/portal/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = email }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var mail = server.Mail.Sent.Last(m => m.Subject.Contains("Sign in"));
        var token = Regex.Match(mail.Body, @"https://license\.test/portal/verify\?token=([\w-]+)").Groups[1].Value;
        Assert.NotEmpty(token);
        var verify = await c.GetAsync($"/portal/verify?token={token}");
        Assert.Equal(HttpStatusCode.Redirect, verify.StatusCode);
        var cookie = verify.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        Assert.Equal(HttpStatusCode.BadRequest, (await c.GetAsync($"/portal/verify?token={token}")).StatusCode); // single use
        return (c, cookie);
    }

    private static HttpRequestMessage With(string cookie, HttpMethod m, string url)
    {
        var r = new HttpRequestMessage(m, url);
        r.Headers.Add("Cookie", cookie);
        return r;
    }

    [Fact]
    public async Task Sign_in_by_link_shows_the_subscription_and_downloads_a_valid_key()
    {
        using var server = new TestServer();
        var (_, _, licenseId, _) = await server.NewSubscriptionAsync(["billing", "capture"], seats: 15, name: "Portal Co", email: "boss@portal.test");
        var (c, cookie) = await SignInAsync(server, "Boss@Portal.test");

        var page = await (await c.SendAsync(With(cookie, HttpMethod.Get, "/portal"))).Content.ReadAsStringAsync();
        Assert.Contains("Portal Co", page);
        Assert.Contains("15 full seats", page);
        Assert.Contains($"/portal/key/{licenseId}", page);
        Assert.DoesNotContain("Manage billing", page); // no Stripe customer on a manual subscription

        var download = await c.SendAsync(With(cookie, HttpMethod.Get, $"/portal/key/{licenseId}"));
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.True(Wb2Verifier.TryVerify(await download.Content.ReadAsStringAsync(), server.PublicKeyPem, out var claims));
        Assert.Equal(15, claims!.Seats);
    }

    [Fact]
    public async Task The_portal_needs_a_session_and_hides_other_customers()
    {
        using var server = new TestServer();
        var (_, _, licenseId, _) = await server.NewSubscriptionAsync(["billing"], email: "a@one.test");
        await server.NewSubscriptionAsync(["billing"], name: "Other", email: "b@two.test");

        var anon = server.Plain();
        Assert.Contains("Email me a link", await anon.GetStringAsync("/portal"));
        Assert.Equal(HttpStatusCode.Redirect, (await anon.GetAsync($"/portal/key/{licenseId}")).StatusCode);

        var (c, cookie) = await SignInAsync(server, "b@two.test");
        Assert.Equal(HttpStatusCode.NotFound, (await c.SendAsync(With(cookie, HttpMethod.Get, $"/portal/key/{licenseId}"))).StatusCode);
        var forged = await anon.SendAsync(With("wb_portal=not-a-real-cookie", HttpMethod.Get, "/portal"));
        Assert.Contains("Email me a link", await forged.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_unknown_email_gets_the_same_answer_and_no_mail()
    {
        using var server = new TestServer();
        var res = await server.Plain().PostAsync("/portal/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "nobody@nowhere.test" }));
        Assert.Contains("on its way", await res.Content.ReadAsStringAsync());
        Assert.Empty(server.Mail.Sent);
    }

    [Fact]
    public async Task A_stripe_customer_can_open_the_billing_portal_to_change_seats()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = new("sub_1", "cus_1", "active", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1), false, [new(TestServer.SeatPrice, 5), new(TestServer.BillingPrice, 1)]);
        var payload = System.Text.Json.JsonSerializer.Serialize(new { id = "evt_1", type = "checkout.session.completed", data = new { @object = new { mode = "subscription", subscription = "sub_1", customer = "cus_1", customer_details = new { email = "pay@stripe.test", name = "Stripe Co" } } } });
        var hook = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook") { Content = new StringContent(payload) };
        hook.Headers.Add("Stripe-Signature", Stripe.StripeSignature.Header(payload, TestServer.WebhookSecret, DateTimeOffset.UtcNow));
        Assert.Equal(HttpStatusCode.OK, (await server.CreateClient().SendAsync(hook)).StatusCode);

        var (c, cookie) = await SignInAsync(server, "pay@stripe.test");
        Assert.Contains("Manage billing", await (await c.SendAsync(With(cookie, HttpMethod.Get, "/portal"))).Content.ReadAsStringAsync());
        var manage = await c.SendAsync(With(cookie, HttpMethod.Post, "/portal/manage"));
        Assert.Equal(HttpStatusCode.Redirect, manage.StatusCode);
        Assert.StartsWith("https://billing.stripe.test/session/cus_1", manage.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        using var server = new TestServer(new() { ["RateLimits:LoginPerMinute"] = "2" });
        var c = server.Plain();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++)
            codes.Add((await c.PostAsync("/portal/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["email"] = "x@y.test" }))).StatusCode);
        Assert.Equal([HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }
}

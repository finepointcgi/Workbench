using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Workbench.LicenseServer.Data;
using Workbench.LicenseServer.Stripe;
using Microsoft.EntityFrameworkCore;

namespace Workbench.LicenseServer.Tests;

/// <summary>Stripe events are locally signed sample payloads and the Stripe API is a fake: nothing here talks to real Stripe.</summary>
public class StripeTests
{
    private static string Event(string id, string type, object obj) =>
        JsonSerializer.Serialize(new { id, type, data = new { @object = obj } });

    private static async Task<HttpResponseMessage> SendAsync(TestServer s, string payload, string? secret = TestServer.WebhookSecret, DateTimeOffset? at = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        if (secret != null) req.Headers.Add("Stripe-Signature", StripeSignature.Header(payload, secret, at ?? DateTimeOffset.UtcNow));
        return await s.CreateClient().SendAsync(req);
    }

    private static StripeSubscription Sub(string id, string status = "active", int seats = 10, DateTime? end = null, params string[] extraPrices)
    {
        var items = new List<StripeItem> { new(TestServer.SeatPrice, seats), new(TestServer.BillingPrice, 1) };
        items.AddRange(extraPrices.Select(p => new StripeItem(p, 1)));
        return new(id, "cus_1", status, DateTime.UtcNow.Date, end ?? DateTime.UtcNow.Date.AddMonths(1), false, items);
    }

    private static object Checkout(string sub = "sub_1") => new { mode = "subscription", subscription = sub, customer = "cus_1", customer_details = new { email = "Buyer@Corp.test", name = "Corp Inc" } };

    [Fact]
    public void Signature_verification_accepts_only_a_correct_fresh_signature()
    {
        var now = DateTimeOffset.UtcNow;
        var body = "{\"id\":\"evt_1\"}";
        Assert.True(StripeSignature.Verify(body, StripeSignature.Header(body, "s", now), "s", now, 300));
        Assert.False(StripeSignature.Verify(body + " ", StripeSignature.Header(body, "s", now), "s", now, 300));
        Assert.False(StripeSignature.Verify(body, StripeSignature.Header(body, "other", now), "s", now, 300));
        Assert.False(StripeSignature.Verify(body, StripeSignature.Header(body, "s", now.AddMinutes(-10)), "s", now, 300));
        Assert.False(StripeSignature.Verify(body, null, "s", now, 300));
        Assert.False(StripeSignature.Verify(body, "garbage", "s", now, 300));
        // Stripe may send several v1 values while a secret rolls; any match is enough.
        var t = now.ToUnixTimeSeconds();
        Assert.True(StripeSignature.Verify(body, $"t={t},v1=deadbeef,v1={StripeSignature.Sign(body, t.ToString(), "s")}", "s", now, 300));
    }

    [Fact]
    public async Task The_webhook_rejects_bad_or_missing_signatures_and_works_unconfigured()
    {
        using var server = new TestServer();
        var payload = Event("evt_1", "checkout.session.completed", Checkout());
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(server, payload, secret: null)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(server, payload, secret: "wrong")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SendAsync(server, payload, at: DateTimeOffset.UtcNow.AddHours(-1))).StatusCode);

        using var off = new TestServer(new() { ["Stripe:WebhookSecret"] = "" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await SendAsync(off, payload, secret: "x")).StatusCode);
    }

    [Fact]
    public async Task Checkout_creates_a_customer_subscription_and_emails_a_key()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1", seats: 8, extraPrices: TestServer.BundlePrice);
        var res = await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var mail = Assert.Single(server.Mail.Sent);
        Assert.Equal("buyer@corp.test", mail.To);
        var key = mail.Body.Split('\n').Single(l => l.StartsWith("WB2."));
        Assert.True(Wb2Verifier.TryVerify(key, server.PublicKeyPem, out var claims));
        Assert.Equal("Corp Inc", claims!.Licensee);
        Assert.Equal(13, claims.Seats); // 8 priced seats + 5 included in the bundle
        Assert.Equal(["billing", "enterprise", "govcon"], claims.Modules);
        Assert.False(claims.Trial);

        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDb>();
        var customer = await db.Customers.SingleAsync();
        Assert.Equal("cus_1", customer.StripeCustomerId);
        Assert.Equal("Stripe", (await db.Subscriptions.SingleAsync()).Source);
    }

    [Fact]
    public async Task A_redelivered_event_does_nothing()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1");
        var payload = Event("evt_1", "checkout.session.completed", Checkout());
        await SendAsync(server, payload);
        var again = await SendAsync(server, payload);
        Assert.Contains("duplicate", await again.Content.ReadAsStringAsync());
        Assert.Single(server.Mail.Sent);
        await using var scope = server.Services.CreateAsyncScope();
        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<LicenseDb>().IssuedKeys.CountAsync());
    }

    [Fact]
    public async Task A_seat_change_issues_a_new_key_and_a_deletion_ends_access()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1", seats: 10);
        await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));

        var updated = Sub("sub_1", seats: 30);
        await SendAsync(server, Event("evt_2", "customer.subscription.updated", SubJson(updated)));
        Assert.Equal(2, server.Mail.Sent.Count);
        Assert.True(Wb2Verifier.TryVerify(server.Mail.Sent[1].Body.Split('\n').Single(l => l.StartsWith("WB2.")), server.PublicKeyPem, out var claims));
        Assert.Equal(30, claims!.Seats);

        // The same state again is not a change.
        await SendAsync(server, Event("evt_3", "customer.subscription.updated", SubJson(updated)));
        Assert.Equal(2, server.Mail.Sent.Count);

        await SendAsync(server, Event("evt_4", "customer.subscription.deleted", SubJson(updated)));
        await using var scope = server.Services.CreateAsyncScope();
        Assert.Equal(SubscriptionStatus.Canceled, (await scope.ServiceProvider.GetRequiredService<LicenseDb>().Subscriptions.SingleAsync()).Status);
    }

    [Fact]
    public async Task Payment_failure_marks_past_due_and_payment_renews_with_a_new_key()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1", end: DateTime.UtcNow.Date.AddMonths(1));
        await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));

        await SendAsync(server, Event("evt_2", "invoice.payment_failed", new { subscription = "sub_1" }));
        await using (var scope = server.Services.CreateAsyncScope())
            Assert.Equal(SubscriptionStatus.PastDue, (await scope.ServiceProvider.GetRequiredService<LicenseDb>().Subscriptions.SingleAsync()).Status);

        // Stripe reports the renewal: active again, paid through two months from now.
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1", end: DateTime.UtcNow.Date.AddMonths(2));
        await SendAsync(server, Event("evt_3", "invoice.paid", new { subscription = "sub_1" }));
        Assert.Equal(2, server.Mail.Sent.Count);
        await using var scope2 = server.Services.CreateAsyncScope();
        Assert.Equal(SubscriptionStatus.Active, (await scope2.ServiceProvider.GetRequiredService<LicenseDb>().Subscriptions.SingleAsync()).Status);
    }

    [Fact]
    public async Task The_newer_api_shape_with_the_period_on_the_items_and_the_invoice_parent_is_understood()
    {
        using var server = new TestServer();
        var end = DateTimeOffset.UtcNow.AddMonths(1).ToUnixTimeSeconds();
        var sub = JsonDocument.Parse($$$"""
            {"id":"sub_9","customer":"cus_9","status":"active","cancel_at_period_end":false,
             "items":{"data":[{"price":{"id":"{{{TestServer.SeatPrice}}}"},"quantity":4,"current_period_start":{{{end - 2592000}}},"current_period_end":{{{end}}}},
                              {"price":{"id":"{{{TestServer.BillingPrice}}}"},"quantity":1,"current_period_start":{{{end - 2592000}}},"current_period_end":{{{end}}}}]}}
            """).RootElement;
        var parsed = StripeSubscription.FromJson(sub);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(end).UtcDateTime, parsed.PeriodEnd);
        server.Stripe.Subscriptions["sub_9"] = parsed;
        server.Stripe.Emails["cus_9"] = "late@corp.test";

        var res = await SendAsync(server, Event("evt_9", "invoice.paid", new { parent = new { subscription_details = new { subscription = "sub_9" } } }));
        Assert.Contains("subscription created", await res.Content.ReadAsStringAsync());
        Assert.Equal("late@corp.test", Assert.Single(server.Mail.Sent).To);
    }

    [Fact]
    public async Task Unmapped_prices_and_unrelated_events_are_ignored()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_x"] = new("sub_x", "cus_x", "active", DateTime.UtcNow, DateTime.UtcNow.AddMonths(1), false, [new("price_unknown", 3)]);
        var res = await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout("sub_x")));
        Assert.Contains("ignored", await res.Content.ReadAsStringAsync());
        Assert.Contains("ignored", await (await SendAsync(server, Event("evt_2", "charge.refunded", new { id = "ch_1" }))).Content.ReadAsStringAsync());
        Assert.Empty(server.Mail.Sent);
        await using var scope = server.Services.CreateAsyncScope();
        Assert.Equal(0, await scope.ServiceProvider.GetRequiredService<LicenseDb>().Subscriptions.CountAsync());
    }

    [Fact]
    public async Task A_stripe_checkout_links_to_a_customer_an_admin_already_created()
    {
        using var server = new TestServer();
        var existing = await server.PostAsync(server.Admin(), "/admin/api/customers", new { name = "Corp Inc", email = "buyer@corp.test" });
        server.Stripe.Subscriptions["sub_1"] = Sub("sub_1");
        await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDb>();
        var only = await db.Customers.SingleAsync();
        Assert.Equal(existing.GetProperty("id").GetGuid(), only.Id);
        Assert.Equal("cus_1", only.StripeCustomerId);
    }

    [Fact]
    public async Task A_ModuleSeats_price_gives_just_that_module_its_own_seat_count()
    {
        using var server = new TestServer();
        // 25 base seats, billing, plus accounting sold as 10 seats: accounting differs from seats, billing doesn't.
        server.Stripe.Subscriptions["sub_1"] = new("sub_1", "cus_1", "active", DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddMonths(1), false,
            [new(TestServer.SeatPrice, 25), new(TestServer.BillingPrice, 1), new(TestServer.AccountingSeatsPrice, 10)]);
        await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));
        Assert.True(Wb2Verifier.TryVerify(Assert.Single(server.Mail.Sent).Body.Split('\n').Single(l => l.StartsWith("WB2.")), server.PublicKeyPem, out var claims));
        Assert.Equal(25, claims!.Seats);
        Assert.Equal(["accounting", "billing"], claims.Modules);
        Assert.Equal(new Dictionary<string, int> { ["accounting"] = 10 }, claims.ModuleSeats);
    }

    [Fact]
    public async Task Without_base_seats_the_largest_module_count_is_the_seats()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_1"] = new("sub_1", "cus_1", "active", DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddMonths(1), false,
            [new(TestServer.AccountingSeatsPrice, 10), new(TestServer.CaptureSeatsPrice, 4)]);
        await SendAsync(server, Event("evt_1", "checkout.session.completed", Checkout()));
        Assert.True(Wb2Verifier.TryVerify(Assert.Single(server.Mail.Sent).Body.Split('\n').Single(l => l.StartsWith("WB2.")), server.PublicKeyPem, out var claims));
        Assert.Equal(10, claims!.Seats);
        Assert.Equal(new Dictionary<string, int> { ["capture"] = 4 }, claims.ModuleSeats);
    }

    private static object SubJson(StripeSubscription s) => new
    {
        id = s.Id, customer = s.CustomerId, status = s.Status, cancel_at_period_end = s.CancelAtPeriodEnd,
        current_period_start = new DateTimeOffset(s.PeriodStart!.Value).ToUnixTimeSeconds(), current_period_end = new DateTimeOffset(s.PeriodEnd!.Value).ToUnixTimeSeconds(),
        items = new { data = s.Items.Select(i => new { price = new { id = i.PriceId }, quantity = i.Quantity }) },
    };
}

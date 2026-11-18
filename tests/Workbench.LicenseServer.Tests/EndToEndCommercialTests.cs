using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Workbench.Application.Licensing;
using Workbench.Domain.Licensing;
using Workbench.LicenseServer.Stripe;

namespace Workbench.LicenseServer.Tests;

/// <summary>
/// The commercial path end to end: what the license server issues must be accepted by the product's own key reader (not the
/// independent test reader) and must mean the same modules, seats and dates on both sides. Stripe is a fake; nothing leaves the process.
/// </summary>
public class EndToEndCommercialTests
{
    private static LicenseClaims Client(string key, TestServer server)
    {
        Assert.True(LicenseKeys.TryVerify(key, server.PublicKeyPem, out var claims, out var problem), problem);
        return claims!;
    }

    private static async Task<HttpResponseMessage> WebhookAsync(TestServer s, string type, object obj, string id)
    {
        var payload = JsonSerializer.Serialize(new { id, type, data = new { @object = obj } });
        var req = new HttpRequestMessage(HttpMethod.Post, "/stripe/webhook") { Content = new StringContent(payload, Encoding.UTF8, "application/json") };
        req.Headers.Add("Stripe-Signature", StripeSignature.Header(payload, TestServer.WebhookSecret, DateTimeOffset.UtcNow));
        return await s.CreateClient().SendAsync(req);
    }

    [Fact]
    public async Task A_key_the_server_issues_is_read_by_the_product_with_the_same_modules_seats_and_dates()
    {
        using var server = new TestServer();
        var (_, _, licenseId, key) = await server.NewSubscriptionAsync(["billing", "accounting"], seats: 25, light: 4);
        var c = Client(key, server);
        Assert.Equal(licenseId, c.LicenseId);
        Assert.Equal("Acme Defense", c.Licensee);
        Assert.Equal(25, c.Seats);
        Assert.Equal(4, c.LightSeats);
        Assert.True(c.IncludesModule(ModuleIds.Core));
        Assert.True(c.IncludesModule("billing"));
        Assert.True(c.IncludesModule("accounting"));
        Assert.False(c.IncludesModule("capture"));
        Assert.False(c.IncludesModule("govcon"));
        Assert.False(c.Trial);
    }

    [Fact]
    public async Task Every_module_the_server_can_sell_exists_in_the_products_catalog()
    {
        using var server = new TestServer();
        server.Stripe.Subscriptions["sub_all"] = new("sub_all", "cus_all", "active", DateTime.UtcNow.Date, DateTime.UtcNow.Date.AddMonths(1), false,
            [new(TestServer.SeatPrice, 5), new(TestServer.BillingPrice, 1), new(TestServer.AccountingSeatsPrice, 5), new(TestServer.CaptureSeatsPrice, 5), new(TestServer.BundlePrice, 1)]);
        var res = await WebhookAsync(server, "checkout.session.completed",
            new { mode = "subscription", subscription = "sub_all", customer = "cus_all", customer_details = new { email = "all@corp.test", name = "All Corp" } }, "evt_all");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var key = server.Mail.Sent.Single().Body.Split('\n').Single(l => l.StartsWith("WB2."));
        var claims = Client(key, server);
        var known = ModuleCatalog.Default.Select(m => m.Id).ToHashSet();
        Assert.NotEmpty(claims.Modules!);
        foreach (var m in claims.Modules!) Assert.Contains(m, known);
        foreach (var m in new[] { "billing", "accounting", "capture", "govcon", "enterprise" }) Assert.True(claims.IncludesModule(m), m);
    }

    [Fact]
    public void The_module_ids_the_server_sells_are_the_catalogs_paid_modules_and_the_eight_in_the_private_module_repository()
    {
        // The license server's list and the product's catalog are kept by hand in two places; this keeps them in step.
        Assert.Equal(ModuleCatalog.AllIds.Where(id => id != "core").Order(), Workbench.LicenseServer.Licensing.Modules.All.Order());
        // The eight modules that load as plugins (docs/operations/module-plugins.md), each of which a key can list; "ai" is reserved and loads nothing.
        string[] plugins = ["accounting", "billing", "capture", "enterprise", "govcon", "materials", "projectcontrols", "workforce"];
        Assert.All(plugins, id => Assert.Contains(id, Workbench.LicenseServer.Licensing.Modules.All));
        Assert.Equal(plugins, Workbench.LicenseServer.Licensing.Modules.Normalize(plugins.Reverse()));
        // The retired ids are refused when issuing and read as what they became when stored.
        Assert.Throws<ArgumentException>(() => Workbench.LicenseServer.Licensing.Modules.Normalize(["finance"]));
        Assert.Equal(["accounting", "billing"], Workbench.LicenseServer.Licensing.Modules.Parse("finance"));
        Assert.Equal(["enterprise"], Workbench.LicenseServer.Licensing.Modules.Parse("controls"));
    }

    [Fact]
    public async Task Activate_then_refresh_gives_the_product_a_key_it_accepts_and_a_seat_change_arrives()
    {
        using var server = new TestServer();
        var (_, subId, licenseId, _) = await server.NewSubscriptionAsync(["billing", "accounting"], seats: 10);
        var http = server.CreateClient();
        var act = await http.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "prod" });
        var actKey = JsonDocument.Parse(await act.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString();
        var first = Client(actKey!, server);
        Assert.Equal(10, first.Seats);
        Assert.True(first.Expires().CompareTo(first.IssuedOn!.Value) > 0);

        await server.Admin().PatchAsJsonAsync($"/admin/api/subscriptions/{subId}", new { seats = 40, moduleSeats = new Dictionary<string, int> { ["accounting"] = 12 } });
        var refresh = await http.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "prod" });
        var next = Client(JsonDocument.Parse(await refresh.Content.ReadAsStringAsync()).RootElement.GetProperty("key").GetString()!, server);
        Assert.Equal(40, next.Seats);
        Assert.Equal(12, next.SeatsFor("accounting"));
        Assert.Equal(40, next.SeatsFor("billing"));
        Assert.Equal(licenseId, next.LicenseId);
    }

    [Fact]
    public async Task A_key_signed_by_another_server_is_refused_by_the_product()
    {
        using var server = new TestServer();
        using var other = new TestServer();
        var (_, _, _, key) = await server.NewSubscriptionAsync(["billing"]);
        Assert.False(LicenseKeys.TryVerify(key, other.PublicKeyPem, out _, out var problem));
        Assert.False(string.IsNullOrEmpty(problem));
        // A tampered payload is refused too.
        var parts = key.Split('.');
        var forged = $"{parts[0]}.{parts[1][..^2]}AA.{parts[2]}";
        Assert.False(LicenseKeys.TryVerify(forged, server.PublicKeyPem, out _, out _));
    }
}

internal static class ClaimsExt
{
    public static DateOnly Expires(this LicenseClaims c) => c.ExpiresOn;
}

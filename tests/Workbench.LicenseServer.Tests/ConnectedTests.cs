using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.LicenseServer.Data;

namespace Workbench.LicenseServer.Tests;

public class ConnectedTests
{
    private static async Task<JsonElement> ReadAsync(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement.Clone();

    [Fact]
    public async Task Activate_and_refresh_return_a_short_lived_key_and_record_the_install()
    {
        using var server = new TestServer();
        var (_, _, licenseId, portalKey) = await server.NewSubscriptionAsync(["billing"], seats: 10, light: 2);
        var c = server.CreateClient();

        var act = await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "prod-1", seatsInUse = 7, lightSeatsInUse = 1, version = "1.4.0" });
        Assert.Equal(HttpStatusCode.OK, act.StatusCode);
        var body = await ReadAsync(act);
        Assert.True(Wb2Verifier.TryVerify(body.GetProperty("key").GetString(), server.PublicKeyPem, out var claims));
        Assert.Equal(licenseId, claims!.LicenseId);
        Assert.Equal(["billing"], claims.Modules);
        Assert.Equal(2, claims.LightSeats);
        Assert.Equal(claims.IssuedAt.AddDays(14), claims.Expires);   // short-lived
        Assert.True(Wb2Verifier.TryVerify(portalKey, server.PublicKeyPem, out var longer));
        Assert.True(longer!.Expires > claims.Expires);                 // the portal key lasts the term
        Assert.Equal(0, body.GetProperty("warnings").GetArrayLength());

        var refresh = await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "prod-1", seatsInUse = 14, lightSeatsInUse = 0 });
        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
        Assert.Contains("14 full seats", (await ReadAsync(refresh)).GetProperty("warnings")[0].GetString());

        await using var scope = server.Services.CreateAsyncScope();
        var a = await scope.ServiceProvider.GetRequiredService<LicenseDb>().Activations.SingleAsync();
        Assert.Equal("prod-1", a.InstallId);
        Assert.Equal(14, a.SeatsReported);
        Assert.Equal("1.4.0", a.Version);
    }

    [Fact]
    public async Task A_refresh_picks_up_a_seat_change_straight_away()
    {
        using var server = new TestServer();
        var (_, subId, licenseId, _) = await server.NewSubscriptionAsync(["billing"], seats: 10);
        var c = server.CreateClient();
        await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "i1" });
        await server.Admin().PatchAsJsonAsync($"/admin/api/subscriptions/{subId}", new { seats = 50 });
        var body = await ReadAsync(await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" }));
        Assert.Equal(50, body.GetProperty("seats").GetInt32());
    }

    [Fact]
    public async Task Refresh_without_activation_and_unknown_licenses_are_404()
    {
        using var server = new TestServer();
        var (_, _, licenseId, _) = await server.NewSubscriptionAsync(["billing"]);
        var c = server.CreateClient();
        var r = await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "never-activated" });
        Assert.Equal(HttpStatusCode.NotFound, r.StatusCode);
        Assert.Equal("not_activated", (await ReadAsync(r)).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync("/v1/activate", new { licenseId = Guid.NewGuid(), installId = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "" })).StatusCode);
    }

    [Fact]
    public async Task A_lapsed_or_canceled_subscription_gets_402_with_a_reason()
    {
        using var server = new TestServer();
        var (_, subId, licenseId, _) = await server.NewSubscriptionAsync(["billing"]);
        var c = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "i1" })).StatusCode);

        // Past due is still fine inside the paid-through date plus grace.
        await SetAsync(server, subId, s => s.Status = SubscriptionStatus.PastDue);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" })).StatusCode);

        // Beyond the grace days it lapses.
        await SetAsync(server, subId, s => s.TermEndUtc = DateTime.UtcNow.AddDays(-30));
        var lapsed = await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" });
        Assert.Equal(HttpStatusCode.PaymentRequired, lapsed.StatusCode);
        Assert.Equal("subscription_lapsed", (await ReadAsync(lapsed)).GetProperty("reason").GetString());

        await SetAsync(server, subId, s => { s.TermEndUtc = DateTime.UtcNow.AddDays(30); s.Status = SubscriptionStatus.Canceled; });
        var canceled = await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" });
        Assert.Equal(HttpStatusCode.PaymentRequired, canceled.StatusCode);
        Assert.Equal("subscription_canceled", (await ReadAsync(canceled)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task A_revoked_license_is_refused()
    {
        using var server = new TestServer();
        var (_, _, licenseId, _) = await server.NewSubscriptionAsync(["billing"]);
        var c = server.CreateClient();
        await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "i1" });
        await server.Admin().PostAsJsonAsync($"/admin/api/licenses/{licenseId}/revoke", new { reason = "fraud" });
        var r = await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" });
        Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        Assert.Equal("license_revoked", (await ReadAsync(r)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task Installs_are_capped_but_the_same_install_can_reactivate()
    {
        using var server = new TestServer(new() { ["Keys:MaxInstalls"] = "2" });
        var (_, _, licenseId, _) = await server.NewSubscriptionAsync(["billing"]);
        var c = server.CreateClient();
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "a" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "b" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "a" })).StatusCode);
        var third = await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "c" });
        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);
        Assert.Equal("too_many_installs", (await ReadAsync(third)).GetProperty("reason").GetString());
    }

    [Fact]
    public async Task The_connected_endpoints_are_rate_limited()
    {
        using var server = new TestServer(new() { ["RateLimits:ConnectedPerMinute"] = "3" });
        var c = server.CreateClient();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++) codes.Add((await c.PostAsJsonAsync("/v1/refresh", new { licenseId = Guid.NewGuid(), installId = "x" })).StatusCode);
        Assert.Equal([HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests, HttpStatusCode.TooManyRequests], codes);
    }

    private static async Task SetAsync(TestServer server, Guid subId, Action<Subscription> edit)
    {
        await using var scope = server.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDb>();
        var s = await db.Subscriptions.SingleAsync(x => x.Id == subId);
        edit(s);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_refresh_carries_per_module_seats_into_the_key_and_the_response()
    {
        using var server = new TestServer();
        var (_, subId, licenseId, _) = await server.NewSubscriptionAsync(["billing", "accounting"], seats: 25);
        var c = server.CreateClient();
        await c.PostAsJsonAsync("/v1/activate", new { licenseId, installId = "i1" });
        await server.Admin().PatchAsJsonAsync($"/admin/api/subscriptions/{subId}", new { moduleSeats = new Dictionary<string, int> { ["accounting"] = 10, ["billing"] = 25 } });
        var body = await ReadAsync(await c.PostAsJsonAsync("/v1/refresh", new { licenseId, installId = "i1" }));
        Assert.Equal(10, body.GetProperty("moduleSeats").GetProperty("accounting").GetInt32());
        Assert.False(body.GetProperty("moduleSeats").TryGetProperty("billing", out _)); // equal to seats: not listed
        Assert.True(Wb2Verifier.TryVerify(body.GetProperty("key").GetString(), server.PublicKeyPem, out var claims));
        Assert.Equal(new Dictionary<string, int> { ["accounting"] = 10 }, claims!.ModuleSeats);
    }
}

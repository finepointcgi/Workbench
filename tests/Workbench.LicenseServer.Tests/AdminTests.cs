using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Workbench.LicenseServer.Tests;

public class AdminTests
{
    [Fact]
    public async Task The_admin_api_needs_the_key()
    {
        using var server = new TestServer();
        var anon = server.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/admin/api/customers")).StatusCode);
        anon.DefaultRequestHeaders.Add("X-Admin-Key", "wrong-wrong-wrong-wrong-wrong");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/admin/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await server.Admin().GetAsync("/admin/api/customers")).StatusCode);

        var bearer = server.CreateClient();
        bearer.DefaultRequestHeaders.Authorization = new("Bearer", TestServer.AdminKey);
        Assert.Equal(HttpStatusCode.OK, (await bearer.GetAsync("/admin/api/customers")).StatusCode);
    }

    [Fact]
    public async Task The_admin_api_is_off_without_a_configured_key()
    {
        using var server = new TestServer(new() { ["Admin:ApiKey"] = "" });
        var c = server.CreateClient();
        c.DefaultRequestHeaders.Add("X-Admin-Key", "");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await c.GetAsync("/admin/api/customers")).StatusCode);
    }

    [Fact]
    public async Task Customer_subscription_extend_cancel_and_history()
    {
        using var server = new TestServer();
        var admin = server.Admin();
        var (customerId, subId, licenseId, _) = await server.NewSubscriptionAsync(["billing"], months: 6);

        var dup = await admin.PostAsJsonAsync("/admin/api/customers", new { name = "Again", email = "OPS@acme.test" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);
        var bad = await admin.PostAsJsonAsync($"/admin/api/customers/{customerId}/subscriptions", new { modules = new[] { "payroll" }, seats = 1 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        var changed = await admin.PatchAsJsonAsync($"/admin/api/subscriptions/{subId}", new { seats = 25, modules = new[] { "billing", "capture" } });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        var extended = await server.PostAsync(admin, $"/admin/api/subscriptions/{subId}/extend", new { months = 12 });
        Assert.True(Wb2Verifier.TryVerify(extended.GetProperty("key").GetString(), server.PublicKeyPem, out var claims));
        Assert.Equal(25, claims!.Seats);
        Assert.Equal(["billing", "capture"], claims.Modules);
        Assert.True(claims.Expires > claims.IssuedAt.AddMonths(17));

        var history = JsonDocument.Parse(await admin.GetStringAsync($"/admin/api/licenses/{licenseId}/history")).RootElement;
        Assert.Equal(3, history.GetProperty("keys").GetArrayLength());

        await server.PostAsync(admin, $"/admin/api/subscriptions/{subId}/cancel", new { });
        var after = await admin.PostAsJsonAsync($"/admin/api/subscriptions/{subId}/reissue", new { email = false });
        Assert.Equal(HttpStatusCode.Conflict, after.StatusCode);
    }

    [Fact]
    public async Task An_offline_key_is_long_lived_and_can_be_revoked()
    {
        using var server = new TestServer();
        var admin = server.Admin();
        var customer = await server.PostAsync(admin, "/admin/api/customers", new { name = "Fort Secure", email = "it@fort.test" });
        var offline = await server.PostAsync(admin, "/admin/api/licenses/offline",
            new { customerId = customer.GetProperty("id").GetGuid(), modules = new[] { "govcon", "enterprise" }, seats = 40, days = 365 });
        var licenseId = offline.GetProperty("licenseId").GetGuid();
        Assert.True(Wb2Verifier.TryVerify(offline.GetProperty("key").GetString(), server.PublicKeyPem, out var claims));
        Assert.Equal("Fort Secure", claims!.Licensee);
        Assert.Equal(claims.IssuedAt.AddDays(365), claims.Expires);
        Assert.Equal(40, claims.Seats);

        // Offline keys don't connect.
        var connect = await server.CreateClient().PostAsJsonAsync("/v1/activate", new { licenseId, installId = "air-gap" });
        Assert.Equal(HttpStatusCode.Conflict, connect.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await admin.PostAsJsonAsync($"/admin/api/licenses/{licenseId}/revoke", new { reason = "contract ended" })).StatusCode);
        var list = JsonDocument.Parse(await admin.GetStringAsync("/admin/api/revocations")).RootElement;
        Assert.Equal(licenseId, list[0].GetProperty("licenseId").GetGuid());
        var publicList = JsonDocument.Parse(await server.CreateClient().GetStringAsync("/v1/revocations")).RootElement;
        Assert.Equal(licenseId, publicList[0].GetGuid());
    }

    [Fact]
    public async Task The_admin_page_is_served()
    {
        using var server = new TestServer();
        var html = await server.CreateClient().GetStringAsync("/admin/");
        Assert.Contains("License admin", html);
    }
}

internal static class HttpExtensions
{
    public static Task<HttpResponseMessage> PatchAsJsonAsync<T>(this HttpClient c, string url, T body) =>
        c.PatchAsync(url, JsonContent.Create(body));
}

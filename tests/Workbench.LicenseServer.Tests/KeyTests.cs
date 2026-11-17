using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using Workbench.LicenseServer.Licensing;

namespace Workbench.LicenseServer.Tests;

public class KeyTests
{
    [Fact]
    public async Task Issued_keys_round_trip_through_an_independent_verifier()
    {
        using var server = new TestServer();
        var (_, _, licenseId, key) = await server.NewSubscriptionAsync(["billing", "govcon"], seats: 12, light: 3, name: "Round Trip Inc");

        Assert.StartsWith("WB2.", key);
        Assert.True(Wb2Verifier.TryVerify(key, server.PublicKeyPem, out var claims));
        Assert.Equal(2, claims!.V);
        Assert.Equal(licenseId, claims.LicenseId);
        Assert.Equal("Round Trip Inc", claims.Licensee);
        Assert.Equal(12, claims.Seats);
        Assert.Equal(3, claims.LightSeats);
        Assert.Equal(["billing", "govcon"], claims.Modules);
        Assert.False(claims.Trial);
        Assert.True(claims.Expires > claims.IssuedAt);
    }

    [Fact]
    public async Task Payload_has_exactly_the_specified_fields()
    {
        using var server = new TestServer();
        var (_, _, _, key) = await server.NewSubscriptionAsync(["capture"]);
        using var doc = JsonDocument.Parse(Wb2Verifier.Decode(key.Split('.')[1]));
        Assert.Equal(["v", "licenseId", "licensee", "issuedAt", "expires", "seats", "lightSeats", "modules", "trial"], doc.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task Editing_any_byte_of_the_payload_or_signature_breaks_the_key()
    {
        using var server = new TestServer();
        var (_, _, _, key) = await server.NewSubscriptionAsync(["billing"]);
        var parts = key.Split('.');
        Assert.True(Wb2Verifier.TryVerify(key, server.PublicKeyPem, out _));

        var payload = Wb2Verifier.Decode(parts[1]);
        for (var i = 0; i < payload.Length; i++)
        {
            var copy = (byte[])payload.Clone();
            copy[i] ^= 0x01;
            Assert.False(Wb2Verifier.TryVerify($"WB2.{Wb2Verifier.Encode(copy)}.{parts[2]}", server.PublicKeyPem, out _), $"payload byte {i}");
        }

        var sig = Wb2Verifier.Decode(parts[2]);
        for (var i = 0; i < sig.Length; i++)
        {
            var copy = (byte[])sig.Clone();
            copy[i] ^= 0x01;
            Assert.False(Wb2Verifier.TryVerify($"WB2.{parts[1]}.{Wb2Verifier.Encode(copy)}", server.PublicKeyPem, out _), $"signature byte {i}");
        }

        Assert.False(Wb2Verifier.TryVerify("WB1." + parts[1] + "." + parts[2], server.PublicKeyPem, out _));
        Assert.False(Wb2Verifier.TryVerify(null, server.PublicKeyPem, out _));
    }

    [Fact]
    public async Task A_key_is_refused_under_another_public_key()
    {
        using var server = new TestServer();
        using var other = new TestServer();
        var (_, _, _, key) = await server.NewSubscriptionAsync(["billing"]);
        Assert.False(Wb2Verifier.TryVerify(key, other.PublicKeyPem, out _));
    }

    [Fact]
    public void Modules_are_validated_and_core_is_implied()
    {
        Assert.Equal(["billing", "capture"], Modules.Normalize(["Billing", "core", "capture", "billing", " "]));
        Assert.Throws<ArgumentException>(() => Modules.Normalize(["payroll"]));
    }

    [Fact]
    public void The_server_refuses_to_start_without_a_signing_key()
    {
        var o = Options.Create(new SigningOptions());
        Assert.Throws<InvalidOperationException>(() => new LicenseSigner(o));
    }

    [Fact]
    public async Task The_public_key_endpoint_matches_the_signing_key()
    {
        using var server = new TestServer();
        var pem = await server.CreateClient().GetStringAsync("/v1/public-key");
        Assert.Equal(server.PublicKeyPem.Trim(), pem.Trim());
        Assert.Equal(server.PublicKeyPem.Trim(), server.Services.GetRequiredService<ILicenseSigner>().PublicKeyPem.Trim());
    }

    private static KeyClaims Claims(int seats, string[] modules, Dictionary<string, int>? moduleSeats) =>
        new(Guid.Parse("11111111-2222-3333-4444-555555555555"), "Golden Co", new DateOnly(2027, 1, 1), new DateOnly(2028, 1, 1), seats, 10, modules, false, moduleSeats);

    [Fact]
    public void Golden_payload_with_moduleSeats_matches_the_spec_byte_for_byte()
    {
        var payload = System.Text.Encoding.UTF8.GetString(LicenseSigner.BuildPayload(
            Claims(25, ["accounting", "billing"], new() { ["billing"] = 5, ["accounting"] = 10 })));
        Assert.Equal("{\"v\":2,\"licenseId\":\"11111111-2222-3333-4444-555555555555\",\"licensee\":\"Golden Co\",\"issuedAt\":\"2027-01-01\",\"expires\":\"2028-01-01\","
            + "\"seats\":25,\"lightSeats\":10,\"modules\":[\"accounting\",\"billing\"],\"moduleSeats\":{\"accounting\":10,\"billing\":5},\"trial\":false}", payload);
    }

    [Fact]
    public void moduleSeats_only_lists_modules_in_the_key_whose_seats_differ()
    {
        string Json(KeyClaims c) => System.Text.Encoding.UTF8.GetString(LicenseSigner.BuildPayload(c));
        // Equal to seats, or for a module that isn't sold: nothing to say, so the member is absent.
        Assert.DoesNotContain("moduleSeats", Json(Claims(25, ["billing"], new() { ["billing"] = 25, ["capture"] = 3 })));
        Assert.DoesNotContain("moduleSeats", Json(Claims(25, ["billing"], null)));
        var json = Json(Claims(25, ["billing", "capture"], new() { ["billing"] = 25, ["capture"] = 3, ["ai"] = 9 }));
        Assert.Contains("\"moduleSeats\":{\"capture\":3}", json);
    }

    [Fact]
    public async Task A_signed_key_with_moduleSeats_verifies_and_editing_the_member_breaks_it()
    {
        using var server = new TestServer();
        var admin = server.Admin();
        var customer = await server.PostAsync(admin, "/admin/api/customers", new { name = "Split Seats", email = "s@split.test" });
        var offline = await server.PostAsync(admin, "/admin/api/licenses/offline", new
        {
            customerId = customer.GetProperty("id").GetGuid(), modules = new[] { "billing", "accounting" }, seats = 25, lightSeats = 10,
            moduleSeats = new Dictionary<string, int> { ["accounting"] = 10, ["billing"] = 5 }, days = 365,
        });
        var key = offline.GetProperty("key").GetString()!;
        Assert.True(Wb2Verifier.TryVerify(key, server.PublicKeyPem, out var claims));
        Assert.Equal(new Dictionary<string, int> { ["accounting"] = 10, ["billing"] = 5 }, claims!.ModuleSeats);

        var parts = key.Split('.');
        var edited = System.Text.Encoding.UTF8.GetString(Wb2Verifier.Decode(parts[1])).Replace("\"billing\":5", "\"billing\":6");
        Assert.False(Wb2Verifier.TryVerify($"WB2.{Wb2Verifier.Encode(System.Text.Encoding.UTF8.GetBytes(edited))}.{parts[2]}", server.PublicKeyPem, out _));
    }

    [Fact]
    public async Task Retired_module_ids_are_no_longer_issued()
    {
        using var server = new TestServer();
        var admin = server.Admin();
        var customer = await server.PostAsync(admin, "/admin/api/customers", new { name = "Old Ids", email = "o@old.test" });
        foreach (var old in new[] { "finance", "controls" })
        {
            var r = await admin.PostAsJsonAsync($"/admin/api/customers/{customer.GetProperty("id").GetGuid()}/subscriptions", new { modules = new[] { old }, seats = 1 });
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, r.StatusCode);
        }
        Assert.Equal(new[] { "ai", "materials", "projectcontrols" }, Modules.Normalize(["ai", "projectcontrols", "materials"]));
        Assert.Throws<ArgumentException>(() => Modules.Normalize(["controls"]));
    }
}

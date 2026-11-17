using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Licensing;

namespace Workbench.LicenseServer.Tests;

public class KeyTests
{
    [Fact]
    public async Task Issued_keys_round_trip_through_an_independent_verifier()
    {
        using var server = new TestServer();
        var (_, _, licenseId, key) = await server.NewSubscriptionAsync(["finance", "govcon"], seats: 12, light: 3, name: "Round Trip Inc");

        Assert.StartsWith("WB2.", key);
        Assert.True(Wb2Verifier.TryVerify(key, server.PublicKeyPem, out var claims));
        Assert.Equal(2, claims!.V);
        Assert.Equal(licenseId, claims.LicenseId);
        Assert.Equal("Round Trip Inc", claims.Licensee);
        Assert.Equal(12, claims.Seats);
        Assert.Equal(3, claims.LightSeats);
        Assert.Equal(["finance", "govcon"], claims.Modules);
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
        var (_, _, _, key) = await server.NewSubscriptionAsync(["finance"]);
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
        var (_, _, _, key) = await server.NewSubscriptionAsync(["finance"]);
        Assert.False(Wb2Verifier.TryVerify(key, other.PublicKeyPem, out _));
    }

    [Fact]
    public void Modules_are_validated_and_core_is_implied()
    {
        Assert.Equal(["capture", "finance"], Modules.Normalize(["Finance", "core", "capture", "finance", " "]));
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
}

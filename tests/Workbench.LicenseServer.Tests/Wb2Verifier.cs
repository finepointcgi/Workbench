using System.Security.Cryptography;
using System.Text.Json;

namespace Workbench.LicenseServer.Tests;

public sealed record Wb2Claims(int V, Guid LicenseId, string Licensee, DateOnly IssuedAt, DateOnly Expires, int Seats, int LightSeats, string[] Modules, bool Trial)
{
    /// <summary>The optional <c>moduleSeats</c> member (module id to seats); empty when absent.</summary>
    public Dictionary<string, int> ModuleSeats { get; init; } = [];
}

/// <summary>
/// An independent reader of the WB2 format, written from the spec only (not from the server's code): this is the shape the in-product
/// parser must accept. <c>WB2.&lt;base64url(payload)&gt;.&lt;base64url(signature)&gt;</c>, ECDSA P-256 / SHA-256 over the payload bytes, IEEE P1363 signature.
/// </summary>
public static class Wb2Verifier
{
    public static bool TryVerify(string? key, string publicKeyPem, out Wb2Claims? claims)
    {
        claims = null;
        var parts = (key ?? "").Trim().Split('.');
        if (parts.Length != 3 || parts[0] != "WB2") return false;
        try
        {
            var payload = Decode(parts[1]);
            var signature = Decode(parts[2]);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) return false;
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            claims = new Wb2Claims(r.GetProperty("v").GetInt32(), Guid.Parse(r.GetProperty("licenseId").GetString()!), r.GetProperty("licensee").GetString()!,
                DateOnly.Parse(r.GetProperty("issuedAt").GetString()!), DateOnly.Parse(r.GetProperty("expires").GetString()!),
                r.GetProperty("seats").GetInt32(), r.GetProperty("lightSeats").GetInt32(),
                r.GetProperty("modules").EnumerateArray().Select(m => m.GetString()!).ToArray(), r.GetProperty("trial").GetBoolean())
            {
                ModuleSeats = r.TryGetProperty("moduleSeats", out var ms) && ms.ValueKind == JsonValueKind.Object
                    ? ms.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number).ToDictionary(p => p.Name, p => p.Value.GetInt32()) : [],
            };
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            return false;
        }
    }

    public static byte[] Decode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    public static string Encode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

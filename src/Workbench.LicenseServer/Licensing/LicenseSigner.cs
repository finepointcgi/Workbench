using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Workbench.LicenseServer.Licensing;

/// <summary>What goes into a WB2 key. <see cref="Modules"/> are normalized (core is implied).</summary>
public sealed record KeyClaims(Guid LicenseId, string Licensee, DateOnly IssuedAt, DateOnly Expires, int Seats, int LightSeats, string[] Modules, bool Trial);

/// <summary>
/// Signs <c>WB2.&lt;base64url(payload JSON)&gt;.&lt;base64url(signature)&gt;</c>: ECDSA P-256 over the exact payload bytes, SHA-256, IEEE P1363
/// fixed-field signature, the same scheme as the WB1 keys made by tools/license.cs.
/// </summary>
public interface ILicenseSigner
{
    string Sign(KeyClaims claims);
    /// <summary>The public half as PEM: what the product embeds to verify keys.</summary>
    string PublicKeyPem { get; }
}

public sealed class LicenseSigner : ILicenseSigner, IDisposable
{
    private readonly ECDsa _key = ECDsa.Create();

    public LicenseSigner(IOptions<SigningOptions> options)
    {
        var o = options.Value;
        var pem = !string.IsNullOrWhiteSpace(o.PrivateKeyPem)
            ? o.PrivateKeyPem.Replace("\\n", "\n")
            : !string.IsNullOrWhiteSpace(o.PrivateKeyPath) ? File.ReadAllText(o.PrivateKeyPath)
            : throw new InvalidOperationException("No signing key: set Signing:PrivateKeyPem (SIGNING__PRIVATEKEYPEM) or Signing:PrivateKeyPath (SIGNING__PRIVATEKEYPATH). See docs/admin-guide/license-server.md.");
        _key.ImportFromPem(pem);
        if (_key.KeySize != 256) throw new InvalidOperationException("The signing key must be an ECDSA P-256 key.");
        PublicKeyPem = _key.ExportSubjectPublicKeyInfoPem();
    }

    public string PublicKeyPem { get; }

    public string Sign(KeyClaims c)
    {
        var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteNumber("v", 2);
            json.WriteString("licenseId", c.LicenseId.ToString());
            json.WriteString("licensee", c.Licensee);
            json.WriteString("issuedAt", c.IssuedAt.ToString("yyyy-MM-dd"));
            json.WriteString("expires", c.Expires.ToString("yyyy-MM-dd"));
            json.WriteNumber("seats", c.Seats);
            json.WriteNumber("lightSeats", c.LightSeats);
            json.WriteStartArray("modules");
            foreach (var m in Modules.Normalize(c.Modules)) json.WriteStringValue(m);
            json.WriteEndArray();
            json.WriteBoolean("trial", c.Trial);
            json.WriteEndObject();
        }

        var payload = buffer.ToArray();
        var sig = _key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return $"WB2.{B64(payload)}.{B64(sig)}";
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose() => _key.Dispose();
}

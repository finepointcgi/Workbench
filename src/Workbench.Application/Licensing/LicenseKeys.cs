using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>What a license key says. <see cref="Seats"/> counts full seats (people with a role beyond Employee).</summary>
public sealed record LicenseClaims(string Licensee, Edition Edition, int Seats, DateOnly ExpiresOn);

/// <summary>Where the server finds the public half of the key pair that signs licenses.</summary>
public sealed class LicenseOptions
{
    public const string SectionName = "License";

    /// <summary>The public key of the key pair kept by the product's owner. Licenses signed by anyone else are refused.</summary>
    public const string DefaultPublicKeyPem = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAExtQ5CVzB/AUYoTQL+RCtAwSQ6R3W
        Iq70rhUbmdKDzfBmynV0NYdZPyM3gNAHnEpqJ86rR4ny6hsBQud+b7WhIw==
        -----END PUBLIC KEY-----
        """;

    /// <summary>
    /// <c>Business</c> turns every Business feature on without a key. For the public demo, development and trials the operator
    /// runs themselves; leave it unset in production so the license decides.
    /// </summary>
    public string? EditionOverride { get; set; }

    public string PublicKeyPem { get; set; } = DefaultPublicKeyPem;

    /// <summary>Days after the expiry date that Business keeps working, with a warning, so a late renewal never stops payroll.</summary>
    public int GraceDays { get; set; } = 14;
}

/// <summary>
/// Checks license keys: <c>WB1.&lt;payload&gt;.&lt;signature&gt;</c>, both base64url, the payload JSON signed with ECDSA P-256.
/// The check is offline, so a self-hosted server needs no connection to anyone.
/// </summary>
public static class LicenseKeys
{
    public static bool TryVerify(string? key, string publicKeyPem, out LicenseClaims? claims, out string? problem)
    {
        claims = null;
        problem = null;
        var parts = (key ?? "").Trim().Split('.');
        if (parts.Length != 3 || parts[0] != "WB1")
        {
            problem = "That doesn't look like a Workbench license key.";
            return false;
        }

        try
        {
            var payload = Decode(parts[1]);
            var signature = Decode(parts[2]);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            if (!ecdsa.VerifyData(payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
            {
                problem = "This license key wasn't issued by Workbench, or it was changed.";
                return false;
            }

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;
            var edition = root.GetProperty("edition").GetString()?.ToLowerInvariant() == "business" ? Edition.Business : Edition.Community;
            claims = new LicenseClaims(
                root.GetProperty("licensee").GetString() ?? "",
                edition,
                root.GetProperty("seats").GetInt32(),
                DateOnly.Parse(root.GetProperty("expires").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException)
        {
            problem = "That license key is damaged. Copy the whole key and try again.";
            return false;
        }
    }

    private static byte[] Decode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

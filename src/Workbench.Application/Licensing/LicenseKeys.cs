using System.Security.Cryptography;
using System.Text.Json;
using Workbench.Domain.Licensing;

namespace Workbench.Application.Licensing;

/// <summary>
/// What a license key says. <see cref="Seats"/> is how many active full people it covers and <see cref="LightSeats"/> how many
/// light ones (people who only enter their own time, expenses and leave). <see cref="Modules"/> is null for a WB1 key, which
/// means every module; a WB2 key lists them (<c>core</c> is always included).
/// </summary>
public sealed record LicenseClaims(
    string Licensee,
    Edition Edition,
    int Seats,
    DateOnly ExpiresOn,
    int LightSeats = 0,
    IReadOnlySet<string>? Modules = null,
    bool Trial = false,
    Guid? LicenseId = null,
    DateOnly? IssuedOn = null,
    int Version = 1)
{
    public bool IncludesModule(string id) => id == ModuleIds.Core || Modules is null || Modules.Contains(id);
}

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

    /// <summary>
    /// The license server a <i>connected</i> install refreshes its key from (<c>https://license.example.com</c>). Empty, the default,
    /// means this install never phones home: it runs on the key it was given, which is how defense and air-gapped sites run.
    /// </summary>
    public string? ServerUrl { get; set; }

    /// <summary>Hours between refreshes while they work.</summary>
    public int RefreshIntervalHours { get; set; } = 24;

    /// <summary>Days without a successful refresh before administrators see a banner. Entitlements are not changed by it; the key's own expiry governs.</summary>
    public int RefreshStaleDays { get; set; } = 7;

    public int RefreshTimeoutSeconds { get; set; } = 30;

    /// <summary>Days after the expiry date that Business keeps working, with a warning, so a late renewal never stops payroll.</summary>
    public int GraceDays { get; set; } = 14;

    /// <summary>The modules and their Enforced flags. Defaults to <see cref="ModuleCatalog.Default"/>; tests replace it to prove a flag works when flipped.</summary>
    public IReadOnlyList<ModuleInfo> Modules { get; set; } = ModuleCatalog.Default;
}

/// <summary>
/// Checks license keys: <c>WB1.&lt;payload&gt;.&lt;signature&gt;</c> (every module, per-person seats) or <c>WB2.&lt;payload&gt;.&lt;signature&gt;</c>
/// (modules, full and light seats, trial), both base64url, the payload JSON signed with ECDSA P-256 (SHA-256, IEEE P1363).
/// The check is offline, so a self-hosted server needs no connection to anyone.
/// </summary>
public static class LicenseKeys
{
    public static bool TryVerify(string? key, string publicKeyPem, out LicenseClaims? claims, out string? problem)
    {
        claims = null;
        problem = null;
        var parts = (key ?? "").Trim().Split('.');
        if (parts.Length != 3 || parts[0] is not ("WB1" or "WB2"))
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
            claims = parts[0] == "WB2" ? ReadV2(root) : ReadV1(root);
            return true;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or CryptographicException or KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            problem = "That license key is damaged. Copy the whole key and try again.";
            return false;
        }
    }

    private static LicenseClaims ReadV1(JsonElement root) => new(
        root.GetProperty("licensee").GetString() ?? "",
        root.GetProperty("edition").GetString()?.ToLowerInvariant() == "business" ? Edition.Business : Edition.Community,
        root.GetProperty("seats").GetInt32(),
        ParseDate(root.GetProperty("expires").GetString()!));

    /// <summary>The WB2 payload: <c>v</c> 2, <c>licenseId</c>, <c>licensee</c>, <c>issuedAt</c>, <c>expires</c>, <c>seats</c>, <c>lightSeats</c>, <c>modules</c>, <c>trial</c>.</summary>
    private static LicenseClaims ReadV2(JsonElement root)
    {
        if (root.GetProperty("v").GetInt32() != 2)
        {
            throw new FormatException("Unsupported license version.");
        }

        var modules = root.TryGetProperty("modules", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Select(m => m.GetString()?.Trim().ToLowerInvariant()).Where(m => !string.IsNullOrEmpty(m)).Select(m => m!).ToHashSet()
            : [];
        modules.Remove(ModuleIds.Core);
        modules.IntersectWith(ModuleCatalog.AllIds); // A module this build doesn't know (a newer key) is ignored, not an error.
        modules.Add(ModuleIds.Core);
        return new LicenseClaims(
            root.GetProperty("licensee").GetString() ?? "",
            Edition.Business,
            root.GetProperty("seats").GetInt32(),
            ParseDate(root.GetProperty("expires").GetString()!),
            root.TryGetProperty("lightSeats", out var light) && light.ValueKind == JsonValueKind.Number ? Math.Max(0, light.GetInt32()) : 0,
            modules,
            root.TryGetProperty("trial", out var trial) && trial.ValueKind == JsonValueKind.True,
            Guid.Parse(root.GetProperty("licenseId").GetString()!),
            root.TryGetProperty("issuedAt", out var issued) && issued.GetString() is { } i ? ParseDate(i) : null,
            2);
    }

    /// <summary>An ISO date (<c>2027-11-01</c>); a full timestamp is accepted too and its date used.</summary>
    private static DateOnly ParseDate(string s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d)
            ? d
            : DateOnly.FromDateTime(DateTimeOffset.Parse(s, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);

    private static byte[] Decode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }
}

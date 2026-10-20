using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Workbench.Contracts.Integrations;

/// <summary>
/// How webhook requests are signed: <c>Workbench-Signature: t={unix seconds},v1={hex HMAC-SHA256}</c> where the HMAC key is the
/// endpoint's signing secret (UTF-8) and the signed text is <c>{t}.{raw body}</c>. Receivers recompute it, compare in constant
/// time and refuse timestamps older than a few minutes (replays). See docs/integrations/webhooks.md.
/// </summary>
public static class WebhookSignature
{
    public const string SignatureHeader = "Workbench-Signature";
    public const string EventHeader = "Workbench-Event";
    public const string EventIdHeader = "Workbench-Event-Id";
    public const string DeliveryHeader = "Workbench-Delivery";
    public const string SecretPrefix = "whsec_";

    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    /// <summary>The lower-case hex HMAC-SHA256 of <c>{timestamp}.{body}</c>.</summary>
    public static string Compute(string secret, long timestamp, string body)
    {
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{timestamp.ToString(CultureInfo.InvariantCulture)}.{body}"));
        return Convert.ToHexStringLower(mac);
    }

    /// <summary>The <see cref="SignatureHeader"/> value for a body sent at <paramref name="timestamp"/>.</summary>
    public static string Header(string secret, long timestamp, string body) =>
        $"t={timestamp.ToString(CultureInfo.InvariantCulture)},v1={Compute(secret, timestamp, body)}";

    /// <summary>
    /// True when <paramref name="header"/> carries a <c>v1</c> signature of <paramref name="body"/> made with <paramref name="secret"/>
    /// and its timestamp is within <paramref name="tolerance"/> (default five minutes) of <paramref name="now"/>.
    /// </summary>
    public static bool Verify(string secret, string? header, string body, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(header) || string.IsNullOrEmpty(secret))
        {
            return false;
        }

        long? timestamp = null;
        var signatures = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq <= 0)
            {
                continue;
            }

            var (key, value) = (part[..eq], part[(eq + 1)..]);
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (key == "v1")
            {
                signatures.Add(value);
            }
        }

        if (timestamp is not { } ts || signatures.Count == 0)
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(ts)).Duration() > (tolerance ?? DefaultTolerance))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(secret, ts, body));
        return signatures.Any(s => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s.ToLowerInvariant())));
    }
}

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Workbench.LicenseServer.Stripe;

public sealed class StripeOptions
{
    public const string SectionName = "Stripe";
    /// <summary>The webhook signing secret (whsec_...). Unset = the webhook endpoint answers 503.</summary>
    public string? WebhookSecret { get; set; }
    /// <summary>The API secret key (sk_...), for reading subscriptions and creating customer-portal sessions. Never commit it.</summary>
    public string? SecretKey { get; set; }
    public string ApiBase { get; set; } = "https://api.stripe.com";
    /// <summary>Where Stripe's customer portal sends people back to (the license server's /portal).</summary>
    public string? PortalReturnUrl { get; set; }
    public int ToleranceSeconds { get; set; } = 300;
    /// <summary>Stripe price id to what it sells.</summary>
    public Dictionary<string, PriceMapping> Prices { get; set; } = [];
}

/// <summary>
/// What a Stripe price grants. <c>Role</c> is <c>Seats</c> (quantity x <see cref="SeatsPerUnit"/> full seats), <c>LightSeats</c>
/// (quantity x <see cref="SeatsPerUnit"/> light seats) or <c>Modules</c> (turns <see cref="Modules"/> on, plus the seats it includes once).
/// </summary>
public sealed class PriceMapping
{
    public string Role { get; set; } = "Modules";
    public string[] Modules { get; set; } = [];
    public int SeatsPerUnit { get; set; } = 1;
    public int IncludedSeats { get; set; }
    public int IncludedLightSeats { get; set; }
}

public static class StripeSignature
{
    /// <summary>Checks a <c>Stripe-Signature</c> header (<c>t=...,v1=...</c>): HMAC-SHA256 of "t.payload" with the endpoint secret, within the tolerance.</summary>
    public static bool Verify(string payload, string? header, string secret, DateTimeOffset now, int toleranceSeconds)
    {
        if (string.IsNullOrEmpty(header) || string.IsNullOrEmpty(secret)) return false;
        string? t = null;
        var v1 = new List<string>();
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2) continue;
            if (kv[0].Trim() == "t") t = kv[1].Trim();
            else if (kv[0].Trim() == "v1") v1.Add(kv[1].Trim());
        }
        if (t is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var ts)) return false;
        if (Math.Abs(now.ToUnixTimeSeconds() - ts) > toleranceSeconds) return false;
        var expected = Sign(payload, t, secret);
        return v1.Any(s => CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(s), Encoding.ASCII.GetBytes(expected)));
    }

    public static string Sign(string payload, string timestamp, string secret)
    {
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexStringLower(h.ComputeHash(Encoding.UTF8.GetBytes($"{timestamp}.{payload}")));
    }

    /// <summary>A header value for <paramref name="payload"/>, for tests and the sample-event docs.</summary>
    public static string Header(string payload, string secret, DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},v1={Sign(payload, t, secret)}";
    }
}

public sealed record StripeItem(string PriceId, int Quantity);

/// <summary>The parts of a Stripe subscription object the server uses.</summary>
public sealed record StripeSubscription(string Id, string CustomerId, string Status, DateTime? PeriodStart, DateTime? PeriodEnd,
    bool CancelAtPeriodEnd, IReadOnlyList<StripeItem> Items)
{
    public static StripeSubscription FromJson(JsonElement o)
    {
        var items = new List<StripeItem>();
        DateTime? start = Unix(o, "current_period_start"), end = Unix(o, "current_period_end");
        if (o.TryGetProperty("items", out var its) && its.TryGetProperty("data", out var data))
            foreach (var it in data.EnumerateArray())
            {
                var price = it.TryGetProperty("price", out var p) && p.TryGetProperty("id", out var pid) ? pid.GetString() : null;
                if (price is null) continue;
                items.Add(new(price, it.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qi) ? qi : 1));
                // Newer Stripe API versions keep the period on the items rather than the subscription.
                if (Unix(it, "current_period_end") is { } ie && (end is null || ie > end)) end = ie;
                if (Unix(it, "current_period_start") is { } isr && (start is null || isr < start)) start = isr;
            }
        return new(Str(o, "id") ?? "", Str(o, "customer") ?? "", Str(o, "status") ?? "",
            start, end, o.TryGetProperty("cancel_at_period_end", out var c) && c.ValueKind == JsonValueKind.True, items);
    }

    internal static string? Str(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTime? Unix(JsonElement o, string name) =>
        o.ValueKind == JsonValueKind.Object && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var s)
            ? DateTimeOffset.FromUnixTimeSeconds(s).UtcDateTime : null;
}

/// <summary>The few Stripe API calls the server makes. The real implementation is <see cref="HttpStripeApi"/>; tests use a fake.</summary>
public interface IStripeApi
{
    Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default);
    Task<string?> GetCustomerEmailAsync(string customerId, CancellationToken ct = default);
    /// <summary>A Stripe customer-portal session (change seats, payment method, cancel). Returns its URL.</summary>
    Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default);
}

/// <summary>
/// Plain HTTP against the Stripe REST API (no SDK). NOT VERIFIED against real Stripe: written from the API documentation and exercised only
/// through the <see cref="IStripeApi"/> fake in tests. Try it against a Stripe test-mode key before launch.
/// </summary>
public sealed class HttpStripeApi(HttpClient http, Microsoft.Extensions.Options.IOptions<StripeOptions> options) : IStripeApi
{
    private HttpRequestMessage Req(HttpMethod m, string path, HttpContent? content = null)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.SecretKey)) throw new InvalidOperationException("Stripe:SecretKey is not configured.");
        var r = new HttpRequestMessage(m, o.ApiBase.TrimEnd('/') + path) { Content = content };
        r.Headers.Authorization = new("Bearer", o.SecretKey);
        return r;
    }

    public async Task<StripeSubscription?> GetSubscriptionAsync(string subscriptionId, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, $"/v1/subscriptions/{Uri.EscapeDataString(subscriptionId)}"), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return StripeSubscription.FromJson(doc.RootElement);
    }

    public async Task<string?> GetCustomerEmailAsync(string customerId, CancellationToken ct = default)
    {
        using var res = await http.SendAsync(Req(HttpMethod.Get, $"/v1/customers/{Uri.EscapeDataString(customerId)}"), ct);
        if (res.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return StripeSubscription.Str(doc.RootElement, "email");
    }

    public async Task<string> CreatePortalSessionAsync(string customerId, string returnUrl, CancellationToken ct = default)
    {
        var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["customer"] = customerId, ["return_url"] = returnUrl });
        using var res = await http.SendAsync(Req(HttpMethod.Post, "/v1/billing_portal/sessions", form), ct);
        res.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        return StripeSubscription.Str(doc.RootElement, "url") ?? throw new InvalidOperationException("Stripe returned no portal URL.");
    }
}

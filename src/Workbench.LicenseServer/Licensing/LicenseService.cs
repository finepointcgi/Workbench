using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Workbench.LicenseServer.Data;

namespace Workbench.LicenseServer.Licensing;

/// <summary>Why a connected install can't have a key right now.</summary>
public sealed record Denial(int StatusCode, string Reason, string Message);

/// <summary><see cref="ModuleSeats"/> holds only the modules whose seats differ from <see cref="Seats"/>.</summary>
public sealed record Entitlement(string[] Modules, int Seats, int LightSeats, bool Trial, DateOnly Expires, Denial? Denied)
{
    public IReadOnlyDictionary<string, int> ModuleSeats { get; init; } = new Dictionary<string, int>();
}

/// <summary>Turns subscriptions and licenses into signed keys, and decides whether a license is still entitled.</summary>
public sealed class LicenseService(LicenseDb db, ILicenseSigner signer, IOptions<KeyOptions> keyOptions, TimeProvider clock)
{
    private KeyOptions Keys => keyOptions.Value;
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>
    /// What <paramref name="license"/> may have today. A subscription license is entitled while the subscription is trialing, active or
    /// past due and today is no later than the paid-through date plus the grace days; <paramref name="forConnected"/> shortens the key to
    /// <c>Keys:RefreshDays</c>.
    /// </summary>
    public async Task<Entitlement> EntitlementAsync(License license, bool forConnected, CancellationToken ct = default)
    {
        if (license.RevokedUtc != null)
            return Denied(403, "license_revoked", "This license has been revoked. Contact support.");

        if (license.Kind == LicenseKind.Offline)
        {
            var exp = DateOnly.FromDateTime(license.ExpiresOn ?? clock.GetUtcNow().UtcDateTime);
            return new(Modules.Parse(license.Modules), license.Seats, license.LightSeats, license.IsTrial, exp,
                exp < Today ? new Denial(402, "license_expired", "This license has expired.") : null) { ModuleSeats = Differing(license.ModuleSeats, license.Seats, license.Modules) };
        }

        var sub = await db.Subscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == license.SubscriptionId, ct);
        if (sub is null) return Denied(402, "no_subscription", "This license has no subscription.");

        var through = DateOnly.FromDateTime(sub.TermEndUtc).AddDays(Keys.GraceDays);
        var e = new Entitlement(Modules.Parse(sub.Modules), sub.Seats, sub.LightSeats, sub.IsTrial || sub.Status == SubscriptionStatus.Trialing, through, null)
        { ModuleSeats = Differing(sub.ModuleSeats, sub.Seats, sub.Modules) };
        if (sub.Status == SubscriptionStatus.Canceled)
            return e with { Denied = new(402, "subscription_canceled", "The subscription was canceled.") };
        if (through < Today)
            return e with { Denied = new(402, "subscription_lapsed", "The subscription is not paid through today. Update payment in the customer portal.") };
        if (forConnected)
        {
            var short_ = Today.AddDays(Keys.RefreshDays);
            if (short_ < e.Expires) e = e with { Expires = short_ };
        }
        return e;
    }

    /// <summary>The stored per-module seats that matter: for a module actually sold, and different from the base seats.</summary>
    public static Dictionary<string, int> Differing(string? storedSeats, int seats, string? storedModules)
    {
        var sold = Modules.Parse(storedModules);
        return Modules.ParseSeats(storedSeats).Where(kv => sold.Contains(kv.Key) && kv.Value != seats).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static Entitlement Denied(int code, string reason, string message) =>
        new([], 0, 0, false, DateOnly.MinValue, new Denial(code, reason, message));

    public string Sign(License license, Entitlement e) =>
        signer.Sign(new KeyClaims(license.Id, license.Licensee, Today, e.Expires, e.Seats, e.LightSeats, e.Modules, e.Trial, e.ModuleSeats));

    /// <summary>Signs a key for the license's current entitlement and records it in the history. Throws <see cref="InvalidOperationException"/> when not entitled.</summary>
    public async Task<IssuedKey> IssueAsync(License license, string reason, string issuedBy, CancellationToken ct = default)
    {
        var e = await EntitlementAsync(license, forConnected: false, ct);
        if (e.Denied != null) throw new InvalidOperationException(e.Denied.Message);
        var issued = new IssuedKey
        {
            LicenseId = license.Id, Reason = reason, IssuedBy = issuedBy, Key = Sign(license, e), Modules = Modules.Join(e.Modules),
            Seats = e.Seats, LightSeats = e.LightSeats, ModuleSeats = Modules.JoinSeats(e.ModuleSeats), IsTrial = e.Trial, ExpiresOn = e.Expires.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc),
            IssuedUtc = clock.GetUtcNow().UtcDateTime,
        };
        db.IssuedKeys.Add(issued);
        await db.SaveChangesAsync(ct);
        return issued;
    }

    /// <summary>The most recent key for a license, or a fresh one when it has none or its entitlement changed since.</summary>
    public async Task<IssuedKey?> CurrentKeyAsync(License license, CancellationToken ct = default)
    {
        var e = await EntitlementAsync(license, forConnected: false, ct);
        if (e.Denied != null) return null;
        var last = (await db.IssuedKeys.Where(k => k.LicenseId == license.Id).ToListAsync(ct)).OrderByDescending(k => k.IssuedUtc).FirstOrDefault();
        var same = last != null && last.Modules == Modules.Join(e.Modules) && last.Seats == e.Seats && last.LightSeats == e.LightSeats && last.ModuleSeats == Modules.JoinSeats(e.ModuleSeats)
            && last.IsTrial == e.Trial && DateOnly.FromDateTime(last.ExpiresOn) == e.Expires;
        return same ? last : await IssueAsync(license, "Portal download", "portal", ct);
    }

    public async Task<License> EnsureSubscriptionLicenseAsync(Subscription sub, CancellationToken ct = default)
    {
        var license = await db.Licenses.FirstOrDefaultAsync(l => l.SubscriptionId == sub.Id && l.Kind == LicenseKind.Subscription, ct);
        if (license != null) return license;
        var customer = await db.Customers.AsNoTracking().FirstAsync(c => c.Id == sub.CustomerId, ct);
        license = new License { CustomerId = sub.CustomerId, SubscriptionId = sub.Id, Kind = LicenseKind.Subscription, Licensee = customer.Name, CreatedUtc = clock.GetUtcNow().UtcDateTime };
        db.Licenses.Add(license);
        await db.SaveChangesAsync(ct);
        return license;
    }
}

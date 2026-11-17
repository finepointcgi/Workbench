namespace Workbench.LicenseServer.Data;

public sealed class Customer
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string Name { get; set; } = "";
    /// <summary>Lower-case; the portal signs in by emailing this address.</summary>
    public string Email { get; set; } = "";
    public string? StripeCustomerId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public enum SubscriptionStatus { Trialing, Active, PastDue, Canceled }

public sealed class Subscription
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    /// <summary>Comma-separated module ids (finance,capture,...). Stored as text so it is the same on every provider.</summary>
    public string Modules { get; set; } = "";
    public int Seats { get; set; }
    public int LightSeats { get; set; }
    public SubscriptionStatus Status { get; set; } = SubscriptionStatus.Active;
    public bool IsTrial { get; set; }
    public DateTime TermStartUtc { get; set; }
    public DateTime TermEndUtc { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    /// <summary>"Manual" (created by an admin) or "Stripe".</summary>
    public string Source { get; set; } = "Manual";
    public string? StripeSubscriptionId { get; set; }
    public string? StripeCustomerId { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;
}

public enum LicenseKind
{
    /// <summary>Follows a subscription: the key's modules, seats and expiry are recomputed each time one is issued.</summary>
    Subscription,
    /// <summary>A fixed, long-lived key issued by hand (air-gapped customers). Never refreshed.</summary>
    Offline,
}

/// <summary>A license id, the thing a key carries and that can be revoked. Individual keys issued under it are <see cref="IssuedKey"/>s.</summary>
public sealed class License
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public LicenseKind Kind { get; set; }
    public string Licensee { get; set; } = "";
    // The fixed terms of an Offline license (a Subscription license takes them from its subscription).
    public string Modules { get; set; } = "";
    public int Seats { get; set; }
    public int LightSeats { get; set; }
    public bool IsTrial { get; set; }
    public DateTime? ExpiresOn { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? RevokedUtc { get; set; }
    public string? RevokedReason { get; set; }
}

/// <summary>History: every key the server handed out (admin, Stripe or portal). Connected refreshes are not logged here; see <see cref="Activation"/>.</summary>
public sealed class IssuedKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LicenseId { get; set; }
    public string Reason { get; set; } = "";
    /// <summary>"admin", "stripe" or "portal".</summary>
    public string IssuedBy { get; set; } = "";
    public string Key { get; set; } = "";
    public string Modules { get; set; } = "";
    public int Seats { get; set; }
    public int LightSeats { get; set; }
    public bool IsTrial { get; set; }
    public DateTime ExpiresOn { get; set; }
    public DateTime IssuedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class Activation
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid LicenseId { get; set; }
    public string InstallId { get; set; } = "";
    public DateTime FirstSeenUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public int SeatsReported { get; set; }
    public int LightSeatsReported { get; set; }
    public string? Version { get; set; }
    public DateTime? LastKeyExpiresOn { get; set; }
}

/// <summary>Stripe event ids already handled, so a redelivery does nothing.</summary>
public sealed class StripeEvent
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime ReceivedUtc { get; set; } = DateTime.UtcNow;
    public string Outcome { get; set; } = "";
}

/// <summary>A portal sign-in link. Only the SHA-256 of the token is stored.</summary>
public sealed class LoginToken
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid CustomerId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTime ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}

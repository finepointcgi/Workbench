namespace Workbench.Infrastructure.Tenancy;

/// <summary>
/// The commercial state of a hosted tenant. <see cref="Active"/> and <see cref="PastDue"/> keep working (past due shows a
/// warning until the grace period ends); <see cref="Suspended"/> and <see cref="Cancelled"/> make the organization read-only.
/// This is separate from <see cref="TenantStatus"/>, which switches a tenant off entirely.
/// </summary>
public enum EntitlementStatus
{
    Active,
    PastDue,
    Suspended,
    Cancelled,
}

/// <summary>
/// What a hosted tenant has bought or been given: the plan, modules and seats, the trial and its end, and how it was
/// signed up. One row per tenant, in the catalog. The license server (or the host admin) writes it; the product only reads
/// it, through <see cref="TenantEntitlements"/>. A tenant without a row keeps the old behaviour (its catalog <c>Plan</c>).
/// </summary>
public sealed class TenantEntitlement
{
    public Guid TenantId { get; set; }

    /// <summary>For display and reports: <c>trial</c>, <c>business</c>, ...</summary>
    public string Plan { get; set; } = "business";

    /// <summary>Comma-separated module ids (as in the license server's subscriptions). <c>core</c> is implied.</summary>
    public string Modules { get; set; } = "";

    public int Seats { get; set; }

    public int LightSeats { get; set; }

    public EntitlementStatus Status { get; set; } = EntitlementStatus.Active;

    public bool IsTrial { get; set; }

    /// <summary>When the trial ends (while <see cref="IsTrial"/>).</summary>
    public DateTime? TrialEndsUtc { get; set; }

    /// <summary>End of the paid term (a subscription's current period end). After it plus the grace days the tenant is read-only.</summary>
    public DateTime? PaidThroughUtc { get; set; }

    public DateTime? PastDueSinceUtc { get; set; }

    public DateTime? CancelledAtUtc { get; set; }

    /// <summary>Who wrote it last: <c>signup</c>, <c>host</c> or <c>license-server</c>.</summary>
    public string Source { get; set; } = "host";

    /// <summary>The Stripe subscription id (from the license server), for support.</summary>
    public string? ExternalRef { get; set; }

    /// <summary>When the change happened at its source. An update older than this is ignored, so redelivered or reordered events can't undo a newer one.</summary>
    public DateTime? SourceEventAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    // ----- Signup record (null for tenants an administrator created) -----

    public string? SignupEmail { get; set; }

    /// <summary>Where the visitor came from (a <c>?source=</c> or <c>utm_source</c> on the signup link), or <c>direct</c>.</summary>
    public string? SignupSource { get; set; }

    public DateTime? TermsAcceptedAtUtc { get; set; }

    /// <summary>The terms version the visitor accepted (<c>Tenancy:Signup:TermsVersion</c>).</summary>
    public string? TermsVersion { get; set; }

    /// <summary>The company type chosen at signup (small business, contractor, ...). TODO: hook to the company-type setting once PR #29 lands.</summary>
    public string? CompanyType { get; set; }
}

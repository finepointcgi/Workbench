namespace Workbench.Domain.Identity;

public enum SsoProviderKind
{
    EntraId,
    Okta,
    GenericOidc,
}

public enum MfaRequirementLevel
{
    Off,
    AdminsOnly,
    Everyone,
}

public enum JitProvisioning
{
    /// <summary>Only invited, SCIM-provisioned or bootstrap-admin people may sign in (the default).</summary>
    InviteOnly,

    /// <summary>Also anyone the identity provider vouches for whose email is in one of the allowed domains.</summary>
    AllowedDomains,
}

/// <summary>
/// The organization's sign-in settings: its own OpenID Connect identity provider (in MultiTenant mode each tenant
/// brings one; self-hosted deployments may use it instead of, or as well as, <c>Authentication:Oidc</c>), the MFA
/// requirement, just-in-time provisioning rules and the session policy. One row per tenant database; missing means
/// the defaults (no organization IdP, MFA off, invite only, 15-minute re-authentication).
/// </summary>
public sealed class SignInSettings : Entity
{
    public bool SsoEnabled { get; set; }

    public SsoProviderKind ProviderKind { get; set; } = SsoProviderKind.GenericOidc;

    /// <summary>The sign-in button's label, e.g. "Acme Microsoft account".</summary>
    public string? DisplayName { get; set; }

    /// <summary>The issuer URL (Entra: <c>https://login.microsoftonline.com/{tenant-id}/v2.0</c>; Okta: <c>https://{org}.okta.com/oauth2/default</c>).</summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    /// <summary>The client secret, encrypted with the host's Data Protection keys (isolated per tenant). Never shown again.</summary>
    public string? ProtectedClientSecret { get; set; }

    public DateTime? ClientSecretSetAtUtc { get; set; }

    /// <summary>Extra scopes, space-separated, on top of <c>openid profile email</c>.</summary>
    public string? Scopes { get; set; }

    /// <summary>When true, the server-wide sign-in (<c>Authentication:Oidc</c>) is refused for this organization.</summary>
    public bool RequireOrganizationIdp { get; set; }

    public MfaRequirementLevel MfaRequirement { get; set; } = MfaRequirementLevel.Off;

    /// <summary><c>amr</c> values (space-separated) that prove multi-factor authentication.</summary>
    public string AcceptedAmrValues { get; set; } = DefaultAcceptedAmr;

    /// <summary><c>acr</c> (or Entra <c>acrs</c>) values that prove it, space-separated.</summary>
    public string? AcceptedAcrValues { get; set; }

    /// <summary>Sent to the IdP as <c>acr_values</c> (Okta: <c>urn:okta:loa:2fa:any</c>), space-separated.</summary>
    public string? RequestedAcrValues { get; set; }

    public JitProvisioning JitMode { get; set; } = JitProvisioning.InviteOnly;

    /// <summary>Email domains (space- or comma-separated) whose people are created on first sign-in in <see cref="JitProvisioning.AllowedDomains"/> mode.</summary>
    public string? AllowedEmailDomains { get; set; }

    /// <summary>Organization roles (the <c>OrgRoles</c> flags) given to people created just in time, before group mappings.</summary>
    public int JitDefaultRoles { get; set; }

    /// <summary>The claim carrying group ids or names (Entra and Okta: <c>groups</c>).</summary>
    public string GroupsClaim { get; set; } = "groups";

    /// <summary>When true, every sign-in sets the user's organization roles from the group mappings (the IdP manages roles).</summary>
    public bool SyncRolesFromGroups { get; set; }

    /// <summary>Sensitive actions (pay rates, bank details, approval workflows, these settings) need a sign-in this recent.</summary>
    public int ReauthenticationMinutes { get; set; } = DefaultReauthenticationMinutes;

    /// <summary>A stricter absolute session lifetime than the server's, or null for the server's.</summary>
    public int? SessionMaxHours { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }

    public string? UpdatedBy { get; set; }

    public const string DefaultAcceptedAmr = "mfa hwk swk otp fido sms tel face fpt pop";

    public const int DefaultReauthenticationMinutes = 15;
}

/// <summary>An IdP group (Entra object id or Okta group name) that grants organization roles on sign-in.</summary>
public sealed class SsoGroupMapping : Entity
{
    public required string GroupValue { get; set; }

    /// <summary>The <c>OrgRoles</c> flags it grants.</summary>
    public int Roles { get; set; }

    public string? Description { get; set; }
}

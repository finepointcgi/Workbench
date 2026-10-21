namespace Workbench.Contracts.Identity;

public enum IdentityProviderKind
{
    EntraId,
    Okta,
    GenericOidc,
}

/// <summary>Who must have used multi-factor authentication at the identity provider to sign in.</summary>
public enum MfaRequirement
{
    Off,

    /// <summary>People with the Admin organization role.</summary>
    AdminsOnly,

    Everyone,
}

public enum JitProvisioningMode
{
    /// <summary>Only invited, SCIM-provisioned or bootstrap-admin people may sign in.</summary>
    InviteOnly,

    /// <summary>Also anyone from an allowed email domain, created on their first sign-in.</summary>
    AllowedDomains,
}

public sealed record SsoGroupMappingDto(Guid Id, string GroupValue, OrgRoles Roles, string? Description, IReadOnlyList<RoleRef>? CustomRoles = null);

/// <summary>
/// The organization's sign-in settings as administrators see them. The client secret is never returned, only whether
/// one is set and when. <see cref="RedirectPath"/> is what to register at the IdP (prefix it with this site's address).
/// </summary>
public sealed record SignInSettingsDto(
    bool SsoEnabled,
    IdentityProviderKind ProviderKind,
    string? DisplayName,
    string? Authority,
    string? ClientId,
    bool HasClientSecret,
    DateTime? ClientSecretSetAtUtc,
    string? Scopes,
    bool RequireOrganizationIdp,
    MfaRequirement Mfa,
    string AcceptedAmrValues,
    string? AcceptedAcrValues,
    string? RequestedAcrValues,
    JitProvisioningMode Jit,
    string? AllowedEmailDomains,
    OrgRoles JitDefaultRoles,
    string GroupsClaim,
    bool SyncRolesFromGroups,
    IReadOnlyList<SsoGroupMappingDto> GroupMappings,
    int ReauthenticationMinutes,
    int? SessionMaxHours,
    DateTime? UpdatedAtUtc,
    string? UpdatedBy,
    SignInEnvironmentDto Environment)
{
    public const string RedirectPath = "/signin-sso";
    public const string SignedOutCallbackPath = "/signout-callback-sso";
}

/// <summary>What the server provides, and what the administrator's own session proved (for the lock-out guards).</summary>
public sealed record SignInEnvironmentDto(
    bool ServerOidcConfigured,
    bool MultiTenant,
    int ServerIdleTimeoutMinutes,
    int ServerAbsoluteLifetimeHours,
    string? CurrentSessionMethod,
    bool CurrentSessionMfa);

public sealed record SaveSsoProviderRequest(
    bool Enabled,
    IdentityProviderKind ProviderKind,
    string? DisplayName,
    string? Authority,
    string? ClientId,
    string? NewClientSecret,
    string? Scopes,
    bool RequireOrganizationIdp,
    bool ClearClientSecret = false);

public sealed record SaveMfaPolicyRequest(MfaRequirement Mfa, string? AcceptedAmrValues, string? AcceptedAcrValues, string? RequestedAcrValues);

/// <param name="CustomRoleIds">Custom roles the group grants, besides the built-in <paramref name="Roles"/>.</param>
public sealed record SaveSsoGroupMapping(string GroupValue, OrgRoles Roles, string? Description = null, IReadOnlyList<Guid>? CustomRoleIds = null);

public sealed record SaveProvisioningRequest(
    JitProvisioningMode Mode,
    string? AllowedEmailDomains,
    OrgRoles DefaultRoles,
    string? GroupsClaim,
    bool SyncRolesFromGroups,
    IReadOnlyList<SaveSsoGroupMapping> Mappings);

public sealed record SaveSessionPolicyRequest(int ReauthenticationMinutes, int? SessionMaxHours);

/// <summary>
/// Sign-in settings (Admin › Users and security › Sign-in). Admins only; every change is a <c>settings.sso</c> security
/// event and needs a recent sign-in. Guards against locking the organization out: requiring the organization's IdP needs
/// the administrator to be signed in through it, and requiring MFA needs their own session to have used MFA.
/// </summary>
public interface ISignInSettingsService
{
    Task<SignInSettingsDto> GetAsync(CancellationToken cancellationToken = default);

    Task<SignInSettingsDto> SaveProviderAsync(SaveSsoProviderRequest request, CancellationToken cancellationToken = default);

    Task<SignInSettingsDto> SaveMfaPolicyAsync(SaveMfaPolicyRequest request, CancellationToken cancellationToken = default);

    Task<SignInSettingsDto> SaveProvisioningAsync(SaveProvisioningRequest request, CancellationToken cancellationToken = default);

    Task<SignInSettingsDto> SaveSessionPolicyAsync(SaveSessionPolicyRequest request, CancellationToken cancellationToken = default);
}

// ---- Re-authentication for sensitive actions ----

/// <summary>The sensitive actions that need a recent sign-in.</summary>
public static class SensitiveActions
{
    public const string PayRateReveal = "pay_rate.reveal";
    public const string BankDetails = "bank_details";
    public const string VendorTaxId = "vendor_tax_id.reveal";
    public const string ApprovalWorkflowChange = "approval_workflow.change";
    public const string SignInSettingsChange = "sign_in_settings.change";

    public static string Describe(string action) => action switch
    {
        PayRateReveal => "show pay rates",
        BankDetails => "see or change bank account details",
        VendorTaxId => "show a vendor's tax id",
        ApprovalWorkflowChange => "change approval workflows",
        SignInSettingsChange => "change sign-in settings",
        _ => "do that",
    };
}

/// <summary>
/// Thrown when a sensitive action needs a fresher sign-in. A 403 over HTTP. The UI offers "Sign in again"
/// (<c>/account/reauthenticate?returnUrl=...</c>), which comes back to the same page.
/// </summary>
public sealed class ReauthenticationRequiredException(string action, int windowMinutes)
    : WorkbenchForbiddenException($"For your security, sign in again to {SensitiveActions.Describe(action)}. It's been more than {windowMinutes} minute{(windowMinutes == 1 ? "" : "s")} since you signed in.")
{
    public string Action { get; } = action;

    public int WindowMinutes { get; } = windowMinutes;
}

public sealed record ReauthenticationStatusDto(DateTime? AuthenticatedAtUtc, int WindowMinutes, bool IsRecent, bool MfaPerformed, string? Method);

/// <summary>The signed-in user's re-authentication state, and a notice when a sensitive action was just refused.</summary>
public interface IReauthenticationService
{
    Task<ReauthenticationStatusDto> GetStatusAsync(CancellationToken cancellationToken = default);

    /// <summary>Raised (in the same scope, e.g. the Blazor circuit) when a sensitive action was refused for an old sign-in.</summary>
    event Action<ReauthenticationRequiredException>? Required;
}

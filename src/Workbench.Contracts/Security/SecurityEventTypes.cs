namespace Workbench.Contracts.Security;

/// <summary>Event names in the security event log. Lower-case, dot-separated: area first, then what happened.</summary>
public static class SecurityEventTypes
{
    public const string SignInSuccess = "signin.success";
    public const string SignInFailure = "signin.failure";
    public const string DevSignIn = "signin.dev";
    public const string SignOut = "signout";
    public const string SessionRejected = "session.rejected";
    public const string MfaRequired = "signin.mfa_required";
    public const string Reauthenticated = "signin.reauthenticated";
    public const string ReauthenticationRequired = "access.reauth_required";
    public const string LockedOut = "signin.locked_out"; // Built-in account: too many wrong passwords or codes; detail has until when.
    public const string RecoveryCodeUsed = "signin.recovery_code"; // Built-in account: signed in with an MFA recovery code instead of the app.

    public const string PasswordSet = "user.password_set"; // Built-in account: password set from an invite or reset link, or changed; never the password.
    public const string PasswordResetRequested = "user.password_reset_requested"; // Forgot password (or an admin) asked for a link; detail says whether one was sent.
    public const string MfaEnabled = "user.mfa_enabled"; // Authenticator app (TOTP) set up.
    public const string MfaDisabled = "user.mfa_disabled"; // Authenticator app removed, by the user or an administrator's reset.
    public const string MfaRecoveryCodesRegenerated = "user.mfa_recovery_codes";

    public const string RolesChanged = "user.roles_changed";
    public const string UserCreated = "user.created";
    public const string UserDeactivated = "user.deactivated";
    public const string UserReactivated = "user.reactivated";
    public const string SessionsRevoked = "user.sessions_revoked";
    public const string EmployeeLinkChanged = "user.employee_link";

    public const string RoleCreated = "role.created"; // Admin › Roles: a custom role was created (or cloned); detail has its permissions.
    public const string RoleChanged = "role.changed"; // A custom role's name, description or permissions changed; detail has before → after.
    public const string RoleDeleted = "role.deleted";
    public const string PermissionRegistered = "permission.registered"; // A feature registered a run-time permission (custom field, automation, form).
    public const string PermissionUnregistered = "permission.unregistered";

    public const string InviteCreated = "invite.created";
    public const string InviteAccepted = "invite.accepted";
    public const string InviteRevoked = "invite.revoked";

    public const string Scim = "scim.request";

    public const string AccessReviewStarted = "access_review.started";
    public const string AccessReviewDecision = "access_review.decision";
    public const string AccessReviewCompleted = "access_review.completed";

    public const string PermissionDenied = "access.denied";
    public const string RateLimited = "access.rate_limited";

    public const string ExportLaborCsv = "export.labor_csv";
    public const string ExportFinancialCsv = "export.financial_csv";
    public const string ExportSecurityEvents = "export.security_events";
    public const string ExportReport = "export.report";
    public const string ExportEvm = "export.evm"; // IPMDAR Format 1/5 workbooks (earned value).
    public const string ExportAssets = "export.assets"; // Fixed asset register, schedules, roll-forward and government property exports.
    public const string ExportPricing = "export.pricing"; // Proposal cost volume workbooks (cost, indirect and bill rates).

    public const string PeriodStatusChanged = "period.status_changed";
    public const string SettingChanged = "settings.changed";
    public const string SignInSettingsChanged = "settings.sso";
    public const string AssistantSettingsChanged = "settings.assistant"; // AI assistant turned on/off, connection, model, limits, notice accepted.
    public const string ComplianceChecklistAnswered = "compliance.checklist";
    public const string LegalHoldPlaced = "legal_hold.placed";
    public const string LegalHoldReleased = "legal_hold.released";
    public const string RetentionRun = "retention.run";

    public const string ApiKeyCreated = "apikey.created"; // Integrations: an API key (service credential) was issued; detail has its scopes and roles, never the key.
    public const string ApiKeyRevoked = "apikey.revoked";
    public const string ApiKeyRejected = "apikey.rejected"; // A request with an unknown, revoked or expired key, or outside the key's scopes.
    public const string WebhookChanged = "integration.webhook"; // Webhook endpoint created, changed, secret rotated or deleted.
    public const string ConnectorChanged = "integration.connector"; // Connector created, changed or deleted.
}

public static class SecurityOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
}

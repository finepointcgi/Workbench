namespace Workbench.Contracts.Security;

/// <summary>Event names in the security event log. Lower-case, dot-separated: area first, then what happened.</summary>
public static class SecurityEventTypes
{
    public const string SignInSuccess = "signin.success";
    public const string SignInFailure = "signin.failure";
    public const string DevSignIn = "signin.dev";
    public const string SignOut = "signout";
    public const string SessionRejected = "session.rejected";

    public const string RolesChanged = "user.roles_changed";
    public const string UserCreated = "user.created";
    public const string UserDeactivated = "user.deactivated";
    public const string UserReactivated = "user.reactivated";
    public const string SessionsRevoked = "user.sessions_revoked";
    public const string EmployeeLinkChanged = "user.employee_link";

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

    public const string PeriodStatusChanged = "period.status_changed";
    public const string SettingChanged = "settings.changed";
    public const string LegalHoldPlaced = "legal_hold.placed";
    public const string LegalHoldReleased = "legal_hold.released";
    public const string RetentionRun = "retention.run";
}

public static class SecurityOutcomes
{
    public const string Success = "success";
    public const string Failure = "failure";
    public const string Denied = "denied";
}

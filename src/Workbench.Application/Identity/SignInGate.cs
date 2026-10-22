using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Application.Identity;

/// <param name="Code">Why it was refused, for the message the browser sees: <c>signin</c>, <c>mfa</c> or <c>org_idp</c>.</param>
public sealed record SignInDecision(bool Allowed, string? Reason, AppUser? User, string? Code = null)
{
    public static SignInDecision Deny(string reason, string code = SignInDenials.General) => new(false, reason, null, code);
}

public static class SignInDenials
{
    public const string General = "signin";
    public const string Mfa = "mfa";
    public const string OrganizationIdp = "org_idp";
}

/// <summary>
/// Decides whether someone the identity provider vouched for may sign in, and records the attempt. Known active
/// users may; deactivated users may not. Someone new needs a pending invite (accepted now, with its roles), a
/// bootstrap admin email, or an allowed email domain (just-in-time provisioning), unless
/// <see cref="SignInPolicyOptions.RequireInvite"/> is off. The organization's sign-in settings add the MFA requirement
/// (checked before anything is created or recorded as a success), "only our IdP", and roles synced from IdP groups.
/// </summary>
public sealed class SignInGate(
    IWorkbenchDbContextFactory dbFactory,
    UserProvisioner provisioner,
    UserStatusCache statusCache,
    SecurityEventLog securityLog,
    SignInPolicyOptions options,
    TimeProvider clock,
    SignInSettingsStore settingsStore,
    UserLifecycle lifecycle,
    Tenancy.TenancyOptions? tenancy = null)
{
    /// <summary>The actor name for role changes made from IdP groups at sign-in.</summary>
    public const string SsoActor = "sso";

    /// <summary>The <c>method</c> of a built-in account (email and password) sign-in.</summary>
    public const string PasswordMethod = "password";

    /// <summary>The <c>amr</c> value a built-in account sign-in carries after the authenticator app's code was checked.</summary>
    public const string OtpAmr = "otp";

    /// <param name="method">
    /// How they signed in, for the log and the rules: <c>sso</c> (the organization's IdP), <c>oidc</c> (the server's IdP),
    /// <c>password</c> (a built-in account; <paramref name="claims"/> has amr <c>otp</c> when the authenticator app was used), <c>dev</c> or <c>bearer</c>.
    /// </param>
    /// <param name="interactive">
    /// A sign-in (records success and the sign-in time). False for bearer tokens, checked on every API call, which
    /// only record refusals.
    /// </param>
    /// <param name="claims">How the IdP says they authenticated (<c>amr</c>/<c>acr</c>) and their groups. Null for the dev sign-in.</param>
    public async Task<SignInDecision> EvaluateAsync(string email, string? displayName, string method, bool interactive = true, CancellationToken cancellationToken = default,
        SignInClaims? claims = null)
    {
        email = AccessControl.Normalize(email);
        if (email.Length == 0)
        {
            return await DenyAsync(email, method, "The identity provider sent no email.", cancellationToken);
        }

        if (tenancy?.IsMultiTenant == true && Tenancy.TenantScope.Current is null)
        {
            // The host admin site: only the configured host admins, and no tenant database is touched.
            if (!tenancy.IsHostAdmin(email))
            {
                return await DenyAsync(email, method, "Only host administrators can sign in here.", cancellationToken);
            }

            await securityLog.RecordAsync(SecurityEventTypes.SignInSuccess, email, detail: $"{method} (host)", actor: email, cancellationToken: cancellationToken);
            return new SignInDecision(true, null, new AppUser { Email = email, DisplayName = displayName ?? email, CreatedAtUtc = clock.GetUtcNow().UtcDateTime });
        }

        var policy = await settingsStore.GetAsync(cancellationToken);
        // Bearer tokens (the desktop app) always come from the server's IdP, so they're refused too; so are passwords.
        if (method is "oidc" or "bearer" or PasswordMethod && policy.OrganizationIdpReady && policy.Settings.RequireOrganizationIdp)
        {
            return await DenyAsync(email, method, "This organization only allows sign-in through its own identity provider.", cancellationToken, SignInDenials.OrganizationIdp);
        }

        var viaIdp = method is "sso" or "oidc" or "bearer";
        var syncRoles = viaIdp && policy.Settings.SyncRolesFromGroups && claims?.Groups is not null;
        var isBootstrapAdmin = options.BootstrapAdmins.Contains(email);
        var state = await statusCache.GetAsync(email, cancellationToken);
        if (state.Exists)
        {
            if (!state.IsActive)
            {
                return await DenyAsync(email, method, "The account is deactivated.", cancellationToken);
            }

            // Bearer tokens come with every API call: read roles only when a rule needs them.
            var needRoles = syncRoles || policy.Settings.MfaRequirement == MfaRequirementLevel.AdminsOnly;
            var bootstrap = isBootstrapAdmin ? OrgRoles.Admin : OrgRoles.Employee;
            var roles = (needRoles ? await CurrentRolesAsync(email, cancellationToken) : RoleGrant.None).With(bootstrap);
            var target = syncRoles ? SignInRules.RolesFromIdp(policy, claims!.Groups).With(bootstrap) : roles;
            if (await RefuseWithoutMfaAsync(email, method, roles.Union(target), policy, claims, interactive, cancellationToken) is { } refused)
            {
                return refused;
            }

            var user = await provisioner.EnsureUserAsync(email, displayName, isBootstrapAdmin, cancellationToken);
            if (interactive)
            {
                if (syncRoles && !target.SameRolesAs(roles))
                {
                    user = await SyncRolesAsync(email, target, cancellationToken) ?? user;
                }

                await MarkSignedInAsync(email, cancellationToken);
                await RecordSuccessAsync(email, method, claims, cancellationToken);
            }

            return new SignInDecision(true, null, user);
        }

        var invite = await FindPendingInviteAsync(email, cancellationToken);
        var justInTime = invite is null && !isBootstrapAdmin && viaIdp && interactive && SignInRules.DomainAllowed(policy.Settings, email);
        if (invite is null && !isBootstrapAdmin && !justInTime && options.RequireInvite)
        {
            return await DenyAsync(email, method, "No pending invitation for this email.", cancellationToken);
        }

        var newRoles = invite is not null ? InviteRoles(invite)
            : justInTime ? SignInRules.RolesFromIdp(policy, claims?.Groups)
            : RoleGrant.None;
        if (isBootstrapAdmin)
        {
            newRoles = newRoles.With(OrgRoles.Admin);
        }

        if (await RefuseWithoutMfaAsync(email, method, newRoles, policy, claims, interactive, cancellationToken) is { } refusedNew)
        {
            return refusedNew;
        }

        var created = await provisioner.EnsureUserAsync(email, displayName ?? invite?.DisplayName, isBootstrapAdmin, cancellationToken);
        if (invite is not null)
        {
            await AcceptInviteAsync(invite.Id, email, cancellationToken);
        }
        else if (justInTime && newRoles.RoleIds.Count > 0)
        {
            created = await SetInitialRolesAsync(email, newRoles, cancellationToken) ?? created;
        }

        statusCache.Invalidate(email);
        var how = invite is not null ? "Accepted an invitation"
            : isBootstrapAdmin ? "Bootstrap administrator"
            : justInTime ? $"Just-in-time from an allowed domain; roles: {await DescribeAsync(newRoles, cancellationToken)}"
            : "Self sign-up (invites not required)";
        await securityLog.RecordAsync(SecurityEventTypes.UserCreated, email, detail: how, actor: email, cancellationToken: cancellationToken);
        if (interactive)
        {
            if (invite is not null && syncRoles)
            {
                // The IdP manages roles: the invite let them in, the groups decide what they can do.
                var target = SignInRules.RolesFromIdp(policy, claims!.Groups).With(isBootstrapAdmin ? OrgRoles.Admin : OrgRoles.Employee);
                if (!target.SameRolesAs(newRoles))
                {
                    created = await SyncRolesAsync(email, target, cancellationToken) ?? created;
                }
            }

            await MarkSignedInAsync(email, cancellationToken);
            await RecordSuccessAsync(email, method, claims, cancellationToken);
        }

        return new SignInDecision(true, null, created);
    }

    /// <summary>
    /// Whether this organization requires MFA of <paramref name="email"/>: their current roles (or a pending invite's)
    /// against the MFA setting. Built-in accounts ask before signing in, to send people without an app to set one up.
    /// </summary>
    public async Task<bool> RequiresMfaAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        var policy = await settingsStore.GetAsync(cancellationToken);
        if (policy.Settings.MfaRequirement == MfaRequirementLevel.Off)
        {
            return false;
        }

        var roles = (await CurrentRolesAsync(email, cancellationToken)).With(options.BootstrapAdmins.Contains(email) ? OrgRoles.Admin : OrgRoles.Employee);
        if (await FindPendingInviteAsync(email, cancellationToken) is { } invite)
        {
            roles = roles.Union(InviteRoles(invite));
        }

        return await MfaRequiredAsync(policy, roles, cancellationToken);
    }

    private async Task<bool> MfaRequiredAsync(SignInPolicySnapshot policy, RoleGrant roles, CancellationToken cancellationToken)
    {
        if (policy.Settings.MfaRequirement == MfaRequirementLevel.Off)
        {
            return false;
        }

        if (policy.Settings.MfaRequirement == MfaRequirementLevel.Everyone)
        {
            return true;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return SignInRules.MfaRequired(policy.Settings, (await RoleStore.ResolveRoleIdsAsync(db, roles.RoleIds, cancellationToken)).Permissions);
    }

    /// <summary>The refusal when the organization requires MFA for these roles and the IdP didn't report it; null when fine.</summary>
    private async Task<SignInDecision?> RefuseWithoutMfaAsync(string email, string method, RoleGrant roles, SignInPolicySnapshot policy, SignInClaims? claims,
        bool interactive, CancellationToken cancellationToken)
    {
        // The dev sign-in exists only in Development and has no second factor to check. Built-in accounts prove MFA with
        // Workbench's own authenticator check, whatever amr values the organization accepts from its IdP.
        var satisfied = method == PasswordMethod ? claims?.Amr.Contains(OtpAmr) == true : SignInRules.MfaSatisfied(policy.Settings, claims ?? SignInClaims.None);
        if (method == "dev" || satisfied || !await MfaRequiredAsync(policy, roles, cancellationToken))
        {
            return null;
        }

        var seen = claims is null ? "none" : $"amr={string.Join(",", claims.Amr.Take(8))}; acr={string.Join(",", claims.Acr.Take(4))}";
        await securityLog.RecordAsync(SecurityEventTypes.MfaRequired, email, SecurityOutcomes.Denied,
            $"{method}: MFA required ({policy.Settings.MfaRequirement}) but not reported by the identity provider ({Clip(seen, 300)})", actor: email, cancellationToken: cancellationToken);
        if (!interactive)
        {
            return SignInDecision.Deny("Multi-factor authentication is required.", SignInDenials.Mfa);
        }

        if (method == PasswordMethod)
        {
            return SignInDecision.Deny("Your organization requires multi-factor authentication. Set up an authenticator app and sign in with its code.", SignInDenials.Mfa);
        }

        return SignInDecision.Deny(
            "Your organization requires multi-factor authentication, and your identity provider reported that this sign-in didn't use it. Sign in again with your second factor.",
            SignInDenials.Mfa);
    }

    private async Task RecordSuccessAsync(string email, string method, SignInClaims? claims, CancellationToken cancellationToken)
    {
        var detail = claims is null ? method : $"{method}; amr={string.Join(",", claims.Amr.Take(8))}{(claims.Acr.Count > 0 ? $"; acr={string.Join(",", claims.Acr.Take(4))}" : "")}";
        await securityLog.RecordAsync(method == "dev" ? SecurityEventTypes.DevSignIn : SecurityEventTypes.SignInSuccess, email, detail: Clip(detail, 400), actor: email,
            cancellationToken: cancellationToken);
    }

    private async Task<RoleGrant> CurrentRolesAsync(string email, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var userId = await db.Set<AppUser>().AsNoTracking().Where(u => u.Email == email).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(cancellationToken);
        return userId is { } id ? RoleGrant.FromIds(await RoleStore.RoleIdsOfAsync(db, id, cancellationToken)) : RoleGrant.None;
    }

    private async Task<AppUser?> SyncRolesAsync(string email, RoleGrant roles, CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycle.SetGrantAsync(email, roles, SsoActor, cancellationToken);
        }
        catch (ArgumentException ex)
        {
            // The last active admin keeps the role; say so in the log instead of refusing the sign-in.
            await securityLog.RecordAsync(SecurityEventTypes.RolesChanged, email, SecurityOutcomes.Failure, $"Roles from IdP groups not applied: {ex.Message}",
                actor: SsoActor, cancellationToken: cancellationToken);
            return null;
        }
    }

    private async Task<AppUser?> SetInitialRolesAsync(string email, RoleGrant roles, CancellationToken cancellationToken)
    {
        try
        {
            return await lifecycle.GrantAsync(email, roles, SsoActor, cancellationToken);
        }
        catch (WorkbenchNotFoundException)
        {
            return null;
        }
    }

    /// <summary>The roles an invite grants: built-in flags plus custom roles that still exist.</summary>
    internal static RoleGrant InviteRoles(UserInvite invite) => RoleGrant.Of((OrgRoles)invite.Roles, RoleStore.ParseIds(invite.CustomRoleIds));

    private async Task<string> DescribeAsync(RoleGrant roles, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return RoleStore.Describe(await RoleStore.RefsAsync(db, roles.RoleIds, cancellationToken));
    }

    private async Task<UserInvite?> FindPendingInviteAsync(string email, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<UserInvite>().AsNoTracking()
            .Where(i => i.Email == email && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
            .OrderByDescending(i => i.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task AcceptInviteAsync(Guid inviteId, string email, CancellationToken cancellationToken)
    {
        UserInvite invite;
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            invite = await db.Set<UserInvite>().FirstAsync(i => i.Id == inviteId, cancellationToken);
            invite.AcceptedAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }

        // Custom roles deleted since the invite was sent are skipped.
        var roles = InviteRoles(invite);
        await using (var db = await dbFactory.CreateAsync(cancellationToken))
        {
            var existing = await db.Set<Role>().AsNoTracking().Where(r => roles.Custom.Contains(r.Id)).Select(r => r.Id).ToListAsync(cancellationToken);
            roles = roles with { Custom = existing };
        }

        await lifecycle.GrantAsync(email, roles, invite.InvitedBy, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.InviteAccepted, email, detail: $"Invited by {invite.InvitedBy}; roles: {await DescribeAsync(roles, cancellationToken)}", actor: email,
            cancellationToken: cancellationToken);
    }

    private async Task MarkSignedInAsync(string email, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is not null)
        {
            user.LastSignInAtUtc = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<SignInDecision> DenyAsync(string email, string method, string reason, CancellationToken cancellationToken, string code = SignInDenials.General)
    {
        await securityLog.RecordAsync(SecurityEventTypes.SignInFailure, email.Length == 0 ? null : email, SecurityOutcomes.Failure, $"{method}: {reason}",
            actor: email.Length == 0 ? "anonymous" : email, cancellationToken: cancellationToken);
        return SignInDecision.Deny(reason, code);
    }

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}

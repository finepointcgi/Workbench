using Microsoft.EntityFrameworkCore;
using Workbench.Application.Security;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Application.Identity;

public sealed record SignInDecision(bool Allowed, string? Reason, AppUser? User)
{
    public static SignInDecision Deny(string reason) => new(false, reason, null);
}

/// <summary>
/// Decides whether someone the identity provider vouched for may sign in, and records the attempt. Known active
/// users may; deactivated users may not. Someone new needs a pending invite (accepted now, with its roles) or a
/// bootstrap admin email, unless <see cref="SignInPolicyOptions.RequireInvite"/> is off.
/// </summary>
public sealed class SignInGate(
    IWorkbenchDbContextFactory dbFactory,
    UserProvisioner provisioner,
    UserStatusCache statusCache,
    SecurityEventLog securityLog,
    SignInPolicyOptions options,
    TimeProvider clock,
    Tenancy.TenancyOptions? tenancy = null)
{
    /// <param name="method">How they signed in, for the log: <c>oidc</c>, <c>dev</c> or <c>bearer</c>.</param>
    /// <param name="interactive">
    /// A sign-in (records success and the sign-in time). False for bearer tokens, checked on every API call, which
    /// only record refusals.
    /// </param>
    public async Task<SignInDecision> EvaluateAsync(string email, string? displayName, string method, bool interactive = true, CancellationToken cancellationToken = default)
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

        var isBootstrapAdmin = options.BootstrapAdmins.Contains(email);
        var state = await statusCache.GetAsync(email, cancellationToken);
        if (state.Exists)
        {
            if (!state.IsActive)
            {
                return await DenyAsync(email, method, "The account is deactivated.", cancellationToken);
            }

            var user = await provisioner.EnsureUserAsync(email, displayName, isBootstrapAdmin, cancellationToken);
            if (interactive)
            {
                await MarkSignedInAsync(email, cancellationToken);
                await securityLog.RecordAsync(method == "dev" ? SecurityEventTypes.DevSignIn : SecurityEventTypes.SignInSuccess, email, detail: method, actor: email, cancellationToken: cancellationToken);
            }

            return new SignInDecision(true, null, user);
        }

        var invite = await FindPendingInviteAsync(email, cancellationToken);
        if (invite is null && !isBootstrapAdmin && options.RequireInvite)
        {
            return await DenyAsync(email, method, "No pending invitation for this email.", cancellationToken);
        }

        var created = await provisioner.EnsureUserAsync(email, displayName ?? invite?.DisplayName, isBootstrapAdmin, cancellationToken);
        if (invite is not null)
        {
            await AcceptInviteAsync(invite.Id, email, cancellationToken);
        }

        statusCache.Invalidate(email);
        await securityLog.RecordAsync(SecurityEventTypes.UserCreated, email,
            detail: invite is not null ? "Accepted an invitation" : isBootstrapAdmin ? "Bootstrap administrator" : "Self sign-up (invites not required)",
            actor: email, cancellationToken: cancellationToken);
        if (interactive)
        {
            await MarkSignedInAsync(email, cancellationToken);
            await securityLog.RecordAsync(method == "dev" ? SecurityEventTypes.DevSignIn : SecurityEventTypes.SignInSuccess, email, detail: method, actor: email, cancellationToken: cancellationToken);
        }

        return new SignInDecision(true, null, created);
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
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invite = await db.Set<UserInvite>().FirstAsync(i => i.Id == inviteId, cancellationToken);
        var user = await db.Set<AppUser>().FirstAsync(u => u.Email == email, cancellationToken);
        invite.AcceptedAtUtc = clock.GetUtcNow().UtcDateTime;
        user.IsAdmin |= invite.IsAdmin;
        user.IsFinance |= invite.IsFinance;
        user.IsTimekeeper |= invite.IsTimekeeper;
        user.IsResourceManager |= invite.IsResourceManager;
        await db.SaveChangesAsync(cancellationToken);
        var roles = AccessControl.ToRoles(invite.IsAdmin, invite.IsFinance, invite.IsTimekeeper, invite.IsResourceManager);
        await securityLog.RecordAsync(SecurityEventTypes.InviteAccepted, email, detail: $"Invited by {invite.InvitedBy}; roles: {UserLifecycle.RoleText(roles)}", actor: email, cancellationToken: cancellationToken);
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

    private async Task<SignInDecision> DenyAsync(string email, string method, string reason, CancellationToken cancellationToken)
    {
        await securityLog.RecordAsync(SecurityEventTypes.SignInFailure, email.Length == 0 ? null : email, SecurityOutcomes.Failure, $"{method}: {reason}",
            actor: email.Length == 0 ? "anonymous" : email, cancellationToken: cancellationToken);
        return SignInDecision.Deny(reason);
    }
}

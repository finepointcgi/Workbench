using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Application.Identity;
using Workbench.Contracts;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Security;

namespace Workbench.Application.Security;

public sealed class UserAdminService(IWorkbenchDbContextFactory dbFactory, AccessControl access, UserLifecycle lifecycle, Identity.Passwords.PasswordAccounts passwords) : IUserAdminService
{
    public async Task<IReadOnlyList<UserAdminDto>> ListUsersAsync(string? query = null, bool includeInactive = true, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var users = db.Set<AppUser>().AsNoTracking();
        if (!includeInactive)
        {
            users = users.Where(u => u.IsActive);
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var q = query.Trim().ToLowerInvariant();
            users = users.Where(u => u.Email.Contains(q) || u.DisplayName.ToLower().Contains(q));
        }

        var rows = await users.ToListAsync(cancellationToken);
        var userIds = rows.Select(u => u.Id).ToList();
        var held = await RoleStore.UserRefsAsync(db, userIds, cancellationToken);
        var employees = await db.Set<Employee>().AsNoTracking().Select(e => new { e.Id, e.Email, e.DisplayName }).ToListAsync(cancellationToken);
        var byId = employees.ToDictionary(e => e.Id);
        var byEmail = employees.GroupBy(e => e.Email).ToDictionary(g => g.Key, g => g.First());
        var emails = rows.Select(u => u.Email).ToList();
        var credentials = (await db.Set<PasswordCredential>().AsNoTracking().Where(c => emails.Contains(c.Email))
            .Select(c => new { c.Email, HasPassword = c.PasswordHash != null, Mfa = c.ProtectedTotpSecret != null }).ToListAsync(cancellationToken))
            .ToDictionary(c => c.Email);
        return rows
            .OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName).ThenBy(u => u.Email)
            .Select(u =>
            {
                var mode = u.EmployeeUnlinked ? EmployeeLinkMode.Unlinked : u.EmployeeId is null ? EmployeeLinkMode.ByEmail : EmployeeLinkMode.Explicit;
                var employee = mode switch
                {
                    EmployeeLinkMode.Explicit => byId.GetValueOrDefault(u.EmployeeId!.Value),
                    EmployeeLinkMode.ByEmail => byEmail.GetValueOrDefault(u.Email),
                    _ => null,
                };
                var roles = held[u.Id].ToList();
                var credential = credentials.GetValueOrDefault(u.Email);
                return ToDto(u, employee?.Id, employee?.DisplayName, mode, roles) with { HasPassword = credential?.HasPassword == true, MfaEnabled = credential?.Mfa == true };
            })
            .ToList();
    }

    public async Task<UserAdminDto> SetRolesAsync(string email, OrgRoles roles, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await access.RequireCanGrantNewRolesAsync(email, BuiltInRoles.IdsOf(roles), cancellationToken);
        await lifecycle.SetRolesAsync(email, roles, access.Email, cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> DeactivateAsync(string email, string? reason = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await lifecycle.SetActiveAsync(email, false, access.Email, reason, cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> ReactivateAsync(string email, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await lifecycle.SetActiveAsync(email, true, access.Email, cancellationToken: cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> SignOutEverywhereAsync(string email, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await lifecycle.RevokeSessionsAsync(email, access.Email, cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> SetEmployeeLinkAsync(string email, EmployeeLinkMode mode, Guid? employeeId = null, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await lifecycle.SetEmployeeLinkAsync(email, mode, employeeId, access.Email, cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> ResetMfaAsync(string email, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        var user = await GetAsync(email, cancellationToken);
        if (!await passwords.RemoveTotpAsync(user.Email, access.Email, cancellationToken))
        {
            throw new ArgumentException($"{user.Email} has no authenticator app set up in Workbench. MFA for single sign-on is managed at the identity provider.", nameof(email));
        }

        // Whoever has the old phone shouldn't keep a session either.
        await lifecycle.RevokeSessionsAsync(user.Email, access.Email, cancellationToken);
        return await GetAsync(email, cancellationToken);
    }

    public async Task<UserAdminDto> SendPasswordResetAsync(string email, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        var user = await GetAsync(email, cancellationToken);
        if (!user.IsActive)
        {
            throw new ArgumentException("Reactivate the user first.", nameof(email));
        }

        if (!passwords.Options.Enabled)
        {
            throw new ArgumentException("Built-in accounts are turned off on this server (Authentication:Passwords:Enabled).", nameof(email));
        }

        await passwords.RequestPasswordResetAsync(user.Email, requestedBy: access.Email, cancellationToken: cancellationToken);
        return user;
    }

    public async Task<IReadOnlyList<LinkableEmployeeDto>> ListLinkableEmployeesAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<Employee>().AsNoTracking().ToListAsync(cancellationToken);
        return rows.OrderBy(e => e.DisplayName).Select(e => new LinkableEmployeeDto(e.Id, e.EmployeeNumber, e.DisplayName, e.Email)).ToList();
    }

    private async Task<UserAdminDto> GetAsync(string email, CancellationToken cancellationToken)
    {
        email = AccessControl.Normalize(email);
        var all = await ListUsersAsync(email, true, cancellationToken);
        return all.FirstOrDefault(u => u.Email == email) ?? throw new WorkbenchNotFoundException($"User {email} was not found.");
    }

    internal static UserAdminDto ToDto(AppUser u, Guid? employeeId, string? employeeName, EmployeeLinkMode mode, IReadOnlyList<RoleRef> roles) =>
        new(u.Id, u.Email, u.DisplayName, BuiltInRoles.FlagsOf(roles.Where(r => r.Scope is null).Select(r => r.Id)), u.IsActive, u.CreatedAtUtc,
            u.LastSeenAtUtc, u.LastSignInAtUtc, u.DeactivatedAtUtc, u.DeactivatedBy, employeeId, employeeName, mode, u.ScimManaged, roles);
}

public sealed class InviteService(
    IWorkbenchDbContextFactory dbFactory,
    AccessControl access,
    SecurityEventLog securityLog,
    SignInPolicyOptions options,
    IEmailSender email,
    NotificationOptions notificationOptions,
    ILogger<InviteService> logger,
    TimeProvider clock,
    Identity.Passwords.PasswordAccounts passwords,
    Identity.Passwords.AccountLinks links,
    Licensing.EditionService editions) : IInviteService
{
    public async Task<IReadOnlyList<InviteDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var rows = await db.Set<UserInvite>().AsNoTracking().ToListAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        var names = (await RoleStore.RefsAsync(db, rows.SelectMany(i => RoleStore.ParseIds(i.CustomRoleIds)), cancellationToken)).ToDictionary(r => r.Id);
        return rows.OrderByDescending(i => i.CreatedAtUtc).Select(i => ToDto(i, now, names)).ToList();
    }

    public async Task<InviteDto> CreateAsync(CreateInviteRequest request, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        var email = UserLifecycle.ValidEmail(request.Email);
        var grant = RoleGrant.Of(request.Roles, request.CustomRoleIds);
        await access.RequireCanGrantRolesAsync(grant.RoleIds, cancellationToken);
        var name = UserLifecycle.CleanName(request.DisplayName) ?? throw new ArgumentException("Enter the person's name.", nameof(request));
        var now = clock.GetUtcNow().UtcDateTime;

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        if (await db.Set<AppUser>().AnyAsync(u => u.Email == email, cancellationToken))
        {
            throw new ArgumentException($"{email} already has an account. Change their roles or reactivate them instead.", nameof(request));
        }

        await editions.RequireFreeSeatAsync(db, cancellationToken);

        // One pending invite per email: a new one replaces the old.
        var pending = await db.Set<UserInvite>()
            .Where(i => i.Email == email && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
            .ToListAsync(cancellationToken);
        foreach (var old in pending)
        {
            old.RevokedAtUtc = now;
            old.RevokedBy = access.Email;
        }

        var custom = await RoleStore.ValidateCustomAsync(db, request.CustomRoleIds, cancellationToken);
        var invite = new UserInvite
        {
            Email = email,
            DisplayName = name,
            Roles = (int)(request.Roles & OrgRoleChecks.Everything),
            CustomRoleIds = RoleStore.FormatIds(custom),
            InvitedBy = access.Email,
            CreatedAtUtc = now,
            ExpiresAtUtc = now + options.InviteLifetime,
        };
        db.Set<UserInvite>().Add(invite);
        await db.SaveChangesAsync(cancellationToken);
        var refs = await RoleStore.RefsAsync(db, grant.RoleIds, cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.InviteCreated, email,
            detail: $"Roles: {RoleStore.Describe(refs)}; expires {invite.ExpiresAtUtc:yyyy-MM-dd HH:mm} UTC", cancellationToken: cancellationToken);
        await SendInviteEmailAsync(invite, cancellationToken);
        return ToDto(invite, now, refs.ToDictionary(r => r.Id));
    }

    public async Task<InviteDto> RevokeAsync(Guid inviteId, CancellationToken cancellationToken = default)
    {
        await access.RequireAsync(Permission.AdminUsers, "administer users", cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var invite = await db.Set<UserInvite>().FirstOrDefaultAsync(i => i.Id == inviteId, cancellationToken)
            ?? throw WorkbenchNotFoundException.For("Invite", inviteId);
        if (ToDto(invite, now, new Dictionary<Guid, RoleRef>()).Status != InviteStatus.Pending)
        {
            throw new ArgumentException("Only pending invites can be revoked.", nameof(inviteId));
        }

        invite.RevokedAtUtc = now;
        invite.RevokedBy = access.Email;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.InviteRevoked, invite.Email, cancellationToken: cancellationToken);
        return ToDto(invite, now, (await RoleStore.RefsAsync(db, RoleStore.ParseIds(invite.CustomRoleIds), cancellationToken)).ToDictionary(r => r.Id));
    }

    private async Task SendInviteEmailAsync(UserInvite invite, CancellationToken cancellationToken)
    {
        var signIn = string.IsNullOrWhiteSpace(notificationOptions.PublicBaseUrl) && links.FallbackBaseUrl is null
            ? "Workbench"
            : links.Absolute("/account/signin");
        var expires = $"{invite.ExpiresAtUtc:MMMM d, yyyy HH:mm} UTC";
        var text = $"Hello {invite.DisplayName},\n\n{invite.InvitedBy} invited you to Workbench.";
        var html = $"<p>Hello {WebUtility.HtmlEncode(invite.DisplayName)},</p><p>{WebUtility.HtmlEncode(invite.InvitedBy)} invited you to Workbench.</p>";
        if (passwords.Options.Enabled)
        {
            // A one-time link to set a password (the invite's lifetime); organization SSO still works with the same email.
            var accept = links.Absolute($"/account/accept-invite?token={await passwords.IssueInviteTokenAsync(invite, cancellationToken)}");
            text += $" Choose your password here before {expires}:\n\n{accept}\n\nIf your organization signs in with its own account (single sign-on), you can instead sign in with this email address ({invite.Email}) at {signIn}.\n";
            html += $"<p>Choose your password before {expires}:</p><p><a href=\"{WebUtility.HtmlEncode(accept)}\">Accept the invitation</a></p>"
                + $"<p>If your organization signs in with its own account (single sign-on), you can instead sign in with this email address ({WebUtility.HtmlEncode(invite.Email)}) at {WebUtility.HtmlEncode(signIn)}.</p>";
        }
        else
        {
            text += $" Sign in with this email address ({invite.Email}) at {signIn} before {expires}.\n";
            html += $"<p>Sign in with this email address ({WebUtility.HtmlEncode(invite.Email)}) at {WebUtility.HtmlEncode(signIn)} before {expires}.</p>";
        }

        try
        {
            await email.SendAsync(new EmailMessage(invite.Email, "You're invited to Workbench", text, html) { Essential = true }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't email invite {InviteId}", invite.Id);
        }
    }

    internal static InviteDto ToDto(UserInvite i, DateTime now, IReadOnlyDictionary<Guid, RoleRef> roleNames) =>
        new(i.Id, i.Email, i.DisplayName, (OrgRoles)i.Roles, i.InvitedBy, i.CreatedAtUtc, i.ExpiresAtUtc,
            i.AcceptedAtUtc, i.RevokedAtUtc,
            i.AcceptedAtUtc is not null ? InviteStatus.Accepted
            : i.RevokedAtUtc is not null ? InviteStatus.Revoked
            : i.ExpiresAtUtc <= now ? InviteStatus.Expired
            : InviteStatus.Pending,
            RoleStore.ParseIds(i.CustomRoleIds).Select(roleNames.GetValueOrDefault).OfType<RoleRef>().ToList());
}

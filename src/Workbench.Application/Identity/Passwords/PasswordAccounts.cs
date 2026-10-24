using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Workbench.Application.Notifications;
using Workbench.Application.Security;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Security;

namespace Workbench.Application.Identity.Passwords;

public enum PasswordCheckStatus
{
    Ok,

    /// <summary>Wrong password, unknown email or no password set: the same answer for all three.</summary>
    Wrong,

    LockedOut,
}

public sealed record PasswordCheckResult(PasswordCheckStatus Status, bool MfaEnrolled = false, DateTime? LockedOutUntilUtc = null);

/// <summary>What a person's built-in account has, for the account page and the admin list.</summary>
public sealed record PasswordAccountStatus(bool HasPassword, DateTime? PasswordChangedAtUtc, bool MfaEnabled, DateTime? MfaEnabledAtUtc, int RecoveryCodesLeft, DateTime? LockedOutUntilUtc);

/// <summary>An authenticator app enrollment in progress: the secret to type and the <c>otpauth://</c> link the QR code carries.</summary>
public sealed record TotpEnrollment(string Secret, string ProvisioningUri);

/// <summary>
/// Built-in Workbench accounts: passwords (ASP.NET Core's PBKDF2 <see cref="PasswordHasher{TUser}"/>), lockout after
/// repeated failures, one-time email links (accept an invite, reset a password) and the authenticator app (TOTP) with
/// recovery codes. Deciding whether the person may sign in at all (active, invited, MFA policy) stays with
/// <see cref="SignInGate"/>, which the web host calls once these checks pass. Passwords, codes and link tokens are never
/// logged or stored in the clear.
/// </summary>
public sealed class PasswordAccounts(
    IWorkbenchDbContextFactory dbFactory,
    SecurityEventLog securityLog,
    PasswordOptions options,
    SignInPolicyOptions signInOptions,
    IDataProtectionProvider dataProtection,
    IEmailSender email,
    AccountLinks links,
    UserStatusCache statusCache,
    ILogger<PasswordAccounts> logger,
    TimeProvider clock)
{
    public const string Issuer = "Workbench";

    private static readonly PasswordHasher<string> Hasher = new();

    // Spent on unknown emails too, so response time doesn't reveal which emails have accounts.
    private static readonly Lazy<string> DummyHash = new(() => Hasher.HashPassword(string.Empty, Convert.ToHexString(RandomNumberGenerator.GetBytes(16))));

    private IDataProtector Protector => dataProtection.CreateProtector("Workbench.Accounts.Totp.v1");

    public PasswordOptions Options => options;

    public async Task<PasswordAccountStatus> GetStatusAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var c = await db.Set<PasswordCredential>().AsNoTracking().FirstOrDefaultAsync(x => x.Email == email, cancellationToken);
        return c is null
            ? new PasswordAccountStatus(false, null, false, null, 0, null)
            : new PasswordAccountStatus(c.PasswordHash is not null, c.PasswordChangedAtUtc, c.ProtectedTotpSecret is not null, c.TotpEnabledAtUtc,
                RecoveryHashes(c).Count, c.LockedOutUntilUtc > Now ? c.LockedOutUntilUtc : null);
    }

    /// <summary>Checks a password, counting failures toward lockout. Records failures and lockouts; success is recorded by the sign-in gate.</summary>
    public async Task<PasswordCheckResult> CheckPasswordAsync(string email, string? password, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        password ??= string.Empty;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = email.Length == 0 ? null : await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (credential?.PasswordHash is null)
        {
            Hasher.VerifyHashedPassword(string.Empty, DummyHash.Value, password);
            await securityLog.RecordAsync(SecurityEventTypes.SignInFailure, email.Length == 0 ? null : email, SecurityOutcomes.Failure,
                credential is null ? "password: no built-in account for this email" : "password: no password set", actor: email.Length == 0 ? "anonymous" : email,
                cancellationToken: cancellationToken);
            return new PasswordCheckResult(PasswordCheckStatus.Wrong);
        }

        if (credential.LockedOutUntilUtc > Now)
        {
            // No hash check while locked: guessing during the lockout teaches nothing.
            await securityLog.RecordAsync(SecurityEventTypes.SignInFailure, email, SecurityOutcomes.Denied, $"password: account locked until {credential.LockedOutUntilUtc:yyyy-MM-dd HH:mm} UTC",
                actor: email, cancellationToken: cancellationToken);
            return new PasswordCheckResult(PasswordCheckStatus.LockedOut, LockedOutUntilUtc: credential.LockedOutUntilUtc);
        }

        var verdict = Hasher.VerifyHashedPassword(string.Empty, credential.PasswordHash, password);
        if (verdict == PasswordVerificationResult.Failed)
        {
            return await FailAsync(db, credential, "wrong password", cancellationToken);
        }

        if (verdict == PasswordVerificationResult.SuccessRehashNeeded)
        {
            credential.PasswordHash = Hasher.HashPassword(string.Empty, password);
        }

        // A correct password doesn't clear the counter while a second factor is still owed: guessing codes keeps counting.
        if (credential.ProtectedTotpSecret is null)
        {
            credential.FailedAttempts = 0;
            credential.LockedOutUntilUtc = null;
        }

        await db.SaveChangesAsync(cancellationToken);
        return new PasswordCheckResult(PasswordCheckStatus.Ok, credential.ProtectedTotpSecret is not null);
    }

    /// <summary>
    /// Checks an authenticator code or a recovery code (which is used up). Wrong codes count toward the same lockout as
    /// wrong passwords. True on success.
    /// </summary>
    public async Task<PasswordCheckResult> CheckSecondFactorAsync(string email, string? code, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (credential?.ProtectedTotpSecret is null)
        {
            return new PasswordCheckResult(PasswordCheckStatus.Wrong);
        }

        if (credential.LockedOutUntilUtc > Now)
        {
            return new PasswordCheckResult(PasswordCheckStatus.LockedOut, true, credential.LockedOutUntilUtc);
        }

        var secret = Unprotect(credential.ProtectedTotpSecret);
        if (secret is not null && Totp.Match(secret, code, clock.GetUtcNow(), credential.LastTotpStep) is { } step)
        {
            credential.LastTotpStep = step;
            credential.FailedAttempts = 0;
            credential.LockedOutUntilUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            return new PasswordCheckResult(PasswordCheckStatus.Ok, true);
        }

        var hashes = RecoveryHashes(credential);
        var recovery = HashRecoveryCode(code);
        if (recovery is not null && hashes.Remove(recovery))
        {
            credential.RecoveryCodeHashes = string.Join(' ', hashes);
            credential.FailedAttempts = 0;
            credential.LockedOutUntilUtc = null;
            await db.SaveChangesAsync(cancellationToken);
            await securityLog.RecordAsync(SecurityEventTypes.RecoveryCodeUsed, email, detail: $"{hashes.Count} recovery codes left", actor: email, cancellationToken: cancellationToken);
            return new PasswordCheckResult(PasswordCheckStatus.Ok, true);
        }

        return await FailAsync(db, credential, "wrong authenticator or recovery code", cancellationToken);
    }

    private async Task<PasswordCheckResult> FailAsync(IWorkbenchDbContext db, PasswordCredential credential, string what, CancellationToken cancellationToken)
    {
        credential.FailedAttempts++;
        var locked = credential.FailedAttempts >= options.MaxFailedAttempts;
        if (locked)
        {
            credential.LockedOutUntilUtc = Now + options.LockoutDuration;
            credential.FailedAttempts = 0;
        }

        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.SignInFailure, credential.Email, SecurityOutcomes.Failure,
            locked ? $"password: {what}; account locked" : $"password: {what} ({credential.FailedAttempts} of {options.MaxFailedAttempts})", actor: credential.Email,
            cancellationToken: cancellationToken);
        if (locked)
        {
            await securityLog.RecordAsync(SecurityEventTypes.LockedOut, credential.Email, SecurityOutcomes.Denied,
                $"{options.MaxFailedAttempts} failed attempts; locked until {credential.LockedOutUntilUtc:yyyy-MM-dd HH:mm} UTC", actor: credential.Email, cancellationToken: cancellationToken);
            return new PasswordCheckResult(PasswordCheckStatus.LockedOut, credential.ProtectedTotpSecret is not null, credential.LockedOutUntilUtc);
        }

        return new PasswordCheckResult(PasswordCheckStatus.Wrong, credential.ProtectedTotpSecret is not null);
    }

    /// <summary>
    /// Sets the password (from an invite or reset link, or a change), after checking the policy. Ends every unused link for
    /// the email, clears the lockout, and records it. Throws <see cref="ArgumentException"/> with the policy's reason.
    /// </summary>
    public async Task SetPasswordAsync(string email, string? password, string how, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        if (PasswordPolicy.Problem(password, email, options) is { } problem)
        {
            throw new ArgumentException(problem, nameof(password));
        }

        var now = Now;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await GetOrAddAsync(db, email, cancellationToken);
        credential.PasswordHash = Hasher.HashPassword(string.Empty, password!);
        credential.PasswordChangedAtUtc = now;
        credential.FailedAttempts = 0;
        credential.LockedOutUntilUtc = null;
        credential.UpdatedAtUtc = now;
        foreach (var token in await db.Set<AccountToken>().Where(t => t.Email == email && t.UsedAtUtc == null && t.RevokedAtUtc == null).ToListAsync(cancellationToken))
        {
            token.RevokedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.PasswordSet, email, detail: how, actor: email, cancellationToken: cancellationToken);
    }

    /// <summary>Changes the password of a signed-in person, who must give the current one.</summary>
    public async Task ChangePasswordAsync(string email, string? currentPassword, string? newPassword, CancellationToken cancellationToken = default)
    {
        var check = await CheckPasswordAsync(email, currentPassword, cancellationToken);
        if (check.Status != PasswordCheckStatus.Ok)
        {
            throw new ArgumentException(check.Status == PasswordCheckStatus.LockedOut ? "Too many wrong attempts. Try again later." : "Your current password isn't right.", nameof(currentPassword));
        }

        await SetPasswordAsync(email, newPassword, "Changed by the user", cancellationToken);
    }

    /// <summary>
    /// Ends every session and token the user has (after a reset or change of password, or "sign out everywhere"), but not
    /// one issued from now on: the caller usually signs the person in again right after. No-op when the user doesn't exist yet.
    /// </summary>
    public async Task EndSessionsAsync(string email, string actor, string detail, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == email, cancellationToken);
        if (user is null)
        {
            return;
        }

        // Sessions record their start in whole milliseconds; anything before this millisecond is ended.
        var now = Now;
        user.SessionsRevokedAtUtc = new DateTime(now.Ticks - now.Ticks % TimeSpan.TicksPerMillisecond - 1, DateTimeKind.Utc);
        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(email);
        await securityLog.RecordAsync(SecurityEventTypes.SessionsRevoked, email, detail: detail, actor: actor, cancellationToken: cancellationToken);
    }

    // ---------- One-time links ----------

    /// <summary>A new link token for an invite: the raw token goes in the email, only its hash is stored.</summary>
    public async Task<string> IssueInviteTokenAsync(UserInvite invite, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (raw, token) = NewToken(invite.Email, AccountTokenPurpose.Invite, invite.ExpiresAtUtc, Now);
        token.InviteId = invite.Id;
        db.Set<AccountToken>().Add(token);
        await db.SaveChangesAsync(cancellationToken);
        return raw;
    }

    /// <summary>A new token row and its raw value; for callers that save it themselves (tenant provisioning).</summary>
    public static (string Raw, AccountToken Token) NewToken(string email, AccountTokenPurpose purpose, DateTime expiresAtUtc, DateTime now)
    {
        var raw = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        return (raw, new AccountToken { Email = AccessControl.Normalize(email), Purpose = purpose, TokenHash = HashToken(raw), CreatedAtUtc = now, ExpiresAtUtc = expiresAtUtc });
    }

    /// <summary>
    /// Forgot password: emails a one-hour reset link when the email has an active account, a pending invite or is a
    /// bootstrap admin. The caller shows the same message either way; only the security log says which.
    /// </summary>
    public async Task RequestPasswordResetAsync(string emailAddress, string? baseUrl = null, string? requestedBy = null, CancellationToken cancellationToken = default)
    {
        var address = AccessControl.Normalize(emailAddress);
        if (address.Length == 0 || address.Length > 320 || !address.Contains('@'))
        {
            return;
        }

        var now = Now;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var user = await db.Set<AppUser>().AsNoTracking().FirstOrDefaultAsync(u => u.Email == address, cancellationToken);
        var invite = user is null
            ? await db.Set<UserInvite>().AsNoTracking().Where(i => i.Email == address && i.AcceptedAtUtc == null && i.RevokedAtUtc == null && i.ExpiresAtUtc > now)
                .OrderByDescending(i => i.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken)
            : null;
        var eligible = user is { IsActive: true } || invite is not null || (user is null && signInOptions.BootstrapAdmins.Contains(address));
        if (!eligible)
        {
            await securityLog.RecordAsync(SecurityEventTypes.PasswordResetRequested, address, SecurityOutcomes.Failure,
                user is null ? "No account, invite or bootstrap admin for this email; nothing sent" : "The account is deactivated; nothing sent",
                actor: requestedBy ?? "anonymous", cancellationToken: cancellationToken);
            return;
        }

        // A newer link replaces older ones.
        foreach (var old in await db.Set<AccountToken>().Where(t => t.Email == address && t.Purpose == AccountTokenPurpose.PasswordReset && t.UsedAtUtc == null && t.RevokedAtUtc == null)
            .ToListAsync(cancellationToken))
        {
            old.RevokedAtUtc = now;
        }

        var (raw, token) = NewToken(address, AccountTokenPurpose.PasswordReset, now + options.ResetLinkLifetime, now);
        db.Set<AccountToken>().Add(token);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.PasswordResetRequested, address, detail: requestedBy is null ? "Reset link sent" : $"Reset link sent by {requestedBy}",
            actor: requestedBy ?? "anonymous", cancellationToken: cancellationToken);

        var link = links.Absolute($"/account/reset-password?token={raw}", baseUrl);
        var minutes = (int)options.ResetLinkLifetime.TotalMinutes;
        var name = user?.DisplayName ?? invite?.DisplayName ?? address;
        var text = $"Hello {name},\n\nSomeone (hopefully you) asked to reset the Workbench password for {address}. Set a new password here within {minutes} minutes:\n\n{link}\n\n"
            + "The link works once. If you didn't ask for this, ignore this email: your password hasn't changed.\n";
        var html = $"<p>Hello {WebUtility.HtmlEncode(name)},</p><p>Someone (hopefully you) asked to reset the Workbench password for {WebUtility.HtmlEncode(address)}. "
            + $"Set a new password within {minutes} minutes:</p><p><a href=\"{WebUtility.HtmlEncode(link)}\">Reset your password</a></p>"
            + "<p>The link works once. If you didn't ask for this, ignore this email: your password hasn't changed.</p>";
        await SendAsync(new EmailMessage(address, "Reset your Workbench password", text, html) { Essential = true }, cancellationToken);
    }

    /// <summary>The token's row when it's unused, unexpired and for <paramref name="purpose"/>; otherwise null. Doesn't use it.</summary>
    public async Task<AccountToken?> FindTokenAsync(string? raw, AccountTokenPurpose purpose, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > 200)
        {
            return null;
        }

        var hash = HashToken(raw.Trim());
        var now = Now;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return await db.Set<AccountToken>().AsNoTracking()
            .FirstOrDefaultAsync(t => t.TokenHash == hash && t.Purpose == purpose && t.UsedAtUtc == null && t.RevokedAtUtc == null && t.ExpiresAtUtc > now, cancellationToken);
    }

    /// <summary>Uses the token up: true only for the one caller that got it while still valid.</summary>
    public async Task<bool> UseTokenAsync(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var now = Now;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var token = await db.Set<AccountToken>().FirstOrDefaultAsync(t => t.Id == tokenId && t.UsedAtUtc == null && t.RevokedAtUtc == null && t.ExpiresAtUtc > now, cancellationToken);
        if (token is null)
        {
            return false;
        }

        token.UsedAtUtc = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            return false; // Two requests raced with the same link: UsedAtUtc is a concurrency token, so only the first counts.
        }

        return true;
    }

    // ---------- Authenticator app ----------

    /// <summary>Starts (or restarts) setting up an authenticator app: a new secret, kept until a code confirms it.</summary>
    public async Task<TotpEnrollment> BeginTotpEnrollmentAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        var secret = Totp.NewSecret();
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await GetOrAddAsync(db, email, cancellationToken);
        credential.ProtectedPendingTotpSecret = Protector.Protect(Convert.ToBase64String(secret));
        credential.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        return new TotpEnrollment(Totp.Base32(secret), Totp.ProvisioningUri(Issuer, email, secret));
    }

    /// <summary>The enrollment in progress, so a page reload shows the same QR code. Null when none.</summary>
    public async Task<TotpEnrollment?> PendingTotpEnrollmentAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var pending = await db.Set<PasswordCredential>().AsNoTracking().Where(c => c.Email == email).Select(c => c.ProtectedPendingTotpSecret).FirstOrDefaultAsync(cancellationToken);
        return Unprotect(pending) is { } secret ? new TotpEnrollment(Totp.Base32(secret), Totp.ProvisioningUri(Issuer, email, secret)) : null;
    }

    /// <summary>Finishes the setup when <paramref name="code"/> matches the new secret: returns the recovery codes (shown once), or null for a wrong code.</summary>
    public async Task<IReadOnlyList<string>?> ConfirmTotpEnrollmentAsync(string email, string? code, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (Unprotect(credential?.ProtectedPendingTotpSecret) is not { } secret || Totp.Match(secret, code, clock.GetUtcNow(), null) is not { } step)
        {
            return null;
        }

        var replacing = credential!.ProtectedTotpSecret is not null;
        credential.ProtectedTotpSecret = credential.ProtectedPendingTotpSecret;
        credential.ProtectedPendingTotpSecret = null;
        credential.TotpEnabledAtUtc = Now;
        credential.LastTotpStep = step;
        credential.UpdatedAtUtc = Now;
        var codes = NewRecoveryCodes(credential);
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.MfaEnabled, email, detail: replacing ? "Authenticator app replaced" : "Authenticator app set up", actor: email,
            cancellationToken: cancellationToken);
        return codes;
    }

    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(string email, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email && c.ProtectedTotpSecret != null, cancellationToken)
            ?? throw new ArgumentException("Set up an authenticator app first.", nameof(email));
        var codes = NewRecoveryCodes(credential);
        credential.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.MfaRecoveryCodesRegenerated, email, actor: email, cancellationToken: cancellationToken);
        return codes;
    }

    /// <summary>
    /// Removes the authenticator app and recovery codes. <paramref name="actor"/> is the person themselves or the
    /// administrator resetting it (their next sign-in sets it up again when MFA is required). False when there was none.
    /// </summary>
    public async Task<bool> RemoveTotpAsync(string email, string actor, CancellationToken cancellationToken = default)
    {
        email = AccessControl.Normalize(email);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var credential = await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (credential is null || (credential.ProtectedTotpSecret is null && credential.ProtectedPendingTotpSecret is null))
        {
            return false;
        }

        credential.ProtectedTotpSecret = null;
        credential.ProtectedPendingTotpSecret = null;
        credential.TotpEnabledAtUtc = null;
        credential.LastTotpStep = null;
        credential.RecoveryCodeHashes = null;
        credential.UpdatedAtUtc = Now;
        await db.SaveChangesAsync(cancellationToken);
        await securityLog.RecordAsync(SecurityEventTypes.MfaDisabled, email, detail: actor == email ? "Removed by the user" : $"Reset by administrator {actor}", actor: actor,
            cancellationToken: cancellationToken);
        return true;
    }

    // ---------- Helpers ----------

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private async Task<PasswordCredential> GetOrAddAsync(IWorkbenchDbContext db, string email, CancellationToken cancellationToken)
    {
        var credential = await db.Set<PasswordCredential>().FirstOrDefaultAsync(c => c.Email == email, cancellationToken);
        if (credential is null)
        {
            credential = new PasswordCredential { Email = email, CreatedAtUtc = Now };
            db.Set<PasswordCredential>().Add(credential);
        }

        return credential;
    }

    private byte[]? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return Convert.FromBase64String(Protector.Unprotect(protectedSecret));
        }
        catch (CryptographicException ex)
        {
            // Keys lost or the row copied from another tenant: an administrator resets MFA and the person sets it up again.
            logger.LogWarning(ex, "Couldn't decrypt an authenticator secret");
            return null;
        }
    }

    private List<string> NewRecoveryCodes(PasswordCredential credential)
    {
        const string alphabet = "abcdefghjkmnpqrstuvwxyz23456789";
        var codes = Enumerable.Range(0, options.RecoveryCodeCount)
            .Select(_ => new string(Enumerable.Range(0, 10).Select(_ => alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)]).ToArray()))
            .Select(c => $"{c[..5]}-{c[5..]}")
            .ToList();
        credential.RecoveryCodeHashes = string.Join(' ', codes.Select(c => HashRecoveryCode(c)!));
        return codes;
    }

    private static List<string> RecoveryHashes(PasswordCredential credential) =>
        (credential.RecoveryCodeHashes ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

    private static string? HashRecoveryCode(string? code)
    {
        var normalized = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        return normalized.Length == 10 ? HashToken(normalized) : null;
    }

    private static string HashToken(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private async Task SendAsync(EmailMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await email.SendAsync(message, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't send the account email \"{Subject}\"", message.Subject);
        }
    }
}

/// <summary>
/// Absolute links for account emails: <c>Notifications:PublicBaseUrl</c> (per tenant when it has <c>{tenant}</c>), else
/// the base URL the caller saw the request on, else the last one the web host saw (a single-server install without the setting).
/// </summary>
public sealed class AccountLinks(NotificationOptions notifications)
{
    /// <summary>Set by the web host from incoming requests.</summary>
    public string? FallbackBaseUrl { get; set; }

    public string Absolute(string path, string? requestBaseUrl = null)
    {
        var root = notifications.PublicBaseUrl ?? requestBaseUrl ?? FallbackBaseUrl;
        return string.IsNullOrWhiteSpace(root) ? path : root.TrimEnd('/') + path;
    }
}

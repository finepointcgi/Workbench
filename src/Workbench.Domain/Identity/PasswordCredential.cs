namespace Workbench.Domain.Identity;

/// <summary>
/// A built-in Workbench account's sign-in secrets: the password hash (ASP.NET Core's PBKDF2 format), the lockout counter
/// and the authenticator app (TOTP). Keyed by email like <see cref="AppUser"/>, so a password can be set from an invite
/// before the user row exists (the first sign-in creates it). One row per email; missing means "no password".
/// </summary>
public sealed class PasswordCredential : Entity
{
    /// <summary>Lower-case, unique.</summary>
    public required string Email { get; set; }

    /// <summary>The hash from <c>PasswordHasher</c>, never the password. Null after an administrator removes the password.</summary>
    public string? PasswordHash { get; set; }

    public DateTime? PasswordChangedAtUtc { get; set; }

    /// <summary>Wrong passwords or codes since the last success; reaching the limit locks the account for a while.</summary>
    public int FailedAttempts { get; set; }

    public DateTime? LockedOutUntilUtc { get; set; }

    /// <summary>The authenticator app's shared secret, encrypted with the host's Data Protection keys. Null when MFA isn't set up.</summary>
    public string? ProtectedTotpSecret { get; set; }

    public DateTime? TotpEnabledAtUtc { get; set; }

    /// <summary>A secret shown during enrollment and kept until the first code confirms it.</summary>
    public string? ProtectedPendingTotpSecret { get; set; }

    /// <summary>The last accepted TOTP time step, so a code can't be used twice.</summary>
    public long? LastTotpStep { get; set; }

    /// <summary>SHA-256 hashes (hex, space-separated) of the unused recovery codes.</summary>
    public string? RecoveryCodeHashes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? UpdatedAtUtc { get; set; }
}

public enum AccountTokenPurpose
{
    /// <summary>Accept an invitation by setting a password.</summary>
    Invite,

    /// <summary>Forgot password: set a new one.</summary>
    PasswordReset,
}

/// <summary>
/// A one-time link sent by email (accept an invite, reset a password). Only the SHA-256 hash of the token is stored;
/// it expires, and using it (or a newer link, or a password change) ends it.
/// </summary>
public sealed class AccountToken : Entity
{
    public required string Email { get; set; }

    public AccountTokenPurpose Purpose { get; set; }

    /// <summary>Hex SHA-256 of the token in the link. Unique.</summary>
    public required string TokenHash { get; set; }

    /// <summary>The invite it accepts (<see cref="AccountTokenPurpose.Invite"/> only).</summary>
    public Guid? InviteId { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime ExpiresAtUtc { get; set; }

    public DateTime? UsedAtUtc { get; set; }

    /// <summary>Set when a newer link or a password change made this one useless before it was used.</summary>
    public DateTime? RevokedAtUtc { get; set; }
}

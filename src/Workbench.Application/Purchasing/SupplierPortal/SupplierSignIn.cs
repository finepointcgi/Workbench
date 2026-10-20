using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Purchasing;

namespace Workbench.Application.Purchasing;

/// <summary>Who signed in to the portal: what the web host puts in the portal cookie.</summary>
public sealed record SupplierSignInResult(Guid ContactId, Guid VendorId, string Name, int SessionVersion);

/// <summary>
/// Supplier portal sign-in, separate from internal users: a single-use emailed link (15 minutes; only its SHA-256 hash is
/// stored) or a password the contact set (PBKDF2, at least 12 characters; 5 wrong tries lock it for 15 minutes). Unknown
/// emails get the same answer as known ones. Deactivated contacts, and contacts of inactive vendors, can't sign in, and
/// <see cref="IsSessionValidAsync"/> ends their sessions at once. Every attempt is in the portal activity log.
/// </summary>
public sealed class SupplierSignIn(IWorkbenchDbContextFactory dbFactory, SupplierPortalMail mail, SupplierPortalLog log, TimeProvider clock)
{
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);
    public const int MinimumPasswordLength = 12;
    private const int MaxFailures = 5;
    private const int MaxLinksPerWindow = 5;
    private static readonly TimeSpan Lockout = TimeSpan.FromMinutes(15);
    private static readonly PasswordHasher<SupplierContact> Hasher = new();

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;

    /// <summary>Emails a sign-in link if the address belongs to an active contact. Says nothing either way.</summary>
    public async Task RequestLinkAsync(string? email, string? baseUrl, string? ip, CancellationToken ct = default)
    {
        var normalized = PurchasingSupport.Normalize(email);
        await using var db = await dbFactory.CreateAsync(ct);
        var (contact, vendor) = await FindActiveAsync(db, normalized, ct);
        if (contact is null || vendor is null)
        {
            await log.RecordAsync(null, null, Short(normalized), "signin.link_requested", outcome: "Denied", detail: "No active portal contact has this email.", ip: ip);
            return;
        }

        var since = UtcNow - LinkLifetime;
        if (await db.Set<SupplierSignInToken>().CountAsync(t => t.ContactId == contact.Id && t.CreatedAtUtc > since, ct) >= MaxLinksPerWindow)
        {
            await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.link_requested", outcome: "Denied", detail: "Too many links asked for; wait a few minutes.", ip: ip);
            return;
        }

        var token = await IssueTokenAsync(db, contact, ct);
        await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.link_sent", ip: ip);
        await mail.SignInLinkAsync(contact, vendor.Name, token, baseUrl, ct);
    }

    /// <summary>Used by buyers to (re)send a contact their link; returns nothing about the token.</summary>
    internal async Task SendLinkAsync(IWorkbenchDbContext db, SupplierContact contact, Vendor vendor, string invitedBy, CancellationToken ct)
    {
        var token = await IssueTokenAsync(db, contact, ct);
        await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.link_sent", detail: $"Sent by {invitedBy}");
        await mail.SignInLinkAsync(contact, vendor.Name, token, null, ct);
    }

    /// <summary>Signs in with a link token: once, before it expires, for a still-active contact.</summary>
    public async Task<SupplierSignInResult?> RedeemLinkAsync(string? token, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 200)
        {
            return null;
        }

        var hash = Hash(token);
        await using var db = await dbFactory.CreateAsync(ct);
        var row = await db.Set<SupplierSignInToken>().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.UsedAtUtc is not null || row.ExpiresAtUtc < UtcNow)
        {
            await log.RecordAsync(null, null, "(link)", "signin.link", outcome: "Denied",
                detail: row is null ? "Unknown link." : row.UsedAtUtc is not null ? "The link was already used." : "The link expired.", ip: ip);
            return null;
        }

        row.UsedAtUtc = UtcNow;
        var contact = await db.Set<SupplierContact>().FirstAsync(c => c.Id == row.ContactId, ct);
        var vendor = await db.Set<Vendor>().AsNoTracking().FirstAsync(v => v.Id == contact.VendorId, ct);
        if (!contact.IsActive || vendor.Status == VendorStatus.Inactive)
        {
            await db.SaveChangesAsync(ct);
            await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.link", outcome: "Denied", detail: "The contact or vendor is inactive.", ip: ip);
            return null;
        }

        contact.LastSignInUtc = UtcNow;
        contact.FailedSignIns = 0;
        contact.LockedUntilUtc = null;
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.link", ip: ip);
        return new SupplierSignInResult(contact.Id, vendor.Id, contact.Name, contact.SessionVersion);
    }

    public async Task<SupplierSignInResult?> PasswordSignInAsync(string? email, string? password, string? ip, CancellationToken ct = default)
    {
        var normalized = PurchasingSupport.Normalize(email);
        await using var db = await dbFactory.CreateAsync(ct);
        var (contact, vendor) = await FindActiveAsync(db, normalized, ct, track: true);
        if (contact is null || vendor is null || contact.PasswordHash is null || string.IsNullOrEmpty(password))
        {
            await log.RecordAsync(contact?.Id, vendor?.Id, Short(normalized), "signin.password", outcome: "Denied",
                detail: contact is null ? "No active portal contact has this email." : "No password is set.", ip: ip);
            return null;
        }

        if (contact.LockedUntilUtc is { } until && until > UtcNow)
        {
            await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.password", outcome: "Denied", detail: "Locked after too many wrong passwords.", ip: ip);
            return null;
        }

        var verified = Hasher.VerifyHashedPassword(contact, contact.PasswordHash, password);
        if (verified == PasswordVerificationResult.Failed)
        {
            contact.FailedSignIns++;
            if (contact.FailedSignIns >= MaxFailures)
            {
                contact.LockedUntilUtc = UtcNow + Lockout;
                contact.FailedSignIns = 0;
            }

            await db.SaveChangesAsync(ct);
            await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.password", outcome: "Denied",
                detail: contact.LockedUntilUtc is not null ? "Wrong password; locked for 15 minutes." : "Wrong password.", ip: ip);
            return null;
        }

        if (verified == PasswordVerificationResult.SuccessRehashNeeded)
        {
            contact.PasswordHash = Hasher.HashPassword(contact, password);
        }

        contact.FailedSignIns = 0;
        contact.LockedUntilUtc = null;
        contact.LastSignInUtc = UtcNow;
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(contact.Id, vendor.Id, contact.Email, "signin.password", ip: ip);
        return new SupplierSignInResult(contact.Id, vendor.Id, contact.Name, contact.SessionVersion);
    }

    /// <summary>
    /// Sets the signed-in contact's password (the current one is required when one is set). Ends the contact's other
    /// sessions; the returned result re-issues this one.
    /// </summary>
    public async Task<SupplierSignInResult> SetPasswordAsync(Guid contactId, string? currentPassword, string? newPassword, string? ip, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length < MinimumPasswordLength || newPassword.Length > 256)
        {
            throw new ArgumentException($"Use a password of at least {MinimumPasswordLength} characters.");
        }

        await using var db = await dbFactory.CreateAsync(ct);
        var contact = await db.Set<SupplierContact>().FirstOrDefaultAsync(c => c.Id == contactId && c.IsActive, ct)
            ?? throw new UnauthorizedAccessException("Sign in again.");
        if (contact.PasswordHash is not null &&
            (string.IsNullOrEmpty(currentPassword) || Hasher.VerifyHashedPassword(contact, contact.PasswordHash, currentPassword) == PasswordVerificationResult.Failed))
        {
            await log.RecordAsync(contact.Id, contact.VendorId, contact.Email, "password.changed", outcome: "Denied", detail: "The current password was wrong.", ip: ip);
            throw new ArgumentException("The current password isn't right.");
        }

        if (string.Equals(newPassword, contact.Email, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Don't use your email address as your password.");
        }

        contact.PasswordHash = Hasher.HashPassword(contact, newPassword);
        contact.SessionVersion++;
        await db.SaveChangesAsync(ct);
        await log.RecordAsync(contact.Id, contact.VendorId, contact.Email, "password.changed", ip: ip);
        return new SupplierSignInResult(contact.Id, contact.VendorId, contact.Name, contact.SessionVersion);
    }

    /// <summary>For every portal request: the contact is still active, its vendor isn't inactive, and the session isn't revoked.</summary>
    public async Task<bool> IsSessionValidAsync(Guid contactId, Guid vendorId, int sessionVersion, CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateAsync(ct);
        return await db.Set<SupplierContact>().AsNoTracking()
            .Where(c => c.Id == contactId && c.VendorId == vendorId && c.IsActive && c.SessionVersion == sessionVersion)
            .Join(db.Set<Vendor>(), c => c.VendorId, v => v.Id, (c, v) => v.Status)
            .AnyAsync(s => s != VendorStatus.Inactive, ct);
    }

    public async Task RecordSignOutAsync(Guid contactId, string? ip)
    {
        await using var db = await dbFactory.CreateAsync(CancellationToken.None);
        var contact = await db.Set<SupplierContact>().AsNoTracking().FirstOrDefaultAsync(c => c.Id == contactId);
        if (contact is not null)
        {
            await log.RecordAsync(contact.Id, contact.VendorId, contact.Email, "signout", ip: ip);
        }
    }

    /// <summary>Sets a contact's password directly (development and demo seeds only).</summary>
    public static string HashForSeed(SupplierContact contact, string password) => Hasher.HashPassword(contact, password);

    private async Task<string> IssueTokenAsync(IWorkbenchDbContext db, SupplierContact contact, CancellationToken ct)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        db.Set<SupplierSignInToken>().Add(new SupplierSignInToken
        {
            ContactId = contact.Id,
            TokenHash = Hash(token),
            CreatedAtUtc = UtcNow,
            ExpiresAtUtc = UtcNow + LinkLifetime,
        });

        // Old, used or expired tokens are no use to anyone: drop them while we're here.
        var stale = UtcNow - TimeSpan.FromDays(1);
        db.Set<SupplierSignInToken>().RemoveRange(await db.Set<SupplierSignInToken>().Where(t => t.ContactId == contact.Id && t.ExpiresAtUtc < stale).ToListAsync(ct));
        await db.SaveChangesAsync(ct);
        return token;
    }

    private static async Task<(SupplierContact? Contact, Vendor? Vendor)> FindActiveAsync(IWorkbenchDbContext db, string email, CancellationToken ct, bool track = false)
    {
        if (email.Length == 0 || !email.Contains('@'))
        {
            return (null, null);
        }

        var contacts = track ? db.Set<SupplierContact>() : db.Set<SupplierContact>().AsNoTracking();
        var contact = await contacts.FirstOrDefaultAsync(c => c.Email == email && c.IsActive, ct);
        if (contact is null)
        {
            return (null, null);
        }

        var vendor = await db.Set<Vendor>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == contact.VendorId && v.Status != VendorStatus.Inactive, ct);
        return vendor is null ? (null, null) : (contact, vendor);
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private static string Short(string email) => email.Length == 0 ? "(blank)" : email.Length > 320 ? email[..320] : email;
}

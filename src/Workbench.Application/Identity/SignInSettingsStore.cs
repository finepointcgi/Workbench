using System.Collections.Concurrent;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Identity;
using Workbench.Domain.Identity;

namespace Workbench.Application.Identity;

/// <summary>What the signed-in user's session proved, read by the host from its claims.</summary>
/// <param name="AuthenticatedAtUtc">When they last authenticated at the identity provider (or the dev sign-in).</param>
/// <param name="Method"><c>sso</c> (the organization's IdP), <c>oidc</c> (the server's), <c>dev</c> or <c>bearer</c>.</param>
public sealed record SignInFacts(DateTime? AuthenticatedAtUtc, bool MfaPerformed, string? Method);

/// <summary>The current session's <see cref="SignInFacts"/>. Null when no interactive user is involved (background jobs, in-process tools).</summary>
public interface ISignInContext
{
    SignInFacts? Current { get; }
}

/// <summary>Used when the host doesn't provide one: in-process callers and tests, which have no session to age.</summary>
internal sealed class InProcessSignInContext : ISignInContext
{
    public SignInFacts? Current => null;
}

/// <summary>Server-wide sign-in facts the web host knows (shown on the settings page). Defaults for other hosts.</summary>
public sealed class SignInServerInfo
{
    public bool ServerOidcConfigured { get; init; }

    public int IdleTimeoutMinutes { get; init; } = 30;

    public int AbsoluteLifetimeHours { get; init; } = 12;
}

/// <summary>A tenant's sign-in settings as read at one moment. Replaced (never mutated) when they change.</summary>
public sealed class SignInPolicySnapshot(SignInSettings settings, IReadOnlyList<SsoGroupMapping> mappings, string? clientSecret)
{
    public static readonly SignInPolicySnapshot Default = new(new SignInSettings(), [], null);

    public SignInSettings Settings { get; } = settings;

    public IReadOnlyList<SsoGroupMapping> Mappings { get; } = mappings;

    /// <summary>The decrypted client secret, kept only in memory for the OIDC handler.</summary>
    internal string? ClientSecret { get; } = clientSecret;

    /// <summary>True when the organization's own IdP is enabled and complete.</summary>
    public bool OrganizationIdpReady =>
        Settings.SsoEnabled && !string.IsNullOrWhiteSpace(Settings.Authority) && !string.IsNullOrWhiteSpace(Settings.ClientId);

    public string? GetClientSecret() => ClientSecret;
}

/// <summary>Encrypts the IdP client secret with the host's Data Protection keys (isolated per tenant in MultiTenant mode).</summary>
public sealed class SsoSecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("Workbench.Sso.ClientSecret.v1");

    public string Protect(string secret) => _protector.Protect(secret);

    public string? TryUnprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedSecret);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            // Keys lost or the row copied from another tenant: the admin must enter the secret again.
            return null;
        }
    }
}

/// <summary>
/// Caches each tenant's <see cref="SignInPolicySnapshot"/> (singleton, keyed by tenant). Saves through
/// <see cref="ISignInSettingsService"/> invalidate it at once; other server instances pick changes up within <see cref="Ttl"/>.
/// </summary>
public sealed class SignInSettingsStore(IWorkbenchDbContextFactory dbFactory, SsoSecretProtector secrets, TimeProvider clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);
    private readonly ConcurrentDictionary<string, (SignInPolicySnapshot Snapshot, DateTime LoadedAtUtc)> _entries = new();

    public async Task<SignInPolicySnapshot> GetAsync(CancellationToken cancellationToken = default)
    {
        var key = Tenancy.TenantScope.Qualify("sign-in");
        var now = clock.GetUtcNow().UtcDateTime;
        if (_entries.TryGetValue(key, out var cached) && now - cached.LoadedAtUtc < Ttl)
        {
            return cached.Snapshot;
        }

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var settings = await db.Set<SignInSettings>().AsNoTracking().OrderBy(s => s.Id).FirstOrDefaultAsync(cancellationToken);
        var snapshot = settings is null
            ? SignInPolicySnapshot.Default
            : new SignInPolicySnapshot(settings, await db.Set<SsoGroupMapping>().AsNoTracking().OrderBy(m => m.GroupValue).ToListAsync(cancellationToken),
                secrets.TryUnprotect(settings.ProtectedClientSecret));

        // Keep the same instance while nothing changed, so per-tenant OIDC options built from it stay cached.
        if (cached.Snapshot is { } previous && previous.Settings.UpdatedAtUtc == snapshot.Settings.UpdatedAtUtc && previous.Mappings.Count == snapshot.Mappings.Count
            && previous.Settings.Id == snapshot.Settings.Id)
        {
            snapshot = previous;
        }

        _entries[key] = (snapshot, now);
        return snapshot;
    }

    /// <summary>The cached snapshot without waiting, or null when it must be loaded first.</summary>
    public SignInPolicySnapshot? Peek() =>
        _entries.TryGetValue(Tenancy.TenantScope.Qualify("sign-in"), out var cached) && clock.GetUtcNow().UtcDateTime - cached.LoadedAtUtc < Ttl ? cached.Snapshot : null;

    public void Invalidate() => _entries.TryRemove(Tenancy.TenantScope.Qualify("sign-in"), out _);
}

/// <summary>The claims about how someone authenticated, passed from the IdP's token to the sign-in gate.</summary>
/// <param name="Groups">The groups claim's values, or null when the token had none and roles shouldn't be synced.</param>
public sealed record SignInClaims(IReadOnlyCollection<string> Amr, IReadOnlyCollection<string> Acr, IReadOnlyCollection<string>? Groups)
{
    public static readonly SignInClaims None = new([], [], null);
}

/// <summary>The pure sign-in rules: MFA proof, just-in-time domains and group-to-role mappings.</summary>
public static class SignInRules
{
    private static readonly char[] Separators = [' ', ',', ';', '\n', '\r', '\t'];

    public static IReadOnlyList<string> SplitList(string? values) =>
        (values ?? string.Empty).Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>True when the token's <c>amr</c> or <c>acr</c>/<c>acrs</c> values include one the organization accepts as MFA.</summary>
    public static bool MfaSatisfied(SignInSettings settings, SignInClaims claims)
    {
        var amr = SplitList(settings.AcceptedAmrValues);
        if (claims.Amr.Any(a => amr.Contains(a, StringComparer.OrdinalIgnoreCase)))
        {
            return true;
        }

        var acr = SplitList(settings.AcceptedAcrValues);
        return claims.Acr.Any(a => acr.Contains(a, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Whether MFA is required for someone with <paramref name="roles"/>.</summary>
    public static bool MfaRequired(SignInSettings settings, OrgRoles roles) => settings.MfaRequirement switch
    {
        MfaRequirementLevel.Everyone => true,
        MfaRequirementLevel.AdminsOnly => roles.IsAdmin(),
        _ => false,
    };

    public static bool DomainAllowed(SignInSettings settings, string email)
    {
        if (settings.JitMode != JitProvisioning.AllowedDomains)
        {
            return false;
        }

        var at = email.LastIndexOf('@');
        var domain = at < 0 ? string.Empty : email[(at + 1)..];
        return domain.Length > 0 && SplitList(settings.AllowedEmailDomains).Any(d => string.Equals(d.TrimStart('@'), domain, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The roles the groups grant (case-sensitive: Entra object ids and Okta group names are exact).</summary>
    public static OrgRoles MappedRoles(IReadOnlyList<SsoGroupMapping> mappings, IReadOnlyCollection<string>? groups)
    {
        var roles = OrgRoles.Employee;
        if (groups is null)
        {
            return roles;
        }

        foreach (var mapping in mappings.Where(m => groups.Contains(m.GroupValue, StringComparer.Ordinal)))
        {
            roles |= (OrgRoles)mapping.Roles;
        }

        return roles;
    }

    /// <summary>The roles the IdP decides: the default roles plus every mapped group's.</summary>
    public static OrgRoles RolesFromIdp(SignInPolicySnapshot policy, IReadOnlyCollection<string>? groups) =>
        (OrgRoles)policy.Settings.JitDefaultRoles | MappedRoles(policy.Mappings, groups);
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.Identity;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Collaboration;
using Workbench.Contracts.Identity;
using Workbench.Contracts.Integrations;
using Workbench.Contracts.Security;
using Workbench.Domain.Identity;
using Workbench.Domain.Integrations;

namespace Workbench.Application.Integrations;

/// <summary>How API keys appear as users: a service AppUser per key on a reserved domain nobody can sign in with.</summary>
public static class ApiKeyPrincipal
{
    public const string EmailDomain = "api-keys.workbench.invalid";
    public const string KeyIdClaim = "wb_api_key";
    public const string ScopesClaim = "wb_api_scopes";

    public static string EmailFor(Guid keyId) => $"key-{keyId:N}@{EmailDomain}";

    public static bool IsServiceAccount(string? email) =>
        email is not null && email.EndsWith("@" + EmailDomain, StringComparison.OrdinalIgnoreCase);

    /// <summary>The roles a key's service user holds: its built-in roles plus its custom roles that still exist.</summary>
    internal static async Task<IReadOnlyList<Guid>> RoleIdsOfAsync(IWorkbenchDbContext db, ApiKey key, CancellationToken ct)
    {
        var custom = RoleStore.ParseIds(key.CustomRoleIds).ToList();
        var existing = custom.Count == 0 ? [] : await db.Set<Role>().AsNoTracking().Where(r => custom.Contains(r.Id)).Select(r => r.Id).ToListAsync(ct);
        return RoleGrant.Of((OrgRoles)key.Roles, existing).RoleIds;
    }

    /// <summary>Makes the service user hold exactly the key's roles (tracked, not saved). True when something changed.</summary>
    internal static async Task<bool> SyncRolesAsync(IWorkbenchDbContext db, AppUser user, ApiKey key, DateTime now, CancellationToken ct)
    {
        var wanted = await RoleIdsOfAsync(db, key, ct);
        var held = await RoleStore.RoleIdsOfAsync(db, user.Id, ct);
        if (held.ToHashSet().SetEquals(wanted))
        {
            return false;
        }

        await RoleStore.ReplaceAsync(db, user, wanted, "api-key", now, ct);
        return true;
    }
}

/// <summary>Issues and revokes API keys (Admin › Integrations › API keys). Administrators only (the permission decorator).</summary>
public sealed class ApiKeyService(
    IWorkbenchDbContextFactory dbFactory,
    ICurrentUser currentUser,
    SecurityEventLog securityLog,
    UserStatusCache statusCache,
    TimeProvider clock) : IApiKeyService
{
    private static readonly OrgRoles KnownRoles = OrgRoleChecks.Everything;
    private DateTime Now => clock.GetUtcNow().UtcDateTime;
    private string Me => currentUser.Email.Trim().ToLowerInvariant();

    public async Task<IReadOnlyList<ApiKeyDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var keys = await db.Set<ApiKey>().AsNoTracking().OrderByDescending(k => k.CreatedAtUtc).ToListAsync(cancellationToken);
        var names = (await RoleStore.RefsAsync(db, keys.SelectMany(k => RoleStore.ParseIds(k.CustomRoleIds)), cancellationToken)).ToDictionary(r => r.Id);
        return keys.Select(k => ToDto(k, Now, names)).ToList();
    }

    public async Task<ApiKeyCreatedDto> CreateAsync(CreateApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200)
        {
            throw new ArgumentException("Give the key a name that says what uses it (200 characters at most).");
        }

        var scopes = ApiKeyScopes.Normalize(request.Scopes ?? []);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("Choose at least one area the key can read or write.");
        }

        if ((request.Roles & ~KnownRoles) != 0)
        {
            throw new ArgumentException("That role doesn't exist.");
        }

        var now = Now;
        if (request.ExpiresAtUtc is { } expires && (expires <= now || expires > now.AddYears(3)))
        {
            throw new ArgumentException("The expiry date must be in the future and within three years.");
        }

        var key = IntegrationSecrets.NewApiKey();
        var row = new ApiKey
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(request.Description) ? null : request.Description.Trim()[..Math.Min(request.Description.Trim().Length, 1000)],
            Prefix = IntegrationSecrets.DisplayPrefix(key),
            KeyHash = IntegrationSecrets.HashApiKey(key),
            Roles = (int)request.Roles,
            Scopes = string.Join(',', scopes),
            ServiceUserEmail = "",
            CreatedBy = Me,
            CreatedAtUtc = now,
            ExpiresAtUtc = request.ExpiresAtUtc is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : null,
        };
        row.ServiceUserEmail = ApiKeyPrincipal.EmailFor(row.Id);

        var user = new AppUser { Email = row.ServiceUserEmail, DisplayName = $"API key: {name}"[..Math.Min(name.Length + 9, 200)], CreatedAtUtc = now, IsActive = true };

        await using var db = await dbFactory.CreateAsync(cancellationToken);
        row.CustomRoleIds = RoleStore.FormatIds(await RoleStore.ValidateCustomAsync(db, request.CustomRoleIds, cancellationToken));
        db.Set<AppUser>().Add(user);
        db.Set<ApiKey>().Add(row);
        await ApiKeyPrincipal.SyncRolesAsync(db, user, row, now, cancellationToken);
        var refs = await RoleStore.RefsAsync(db, await ApiKeyPrincipal.RoleIdsOfAsync(db, row, cancellationToken), cancellationToken);
        RoleStore.RecordAssignmentAudit(db, user, [], refs, Me, now);
        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(row.ServiceUserEmail);
        await securityLog.RecordAsync(SecurityEventTypes.ApiKeyCreated, $"{row.Prefix}… {name}",
            detail: $"Scopes: {row.Scopes}; roles: {RoleStore.Describe(refs)}; service user {row.ServiceUserEmail}"
                + (row.ExpiresAtUtc is { } x ? $"; expires {x:yyyy-MM-dd}" : ""),
            cancellationToken: cancellationToken);
        return new ApiKeyCreatedDto(ToDto(row, now, refs.ToDictionary(r => r.Id)), key);
    }

    public async Task<ApiKeyDto> RevokeAsync(Guid apiKeyId, RevokeApiKeyRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var row = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.Id == apiKeyId, cancellationToken) ?? throw WorkbenchNotFoundException.For("API key", apiKeyId);
        if (row.RevokedAtUtc is not null)
        {
            return ToDto(row, Now, new Dictionary<Guid, RoleRef>());
        }

        var now = Now;
        var reason = string.IsNullOrWhiteSpace(request.Reason) ? null : request.Reason.Trim()[..Math.Min(request.Reason.Trim().Length, 500)];
        row.RevokedAtUtc = now;
        row.RevokedBy = Me;
        row.RevokedReason = reason;
        if (await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == row.ServiceUserEmail, cancellationToken) is { } user)
        {
            user.IsActive = false;
            user.DeactivatedAtUtc = now;
            user.DeactivatedBy = Me;
            user.SessionsRevokedAtUtc = now;
        }

        await db.SaveChangesAsync(cancellationToken);
        statusCache.Invalidate(row.ServiceUserEmail);
        await securityLog.RecordAsync(SecurityEventTypes.ApiKeyRevoked, $"{row.Prefix}… {row.Name}", detail: reason, cancellationToken: cancellationToken);
        return ToDto(row, now, (await RoleStore.RefsAsync(db, RoleStore.ParseIds(row.CustomRoleIds), cancellationToken)).ToDictionary(r => r.Id));
    }

    internal static ApiKeyDto ToDto(ApiKey k, DateTime now, IReadOnlyDictionary<Guid, RoleRef> roleNames) => new(
        k.Id, k.Name, k.Description, k.Prefix, (OrgRoles)k.Roles, k.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries), k.ServiceUserEmail, k.CreatedBy,
        k.CreatedAtUtc, k.ExpiresAtUtc, k.RevokedAtUtc, k.RevokedBy, k.LastUsedAtUtc, k.LastUsedFrom, k.IsUsable(now),
        RoleStore.ParseIds(k.CustomRoleIds).Select(roleNames.GetValueOrDefault).OfType<RoleRef>().ToList());
}

/// <summary>Who a valid API key is: its service user and scopes.</summary>
public sealed record ApiKeyIdentity(Guid KeyId, string Prefix, string Name, string Email, string DisplayName, IReadOnlyList<string> Scopes);

/// <summary>
/// Checks a presented API key (the web host's authentication handler calls it). Unknown, revoked and expired keys and keys
/// whose service user was deactivated are refused and recorded as <c>apikey.rejected</c>. The key's roles are copied back
/// onto its service user if someone changed them elsewhere, so the key always acts with exactly the roles it was issued.
/// </summary>
public sealed class ApiKeyAuthenticator(IWorkbenchDbContextFactory dbFactory, SecurityEventLog securityLog, TimeProvider clock)
{
    public static readonly TimeSpan LastUsedResolution = TimeSpan.FromMinutes(1);

    public static bool LooksLikeKey(string? value) =>
        value is not null && value.StartsWith(IntegrationSecrets.ApiKeyPrefix, StringComparison.Ordinal) && value.Length is > 20 and < 200;

    public async Task<(ApiKeyIdentity? Identity, string? Failure)> AuthenticateAsync(string presented, string? remoteAddress, CancellationToken cancellationToken)
    {
        presented = presented.Trim();
        if (!LooksLikeKey(presented))
        {
            return (null, "Not a Workbench API key.");
        }

        var prefix = IntegrationSecrets.DisplayPrefix(presented);
        var hash = IntegrationSecrets.HashApiKey(presented);
        var now = clock.GetUtcNow().UtcDateTime;
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var key = await db.Set<ApiKey>().FirstOrDefaultAsync(k => k.KeyHash == hash, cancellationToken);
        string? failure = key switch
        {
            null => "Unknown API key.",
            { RevokedAtUtc: not null } => "This API key was revoked.",
            { ExpiresAtUtc: { } expires } when expires <= now => "This API key has expired.",
            _ => null,
        };

        var user = key is null ? null : await db.Set<AppUser>().FirstOrDefaultAsync(u => u.Email == key.ServiceUserEmail, cancellationToken);
        if (failure is null && user is not { IsActive: true })
        {
            failure = "This API key's service user is deactivated.";
        }

        if (failure is not null)
        {
            await securityLog.RecordAsync(SecurityEventTypes.ApiKeyRejected, key is null ? $"{prefix}…" : $"{key.Prefix}… {key.Name}", SecurityOutcomes.Denied,
                failure, actor: key?.ServiceUserEmail ?? "api-key", cancellationToken: cancellationToken);
            return (null, failure);
        }

        var changed = await ApiKeyPrincipal.SyncRolesAsync(db, user!, key!, now, cancellationToken);

        if (key.LastUsedAtUtc is not { } last || now - last >= LastUsedResolution || key.LastUsedFrom != Clip(remoteAddress))
        {
            key.LastUsedAtUtc = now;
            key.LastUsedFrom = Clip(remoteAddress);
            user!.LastSeenAtUtc = now;
            changed = true;
        }

        if (changed)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return (new ApiKeyIdentity(key.Id, key.Prefix, key.Name, key.ServiceUserEmail, user!.DisplayName, key.Scopes.Split(',', StringSplitOptions.RemoveEmptyEntries)), null);
    }

    private static string? Clip(string? text) => text is null || text.Length <= 64 ? text : text[..64];
}

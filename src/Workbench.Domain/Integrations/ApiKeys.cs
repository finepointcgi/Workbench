namespace Workbench.Domain.Integrations;

/// <summary>
/// A revocable service credential for the REST API. The key acts as its own service user (<see cref="ServiceUserEmail"/>,
/// an <c>AppUser</c> with the chosen organization roles), so every permission decorator applies to it as to a person; the
/// scopes then narrow which API areas it may read or write. Only a SHA-256 hash of the key is stored.
/// </summary>
public sealed class ApiKey : Entity
{
    public required string Name { get; set; }
    public string? Description { get; set; }

    /// <summary>The key's first characters (<c>wbk_</c> + 8), shown in lists and logs to tell keys apart.</summary>
    public required string Prefix { get; set; }

    /// <summary>Lower-case hex SHA-256 of the whole key. Unique.</summary>
    public required string KeyHash { get; set; }

    /// <summary>Organization roles of the service user (<c>OrgRoles</c> flags), copied to its AppUser.</summary>
    public int Roles { get; set; }

    /// <summary>Granted scopes, comma-separated: <c>area:read</c> or <c>area:write</c> (write includes read).</summary>
    public required string Scopes { get; set; }

    /// <summary>The AppUser this key signs in as (<c>key-…@api-keys.workbench.invalid</c>).</summary>
    public required string ServiceUserEmail { get; set; }

    public required string CreatedBy { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ExpiresAtUtc { get; set; }
    public DateTime? RevokedAtUtc { get; set; }
    public string? RevokedBy { get; set; }
    public string? RevokedReason { get; set; }

    /// <summary>Updated at most once a minute per key.</summary>
    public DateTime? LastUsedAtUtc { get; set; }

    public string? LastUsedFrom { get; set; }

    public bool IsUsable(DateTime now) => RevokedAtUtc is null && (ExpiresAtUtc is null || ExpiresAtUtc > now);
}

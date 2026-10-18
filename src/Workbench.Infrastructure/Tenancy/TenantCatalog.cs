using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Tenancy;

namespace Workbench.Infrastructure.Tenancy;

/// <summary>A tenant as the host admin sees it.</summary>
public sealed record TenantSummary(
    Guid Id, string Slug, string DisplayName, TenantStatus Status, string? Plan, string? Notes, DateTime CreatedAtUtc,
    DateTime? StatusChangedAtUtc, string? StatusChangedBy);

/// <summary>
/// Reads the catalog: resolves a slug to a tenant (cached for <see cref="CacheTtl"/>, so suspending a tenant takes
/// effect on other server instances within that time and at once on this one) and decrypts its connection string.
/// </summary>
public sealed partial class TenantCatalog(IDbContextFactory<CatalogDbContext> catalog, IDataProtectionProvider dataProtection, TimeProvider clock)
{
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    /// <summary>Purpose for tenant connection strings. Uses the root provider, never a tenant-scoped one.</summary>
    public const string ConnectionStringPurpose = "Workbench.Tenancy.ConnectionString.v1";

    private static readonly HashSet<string> Reserved = ["www", "host", "admin", "api", "app", "auth", "login", "mail", "static", "status", "support"];
    private readonly ConcurrentDictionary<string, (Entry? Entry, DateTime FetchedAtUtc)> _bySlug = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, string> _connectionStrings = new();
    private readonly IDataProtector _protector = TenantDataProtectionProvider.RootOf(dataProtection).CreateProtector(ConnectionStringPurpose);

    public sealed record Entry(TenantInfo Info, TenantStatus Status);

    /// <summary>The tenant with this slug (any status), or null. Slugs are compared lowercased.</summary>
    public async Task<Entry?> FindAsync(string slug, CancellationToken cancellationToken = default)
    {
        slug = slug.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow().UtcDateTime;
        if (_bySlug.TryGetValue(slug, out var cached) && now - cached.FetchedAtUtc < CacheTtl)
        {
            return cached.Entry;
        }

        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var row = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Slug == slug, cancellationToken);
        var entry = row is null ? null : new Entry(new TenantInfo(row.Id, row.Slug, row.DisplayName), row.Status);
        if (row is not null)
        {
            _connectionStrings[row.Id] = _protector.Unprotect(row.ProtectedConnectionString);
        }

        _bySlug[slug] = (entry, now);
        return entry;
    }

    /// <summary>The tenant database's connection string (decrypted).</summary>
    public string GetConnectionString(TenantInfo tenant)
    {
        if (_connectionStrings.TryGetValue(tenant.Id, out var cached))
        {
            return cached;
        }

        using var db = catalog.CreateDbContext();
        var row = db.Tenants.AsNoTracking().FirstOrDefault(t => t.Id == tenant.Id)
            ?? throw new InvalidOperationException($"Tenant {tenant.Slug} isn't in the catalog.");
        return _connectionStrings[tenant.Id] = _protector.Unprotect(row.ProtectedConnectionString);
    }

    public async Task<IReadOnlyList<TenantInfo>> ListActiveAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var rows = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).OrderBy(t => t.Slug).ToListAsync(cancellationToken);
        foreach (var row in rows)
        {
            _connectionStrings.TryAdd(row.Id, _protector.Unprotect(row.ProtectedConnectionString));
        }

        return rows.Select(r => new TenantInfo(r.Id, r.Slug, r.DisplayName)).ToList();
    }

    public async Task<IReadOnlyList<TenantSummary>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await catalog.CreateDbContextAsync(cancellationToken);
        var rows = await db.Tenants.AsNoTracking().OrderBy(t => t.Slug).ToListAsync(cancellationToken);
        return rows.Select(ToSummary).ToList();
    }

    public string Protect(string connectionString) => _protector.Protect(connectionString);

    /// <summary>Forgets a cached tenant so the next request reads its new status.</summary>
    public void Invalidate(string slug, Guid? id = null)
    {
        _bySlug.TryRemove(slug.Trim().ToLowerInvariant(), out _);
        if (id is { } tenantId)
        {
            _connectionStrings.TryRemove(tenantId, out _);
        }
    }

    /// <summary>
    /// A valid new slug: 3–40 lowercase letters, digits and inner hyphens, starting with a letter, and not reserved.
    /// Throws <see cref="ArgumentException"/> otherwise.
    /// </summary>
    public static string ValidateSlug(string? slug)
    {
        var value = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!SlugPattern().IsMatch(value))
        {
            throw new ArgumentException("A tenant id is 3–40 lowercase letters, digits and hyphens, starting with a letter (it becomes the subdomain).");
        }

        if (Reserved.Contains(value))
        {
            throw new ArgumentException($"'{value}' is reserved. Choose another tenant id.");
        }

        return value;
    }

    internal static TenantSummary ToSummary(Tenant t) =>
        new(t.Id, t.Slug, t.DisplayName, t.Status, t.Plan, t.Notes, t.CreatedAtUtc, t.StatusChangedAtUtc, t.StatusChangedBy);

    [GeneratedRegex("^[a-z][a-z0-9-]{1,38}[a-z0-9]$")]
    private static partial Regex SlugPattern();
}

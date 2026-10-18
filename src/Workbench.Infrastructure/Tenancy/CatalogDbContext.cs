using Microsoft.EntityFrameworkCore;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Infrastructure.Tenancy;

public enum TenantStatus
{
    /// <summary>Being created: its database is migrated and seeded. Not reachable yet.</summary>
    Provisioning,
    Active,
    /// <summary>Reachable by nobody; background jobs skip it. Data is kept.</summary>
    Suspended,
    /// <summary>Provisioning failed; see <see cref="Tenant.Notes"/>. Retry by creating it again after removing the row.</summary>
    Failed,
}

/// <summary>A customer organization in MultiTenant mode. Lives in the catalog database, never in a tenant database.</summary>
public sealed class Tenant
{
    public Guid Id { get; set; } = Guid.CreateVersion7();

    /// <summary>Lowercase subdomain, e.g. <c>acme</c> for acme.workbench.example. Unique and never reused.</summary>
    public required string Slug { get; set; }

    public required string DisplayName { get; set; }

    public TenantStatus Status { get; set; } = TenantStatus.Provisioning;

    /// <summary>The tenant database's connection string, encrypted with ASP.NET Core Data Protection.</summary>
    public required string ProtectedConnectionString { get; set; }

    public string? Plan { get; set; }

    public string? Notes { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime? StatusChangedAtUtc { get; set; }

    public string? StatusChangedBy { get; set; }
}

/// <summary>Append-only log of host administration (tenant created, suspended, reactivated).</summary>
public sealed class HostAuditEvent
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public DateTime AtUtc { get; set; }
    public required string Actor { get; set; }
    public required string Action { get; set; }
    public Guid? TenantId { get; set; }
    public string? Detail { get; set; }
}

/// <summary>Data Protection keys shared by every tenant in MultiTenant mode (each tenant gets its own purpose).</summary>
public sealed class CatalogDataProtectionKey
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public string? FriendlyName { get; set; }
    public required string Xml { get; set; }
}

/// <summary>
/// The catalog database (MultiTenant mode only): which tenants exist, where their databases are, the shared Data
/// Protection key ring and the host audit log. Small and separate from <see cref="WorkbenchDbContext"/>; its
/// mappings are inline so the tenant model's configuration discovery never picks them up.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<HostAuditEvent> HostAuditEvents => Set<HostAuditEvent>();
    public DbSet<CatalogDataProtectionKey> DataProtectionKeys => Set<CatalogDataProtectionKey>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ProviderConventions.Apply(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.ToTable("Tenants");
            b.Property(t => t.Slug).HasMaxLength(63);
            b.HasIndex(t => t.Slug).IsUnique();
            b.Property(t => t.DisplayName).HasMaxLength(200);
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(16);
            b.Property(t => t.ProtectedConnectionString).HasMaxLength(4000);
            b.Property(t => t.Plan).HasMaxLength(64);
            b.Property(t => t.Notes).HasMaxLength(2000);
            b.Property(t => t.StatusChangedBy).HasMaxLength(320);
        });
        modelBuilder.Entity<HostAuditEvent>(b =>
        {
            b.ToTable("HostAuditEvents");
            b.Property(e => e.Actor).HasMaxLength(320);
            b.Property(e => e.Action).HasMaxLength(64);
            b.Property(e => e.Detail).HasMaxLength(1000);
            b.HasIndex(e => e.AtUtc);
        });
        modelBuilder.Entity<CatalogDataProtectionKey>(b =>
        {
            b.ToTable("DataProtectionKeys");
            b.Property(k => k.FriendlyName).HasMaxLength(200);
        });
        ProviderConventions.Apply(modelBuilder, Database);
    }
}

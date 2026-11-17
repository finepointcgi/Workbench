using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Workbench.LicenseServer.Data;

/// <summary>
/// The license server's own database. One model, two provider subclasses (<see cref="SqliteLicenseDb"/>,
/// <see cref="PostgresLicenseDb"/>) so each provider keeps its own migrations in this one assembly.
/// Keep queries provider-neutral: string comparison is ordinal and case-sensitive on both, so emails are stored lower-case.
/// </summary>
public abstract class LicenseDb(DbContextOptions options) : DbContext(options)
{
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Subscription> Subscriptions => Set<Subscription>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<IssuedKey> IssuedKeys => Set<IssuedKey>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<StripeEvent> StripeEvents => Set<StripeEvent>();
    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();

    protected override void ConfigureConventions(ModelConfigurationBuilder b)
    {
        // Every DateTime is UTC (Npgsql refuses other kinds for timestamptz; SQLite hands back Unspecified).
        b.Properties<DateTime>().HaveConversion<UtcConverter>();
        b.Properties<DateTime?>().HaveConversion<NullableUtcConverter>();
    }

    protected override void OnModelCreating(ModelBuilder m)
    {
        m.Entity<Customer>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(200);
            e.Property(x => x.Email).HasMaxLength(320);
            e.Property(x => x.StripeCustomerId).HasMaxLength(100);
            e.Property(x => x.Notes).HasMaxLength(2000);
            e.HasIndex(x => x.Email);
            e.HasIndex(x => x.StripeCustomerId).IsUnique();
        });
        m.Entity<Subscription>(e =>
        {
            e.Property(x => x.Modules).HasMaxLength(200);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(20);
            e.Property(x => x.StripeSubscriptionId).HasMaxLength(100);
            e.Property(x => x.StripeCustomerId).HasMaxLength(100);
            e.HasIndex(x => x.StripeSubscriptionId).IsUnique();
            e.HasIndex(x => x.CustomerId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<License>(e =>
        {
            e.Property(x => x.Licensee).HasMaxLength(200);
            e.Property(x => x.Modules).HasMaxLength(200);
            e.Property(x => x.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.RevokedReason).HasMaxLength(500);
            e.HasIndex(x => x.CustomerId);
            e.HasIndex(x => x.SubscriptionId);
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<IssuedKey>(e =>
        {
            e.Property(x => x.Reason).HasMaxLength(200);
            e.Property(x => x.IssuedBy).HasMaxLength(20);
            e.Property(x => x.Modules).HasMaxLength(200);
            e.Property(x => x.Key).HasMaxLength(4000);
            e.HasIndex(x => new { x.LicenseId, x.IssuedUtc });
            e.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<Activation>(e =>
        {
            e.Property(x => x.InstallId).HasMaxLength(100);
            e.Property(x => x.Version).HasMaxLength(50);
            e.HasIndex(x => new { x.LicenseId, x.InstallId }).IsUnique();
            e.HasOne<License>().WithMany().HasForeignKey(x => x.LicenseId).OnDelete(DeleteBehavior.Restrict);
        });
        m.Entity<StripeEvent>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(100);
            e.Property(x => x.Outcome).HasMaxLength(500);
        });
        m.Entity<LoginToken>(e =>
        {
            e.Property(x => x.TokenHash).HasMaxLength(64);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasOne<Customer>().WithMany().HasForeignKey(x => x.CustomerId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}

public sealed class SqliteLicenseDb(DbContextOptions<SqliteLicenseDb> options) : LicenseDb(options);

public sealed class PostgresLicenseDb(DbContextOptions<PostgresLicenseDb> options) : LicenseDb(options);

internal sealed class UtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : (v.Kind == DateTimeKind.Local ? v.ToUniversalTime() : DateTime.SpecifyKind(v, DateTimeKind.Utc)),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<DateTime?, DateTime?>(
    v => v == null ? null : (v.Value.Kind == DateTimeKind.Utc ? v : (v.Value.Kind == DateTimeKind.Local ? v.Value.ToUniversalTime() : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc))),
    v => v == null ? null : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));

/// <summary>Only for <c>dotnet ef migrations add</c>; the connection string is never used to connect.</summary>
public sealed class SqliteDesignFactory : IDesignTimeDbContextFactory<SqliteLicenseDb>
{
    public SqliteLicenseDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteLicenseDb>().UseSqlite("Data Source=design.db").Options);
}

public sealed class PostgresDesignFactory : IDesignTimeDbContextFactory<PostgresLicenseDb>
{
    public PostgresLicenseDb CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresLicenseDb>().UseNpgsql("Host=localhost;Database=design").Options);
}

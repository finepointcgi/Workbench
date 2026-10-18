using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Workbench.Application;
using Workbench.Domain;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Entity mappings live in <c>Configurations/</c>, one <see cref="IEntityTypeConfiguration{TEntity}"/>
/// per entity, and are picked up automatically. New entities need a configuration class, not edits here.
/// </summary>
public sealed class WorkbenchDbContext(DbContextOptions<WorkbenchDbContext> options)
    : DbContext(options), IWorkbenchDbContext
{
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<Sheet> Sheets => Set<Sheet>();
    public DbSet<Column> Columns => Set<Column>();
    public DbSet<Item> Items => Set<Item>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        ProviderConventions.Apply(configurationBuilder);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(WorkbenchDbContext).Assembly);
        ProviderConventions.Apply(modelBuilder, Database);
    }
}

/// <summary>
/// Makes PostgreSQL and SQLite behave alike for the things the code relies on:
/// <list type="bullet">
/// <item>Every <see cref="DateTime"/> is UTC. Npgsql refuses to write a non-UTC value to <c>timestamptz</c>, and SQLite
/// returns <c>Unspecified</c>; both now read back as <c>Utc</c>, and unspecified values are taken to be UTC.</item>
/// <item>Text compares and sorts by code point (collation <c>C</c> on PostgreSQL), like SQLite's default <c>BINARY</c>.
/// Without it a PostgreSQL database created with a locale (e.g. <c>en_US.UTF-8</c>) orders "B" before "a" differently
/// and ignores punctuation when sorting codes like <c>1001.01.02</c>. Equality and unique indexes are case-sensitive on
/// both providers, which is why emails are always stored lowercased.</item>
/// <item>Decimals are rounded to their column's scale (half away from zero) when saved. PostgreSQL's <c>numeric(p,s)</c>
/// does that itself; SQLite stores decimals as text at full precision, so it gets the same rounding as a converter.</item>
/// </list>
/// </summary>
internal static class ProviderConventions
{
    public static void Apply(ModelConfigurationBuilder configurationBuilder)
    {
        // Also applies to DateTime? properties.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
    }

    public static void Apply(ModelBuilder modelBuilder, Microsoft.EntityFrameworkCore.Infrastructure.DatabaseFacade database)
    {
        var npgsql = database.IsNpgsql();
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(t => t.GetProperties()))
        {
            if (npgsql && property.ClrType == typeof(string) && property.GetCollation() is null)
            {
                property.SetCollation("C");
            }

            if (!npgsql && property.GetScale() is { } scale && property.GetValueConverter() is null)
            {
                if (property.ClrType == typeof(decimal))
                {
                    property.SetValueConverter(new ValueConverter<decimal, decimal>(v => Math.Round(v, scale, MidpointRounding.AwayFromZero), v => v));
                }
                else if (property.ClrType == typeof(decimal?))
                {
                    property.SetValueConverter(new ValueConverter<decimal?, decimal?>(v => v.HasValue ? Math.Round(v.Value, scale, MidpointRounding.AwayFromZero) : v, v => v));
                }
            }
        }
    }

    private static DateTime ToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(v => ToUtc(v), v => DateTime.SpecifyKind(v, DateTimeKind.Utc));
}

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Workbench.Domain;
using Workbench.Domain.Identity;
using Workbench.Domain.ProjectAccounting;
using Workbench.Infrastructure.Persistence;
using Workbench.Infrastructure.Tenancy;

namespace Workbench.Tests.Platform;

/// <summary>
/// Migrations apply cleanly and match the model on both providers, and the provider differences the code relies on
/// behave the same: UTC DateTimes, ordinal text, case-sensitive unique emails, decimals rounded to their scale.
/// The PostgreSQL half needs a server (docker compose up -d) and is skipped without one.
/// </summary>
public sealed class PersistenceProviderTests
{
    [Fact]
    public Task Sqlite_migrations_apply_cleanly_and_match_the_model() => MigrationsAsync(DatabaseProvider.Sqlite);

    [PostgresFact]
    [Trait("Category", "PostgreSql")]
    public Task PostgreSql_migrations_apply_cleanly_and_match_the_model() => MigrationsAsync(DatabaseProvider.PostgreSql);

    [Fact]
    public Task Sqlite_behaves_like_postgresql_for_dates_text_and_decimals() => BehaviourAsync(DatabaseProvider.Sqlite);

    [PostgresFact]
    [Trait("Category", "PostgreSql")]
    public Task PostgreSql_behaves_like_sqlite_for_dates_text_and_decimals() => BehaviourAsync(DatabaseProvider.PostgreSql);

    [Fact]
    public void Every_decimal_has_a_precision_and_every_entity_a_table_in_the_baseline()
    {
        using var db = new WorkbenchDbContext(TestDb.Options(DatabaseProvider.Sqlite, "Data Source=:memory:"));
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        var unscaled = model.GetEntityTypes().SelectMany(t => t.GetProperties())
            .Where(p => p.ClrType == typeof(decimal) || p.ClrType == typeof(decimal?))
            .Where(p => p.GetPrecision() is null || p.GetScale() is null)
            .Select(p => $"{p.DeclaringType.ShortName()}.{p.Name}").ToList();
        Assert.Empty(unscaled);
    }

    private static async Task MigrationsAsync(DatabaseProvider provider)
    {
        // A brand-new database: every migration applies, and nothing in the model is missing from them.
        var connection = provider == DatabaseProvider.Sqlite
            ? $"Data Source={Path.Combine(Path.GetTempPath(), $"workbench-migrate-{Guid.NewGuid():N}.db")};Pooling=False"
            : PostgresTemplates.ConnectionString($"wbtest_migrate_{Guid.NewGuid():N}");
        await using (var db = new WorkbenchDbContext(TestDb.Options(provider, connection)))
        {
            try
            {
                Assert.NotEmpty(await db.Database.GetPendingMigrationsAsync());
                await db.Database.MigrateAsync();
                Assert.Empty(await db.Database.GetPendingMigrationsAsync());
                Assert.False(db.Database.HasPendingModelChanges(), "The model has changes no migration covers: run scripts/new-migration.sh.");
                Assert.Equal(0, await db.Set<Workspace>().CountAsync());

                // Migrating again is a no-op.
                await db.Database.MigrateAsync();
            }
            finally
            {
                await db.Database.EnsureDeletedAsync();
            }
        }

        var catalogOptions = new DbContextOptionsBuilder<CatalogDbContext>();
        catalogOptions.Use(provider, provider == DatabaseProvider.Sqlite
            ? $"Data Source={Path.Combine(Path.GetTempPath(), $"workbench-catalog-{Guid.NewGuid():N}.db")};Pooling=False"
            : PostgresTemplates.ConnectionString($"wbtest_catalog_{Guid.NewGuid():N}"));
        await using var catalog = new CatalogDbContext(catalogOptions.Options);
        try
        {
            await catalog.Database.MigrateAsync();
            Assert.False(catalog.Database.HasPendingModelChanges(), "The catalog model has changes no migration covers: run scripts/new-migration.sh NAME --catalog.");
            Assert.Equal(0, await catalog.Tenants.CountAsync());
        }
        finally
        {
            await catalog.Database.EnsureDeletedAsync();
        }
    }

    private static async Task BehaviourAsync(DatabaseProvider provider)
    {
        var options = TestDb.Options(provider, TestDb.NewConnectionString(provider));
        var unspecified = new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Unspecified);
        Guid employeeId;
        await using (var db = new WorkbenchDbContext(options))
        {
            // Npgsql refuses non-UTC values for timestamptz; the convention treats unspecified as UTC on both.
            db.Set<AppUser>().AddRange(
                new AppUser { Email = "b@example.com", DisplayName = "B", CreatedAtUtc = unspecified, LastSeenAtUtc = unspecified },
                new AppUser { Email = "a@example.com", DisplayName = "a", CreatedAtUtc = DateTime.UtcNow },
                new AppUser { Email = "c@example.com", DisplayName = "1001.01.02", CreatedAtUtc = DateTime.UtcNow },
                new AppUser { Email = "d@example.com", DisplayName = "1001.01", CreatedAtUtc = DateTime.UtcNow },
                new AppUser { Email = "e@example.com", DisplayName = "1001.0101", CreatedAtUtc = DateTime.UtcNow });
            var employee = new Employee { EmployeeNumber = "E1", Email = "b@example.com", DisplayName = "B", HireDate = new DateOnly(2028, 2, 29) };
            employeeId = employee.Id;
            db.Set<Employee>().Add(employee);
            db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = employee.Id, EffectiveFrom = new DateOnly(2026, 1, 1), HourlyCost = 87.123456m });
            await db.SaveChangesAsync();
        }

        await using (var db = new WorkbenchDbContext(options))
        {
            var b = await db.Set<AppUser>().SingleAsync(u => u.Email == "b@example.com");
            Assert.Equal(DateTimeKind.Utc, b.CreatedAtUtc.Kind);
            Assert.Equal(DateTimeKind.Utc, b.LastSeenAtUtc!.Value.Kind);
            Assert.Equal(new DateTime(2026, 10, 18, 9, 30, 0, DateTimeKind.Utc), b.CreatedAtUtc);
            Assert.Equal(1, await db.Set<AppUser>().CountAsync(u => u.CreatedAtUtc == unspecified));

            // Ordinal ordering: upper case before lower case, and punctuation counts.
            Assert.Equal(["1001.01", "1001.01.02", "1001.0101", "B", "a"], await db.Set<AppUser>().OrderBy(u => u.DisplayName).Select(u => u.DisplayName).ToListAsync());

            // Equality is case-sensitive on both, which is why emails are stored lowercased.
            Assert.Equal(0, await db.Set<AppUser>().CountAsync(u => u.Email == "B@EXAMPLE.COM"));

            // Decimals are rounded to their scale (rates are 18,4) on both providers.
            Assert.Equal(87.1235m, await db.Set<EmployeeCostRate>().Where(r => r.EmployeeId == employeeId).Select(r => r.HourlyCost).SingleAsync());
            Assert.Equal(new DateOnly(2028, 2, 29), (await db.Set<Employee>().SingleAsync()).HireDate);
        }

        // The unique email index rejects an exact duplicate; a differently cased email would be a different row, so
        // the application lowercases before saving.
        await using (var db = new WorkbenchDbContext(options))
        {
            db.Set<AppUser>().Add(new AppUser { Email = "a@example.com", DisplayName = "dup", CreatedAtUtc = DateTime.UtcNow });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }
    }
}

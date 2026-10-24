using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Workbench.Domain.Payroll;
using Workbench.Domain.ProjectAccounting;
using Workbench.Infrastructure.Persistence;

namespace Workbench.Tests.Payroll;

/// <summary>
/// The PayRateHistoryLaborAdjustments migration moves existing pay rates into the history: one row per profile from the
/// hire date, or the first day with time when that's earlier, and a matching labor cost rate where an employee had none.
/// </summary>
public sealed class PayRateMigrationTests
{
    private const string Migration = "PayRateHistoryLaborAdjustments";

    [Fact]
    public Task Sqlite_existing_rates_become_one_history_row() => MigratesAsync(DatabaseProvider.Sqlite);

    [PostgresFact]
    [Trait("Category", "PostgreSql")]
    public Task PostgreSql_existing_rates_become_one_history_row() => MigratesAsync(DatabaseProvider.PostgreSql);

    private static async Task MigratesAsync(DatabaseProvider provider)
    {
        var connection = provider == DatabaseProvider.Sqlite
            ? $"Data Source={Path.Combine(Path.GetTempPath(), $"workbench-payrate-{Guid.NewGuid():N}.db")};Pooling=False"
            : PostgresTemplates.ConnectionString($"wbtest_payrate_{Guid.NewGuid():N}") + ";Command Timeout=300";
        await using var db = new WorkbenchDbContext(TestDb.Options(provider, connection));
        try
        {
            var migrations = db.Database.GetMigrations().ToList();
            var mine = migrations.FindIndex(m => m.EndsWith("_" + Migration, StringComparison.Ordinal));
            Assert.True(mine > 0, "The migration is missing.");
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync(migrations[mine - 1]);

            // Before: a salaried profile with time logged before its recorded hire date, an hourly one with a cost rate
            // already on file, one with no rate, and one that already has history.
            var hire = new DateOnly(2025, 3, 1);
            Employee Person(string n) => new() { EmployeeNumber = n, Email = $"{n}@example.com", DisplayName = n, HireDate = hire };
            var (salaried, hourly, unrated, existing) = (Person("S1"), Person("H1"), Person("U1"), Person("X1"));
            db.Set<Employee>().AddRange(salaried, hourly, unrated, existing);
            var updated = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc);
            db.Set<EmployeePayProfile>().AddRange(
                new EmployeePayProfile { EmployeeId = salaried.Id, PayType = PayType.Salaried, AnnualSalary = 104_000m, EffectiveFrom = hire, UpdatedAtUtc = updated, UpdatedBy = "finance@example.com" },
                new EmployeePayProfile { EmployeeId = hourly.Id, PayType = PayType.Hourly, HourlyRate = 42.5m, EffectiveFrom = hire, UpdatedAtUtc = updated },
                new EmployeePayProfile { EmployeeId = unrated.Id, PayType = PayType.Hourly, EffectiveFrom = hire, UpdatedAtUtc = updated },
                new EmployeePayProfile { EmployeeId = existing.Id, PayType = PayType.Hourly, HourlyRate = 20m, EffectiveFrom = hire, UpdatedAtUtc = updated });
            db.Set<EmployeePayRate>().Add(new EmployeePayRate { EmployeeId = existing.Id, EffectiveFrom = hire, PayType = PayType.Hourly, HourlyRate = 20m, CreatedBy = "x", CreatedAtUtc = updated });
            db.Set<EmployeeCostRate>().Add(new EmployeeCostRate { EmployeeId = hourly.Id, EffectiveFrom = hire, HourlyCost = 55m });

            var category = new LaborCategory { Code = "C", Name = "Cat" };
            var project = new Project { Code = "P", Name = "P" };
            var wbs = new WbsElement { ProjectId = project.Id, Code = "P.1", Name = "Work" };
            var sheet = new Timesheet { EmployeeId = salaried.Id, PeriodStart = new DateOnly(2025, 2, 24), PeriodEnd = new DateOnly(2025, 3, 2), Status = TimesheetStatus.Approved };
            db.AddRange(category, project, wbs, sheet);
            db.Set<TimeEntry>().AddRange(
                new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = new DateOnly(2025, 2, 26), Hours = 8m },
                new TimeEntry { TimesheetId = sheet.Id, WbsElementId = wbs.Id, LaborCategoryId = category.Id, WorkDate = new DateOnly(2025, 2, 24), Hours = 0m });
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            await migrator.MigrateAsync();

            var rates = await db.Set<EmployeePayRate>().AsNoTracking().ToListAsync();
            var s = Assert.Single(rates, r => r.EmployeeId == salaried.Id);
            Assert.Equal(new DateOnly(2025, 2, 26), s.EffectiveFrom); // the first day with time, before the hire date
            Assert.Equal((PayType.Salaried, 104_000m, (decimal?)null), (s.PayType, s.AnnualSalary, s.HourlyRate));
            Assert.Equal("Rate on file before pay rate history", s.Reason);
            Assert.Equal("finance@example.com", s.CreatedBy);
            var h = Assert.Single(rates, r => r.EmployeeId == hourly.Id);
            Assert.Equal((hire, 42.5m, "system"), (h.EffectiveFrom, h.HourlyRate!.Value, h.CreatedBy));
            Assert.DoesNotContain(rates, r => r.EmployeeId == unrated.Id);
            Assert.Single(rates, r => r.EmployeeId == existing.Id);
            Assert.NotEqual(Guid.Empty, s.Id);

            // Cost rates: the salaried one gets 104,000 / 2,080 from the same date; one already on file is kept.
            var costs = await db.Set<EmployeeCostRate>().AsNoTracking().ToListAsync();
            var sc = Assert.Single(costs, c => c.EmployeeId == salaried.Id);
            Assert.Equal((new DateOnly(2025, 2, 26), 50m), (sc.EffectiveFrom, sc.HourlyCost));
            Assert.Equal(55m, Assert.Single(costs, c => c.EmployeeId == hourly.Id).HourlyCost);
            Assert.Single(costs, c => c.EmployeeId == existing.Id); // had history, no cost rate: gets its earliest rate
            Assert.DoesNotContain(costs, c => c.EmployeeId == unrated.Id);

            // The data step is idempotent with the code backfill: nothing left to add.
            Assert.Equal(0, await Application.Payroll.PayRates.BackfillAsync(db, "system", DateTime.UtcNow, CancellationToken.None));
        }
        finally
        {
            await db.Database.EnsureDeletedAsync();
        }
    }
}

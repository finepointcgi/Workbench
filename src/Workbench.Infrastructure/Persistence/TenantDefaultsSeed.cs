using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Workbench.Application.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// What every new tenant starts with (production, not sample data): the default GovCon chart of accounts with its
/// posting rules, and the current and next fiscal years with monthly periods. Idempotent.
/// </summary>
public static class TenantDefaultsSeed
{
    public static async Task SeedAsync(IServiceProvider services, int fiscalYearStartMonth, TimeProvider clock, CancellationToken cancellationToken)
    {
        await using var db = await services.GetRequiredService<IDbContextFactory<WorkbenchDbContext>>().CreateDbContextAsync(cancellationToken);
        var now = clock.GetUtcNow().UtcDateTime;
        if (!await db.Set<Account>().AnyAsync(cancellationToken))
        {
            var (accounts, rules) = DefaultChartOfAccounts.Build(now, "system");
            db.Set<Account>().AddRange(accounts);
            db.Set<PostingRule>().AddRange(rules);
        }

        if (!await db.Set<FiscalYear>().AnyAsync(cancellationToken))
        {
            var month = Math.Clamp(fiscalYearStartMonth, 1, 12);
            var today = DateOnly.FromDateTime(now);
            var start = new DateOnly(today.Month >= month ? today.Year : today.Year - 1, month, 1);
            foreach (var yearStart in new[] { start, start.AddYears(1) })
            {
                // Named for the calendar year it ends in, like the federal fiscal year (Oct 2026 – Sep 2027 is FY2027).
                var end = yearStart.AddYears(1).AddDays(-1);
                var year = new FiscalYear { Year = end.Year, Start = yearStart, End = end };
                db.Add(year);
                db.AddRange(Enumerable.Range(0, 12).Select(i => new FiscalPeriod
                {
                    FiscalYearId = year.Id,
                    Number = i + 1,
                    Name = $"{yearStart.AddMonths(i):yyyy-MM}",
                    Start = yearStart.AddMonths(i),
                    End = yearStart.AddMonths(i + 1).AddDays(-1),
                }));
            }
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development seed for the general ledger: the default GovCon chart of accounts with its posting rules, and the
/// G&amp;A pool for the seeded G&amp;A and B&amp;P charge codes. Nothing is posted: "Post backlog" on the Period close
/// page posts the seeded approved timesheets and ODCs. Runs once, after the financials seed.
/// </summary>
internal static class AccountingSeed
{
    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Account>().AnyAsync(cancellationToken))
        {
            return;
        }

        var (accounts, rules) = DefaultChartOfAccounts.Build(DateTime.UtcNow, "seed@workbench.local");
        db.Set<Account>().AddRange(accounts);
        db.Set<PostingRule>().AddRange(rules);
        var codes = DefaultChartOfAccounts.SeedPools.Keys.ToList();
        foreach (var wbs in await db.Set<WbsElement>().Where(w => codes.Contains(w.Code)).ToListAsync(cancellationToken))
        {
            db.Set<WbsCostPool>().Add(new WbsCostPool { WbsElementId = wbs.Id, Pool = DefaultChartOfAccounts.SeedPools[wbs.Code], UpdatedBy = "seed@workbench.local", UpdatedAtUtc = DateTime.UtcNow });
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}

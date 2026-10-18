using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Application.IndirectRates;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development seed for indirect rates: the fringe → overhead → G&amp;A pools, provisional rates for every seeded fiscal
/// year (fringe 32%, overhead 18%, G&amp;A 11%) and the allocation for each open period that has approved direct cost
/// (closed periods can't take postings). Runs once, after the accounting seed.
/// </summary>
internal static class IndirectRatesSeed
{
    private const string Actor = "seed@workbench.local";

    private static readonly (IndirectPool Pool, decimal Rate)[] Provisional =
    [
        (IndirectPool.Fringe, 0.32m),
        (IndirectPool.Overhead, 0.18m),
        (IndirectPool.GeneralAndAdministrative, 0.11m),
    ];

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (!await db.Set<Account>().AnyAsync(cancellationToken) || await db.Set<IndirectPoolSetup>().AnyAsync(cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        await DefaultPools.EnsureAsync(db, Actor, now, cancellationToken);
        foreach (var year in await db.Set<FiscalYear>().ToListAsync(cancellationToken))
        {
            db.Set<IndirectRate>().AddRange(Provisional.Select(p => new IndirectRate
            {
                Pool = p.Pool,
                FiscalYear = year.Year,
                Basis = IndirectRateBasis.Provisional,
                Rate = p.Rate,
                EffectiveFrom = year.Start,
                Note = "Provisional billing rates (from the last DCAA-approved rates)",
                ApprovedBy = Actor,
                ApprovedAtUtc = now,
                CreatedBy = Actor,
                CreatedAtUtc = now,
            }));
        }

        await db.SaveChangesAsync(cancellationToken);

        var audit = new AuditRecorder(new SeedUser(), TimeProvider.System);
        var engine = new AllocationEngine(new GeneralLedger(audit, TimeProvider.System), audit);
        var today = DateOnly.FromDateTime(now);
        var periods = await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.Status == PeriodStatus.Open && p.Start <= today).OrderBy(p => p.Start).ToListAsync(cancellationToken);
        foreach (var period in periods)
        {
            if ((await engine.PreviewAsync(db, period.Id, cancellationToken)).Total != 0)
            {
                await engine.PostAsync(db, period.Id, cancellationToken);
            }
        }
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}

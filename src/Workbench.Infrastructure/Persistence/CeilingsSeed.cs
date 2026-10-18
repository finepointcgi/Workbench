using Microsoft.EntityFrameworkCore;
using Workbench.Domain.Ceilings;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development sample data for ceilings on the Army contract W56KGU-26-C-0001: a warning cost and blocking billing ceiling
/// on the contract, a blocking hours ceiling on WBS 1001.01.02 set 16 h above the hours already on record (so two more
/// days can be charged), and an overhead rate cap of 15% (below the 18% provisional rate) that billing applies.
/// Runs once, after the other seeds.
/// </summary>
internal static class CeilingsSeed
{
    private const string Actor = "seed@workbench.local";

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<Ceiling>().AnyAsync(cancellationToken))
        {
            return;
        }

        var contract = await db.Set<Contract>().FirstOrDefaultAsync(c => c.ContractNumber == "W56KGU-26-C-0001", cancellationToken);
        var wbs = await db.Set<WbsElement>().FirstOrDefaultAsync(w => w.Code == "1001.01.02", cancellationToken);
        if (contract is null || wbs is null)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var hours = (await db.Set<TimeEntry>().Where(e => e.WbsElementId == wbs.Id).Select(e => e.Hours).ToListAsync(cancellationToken)).Sum();
        db.Set<Ceiling>().AddRange(
            new Ceiling
            {
                Level = CeilingLevel.Contract,
                ContractId = contract.Id,
                CostCeiling = 2_000_000m,
                BillingCeiling = 2_400_000m,
                Enforcement = CeilingEnforcement.Block,
                Notes = "Total estimated cost and contract value",
                CreatedBy = Actor,
                CreatedAtUtc = now,
            },
            new Ceiling
            {
                Level = CeilingLevel.Wbs,
                ContractId = contract.Id,
                ProjectId = wbs.ProjectId,
                WbsElementId = wbs.Id,
                HoursCeiling = hours + 16m,
                Enforcement = CeilingEnforcement.Block,
                ProjectManagerMayOverride = true,
                Notes = "Analysis task hours approved by the COR",
                CreatedBy = Actor,
                CreatedAtUtc = now,
            });
        db.Set<RateCeiling>().Add(new RateCeiling
        {
            ContractId = contract.Id,
            Kind = RateCeilingKind.Overhead,
            MaxRate = 0.15m,
            Notes = "Overhead ceiling in section H of the contract",
            CreatedBy = Actor,
            CreatedAtUtc = now,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}

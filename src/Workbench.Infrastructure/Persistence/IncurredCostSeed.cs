using Microsoft.EntityFrameworkCore;
using Workbench.Application.IncurredCost;
using Workbench.Application.ProjectAccounting;
using Workbench.Contracts.Collaboration;
using Workbench.Domain.IncurredCost;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Infrastructure.Persistence;

/// <summary>
/// Development seed for the incurred cost submission: a FY2026 draft generated from the seeded books, with the contractor's
/// name typed in. Runs once, after every seed that posts to the ledger.
/// </summary>
internal static class IncurredCostSeed
{
    private const string Actor = "seed@workbench.local";
    private const int FiscalYear = 2026;

    public static async Task SeedAsync(WorkbenchDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Set<IncurredCostSubmission>().AnyAsync(cancellationToken)
            || !await db.Set<FiscalYear>().AnyAsync(y => y.Year == FiscalYear, cancellationToken))
        {
            return;
        }

        var audit = new AuditRecorder(new SeedUser(), TimeProvider.System);
        var submission = await IncurredCostGenerator.CreateAsync(db, FiscalYear, audit, TimeProvider.System, cancellationToken);
        db.Set<IncurredCostInput>().Add(new IncurredCostInput
        {
            SubmissionId = submission.Id,
            Schedule = "Cover",
            RowKey = "contractor",
            Column = "value",
            Value = "Workbench Federal Services, LLC",
            UpdatedBy = Actor,
            UpdatedAtUtc = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    private sealed class SeedUser : ICurrentUser
    {
        public string Email => Actor;
    }
}

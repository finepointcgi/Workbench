using Microsoft.EntityFrameworkCore;
using Workbench.Application.Accounting;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Cash;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Cash;

/// <summary>
/// Period close: every active bank account has a completed reconciliation through the period end. The close-check model
/// is pass/fail (a failing check blocks the close), so this is a blocking check that Finance can waive for the period with
/// a reason (Cash › Reconciliation history, or <c>IBankReconciliationService.WaiveCloseCheckAsync</c>); the waiver is
/// audited and the check then passes, saying who waived it and why.
/// </summary>
public sealed class BankReconciliationCloseCheck : IPeriodCloseCheck
{
    public const string Title = "Bank accounts reconciled through period end";

    public async Task<CloseCheckDto> CheckAsync(IWorkbenchDbContext db, FiscalPeriod period, CancellationToken ct)
    {
        var accounts = await db.Set<BankAccount>().AsNoTracking().Where(a => a.IsActive).OrderBy(a => a.Name).ToListAsync(ct);
        if (accounts.Count == 0)
        {
            return new CloseCheckDto(CloseCheckKind.BankReconciled, Title, true, 0, "No bank accounts are set up", []);
        }

        var through = (await db.Set<BankReconciliation>().AsNoTracking().Where(r => r.Status == BankReconciliationStatus.Completed)
                .Select(r => new { r.BankAccountId, r.StatementEndDate }).ToListAsync(ct))
            .GroupBy(r => r.BankAccountId).ToDictionary(g => g.Key, g => g.Max(r => r.StatementEndDate));
        var behind = accounts.Where(a => !through.TryGetValue(a.Id, out var d) || d < period.End).ToList();
        var details = behind.Select(a => through.TryGetValue(a.Id, out var d)
            ? $"{a.Name} (••••{a.AccountNumberLast4}): reconciled through {d:yyyy-MM-dd}"
            : $"{a.Name} (••••{a.AccountNumberLast4}): never reconciled").ToList();
        if (behind.Count == 0)
        {
            return new CloseCheckDto(CloseCheckKind.BankReconciled, Title, true, 0, $"All {accounts.Count} bank account(s) reconciled through {period.End:yyyy-MM-dd}", []);
        }

        var waiver = (await db.Set<BankCloseWaiver>().AsNoTracking().Where(w => w.FiscalPeriodId == period.Id).ToListAsync(ct)).MaxBy(w => w.WaivedAtUtc);
        if (waiver is not null)
        {
            return new CloseCheckDto(CloseCheckKind.BankReconciled, Title, true, behind.Count,
                $"Waived by {waiver.WaivedBy}: {waiver.Reason}", details);
        }

        return new CloseCheckDto(CloseCheckKind.BankReconciled, Title, false, behind.Count,
            $"{behind.Count} bank account(s) not reconciled through {period.End:yyyy-MM-dd}; reconcile them under Cash, or Finance can waive this with a reason", details);
    }
}

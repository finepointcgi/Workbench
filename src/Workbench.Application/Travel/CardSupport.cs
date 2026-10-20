using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Travel;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;
using Workbench.Domain.Travel;

namespace Workbench.Application.Travel;

/// <summary>Card charge states, ages and DTOs, shared by the Finance and cardholder services, the to-dos and posting.</summary>
public static class CardSupport
{
    public const string LiabilityAccountNumber = "2030";
    public const string LiabilityAccountName = "Corporate card payable";

    public static CardChargeState State(CardTransaction t, OdcStatus? lineStatus) =>
        t.IsExcluded ? CardChargeState.Excluded
        : lineStatus switch
        {
            null => CardChargeState.Unassigned,
            OdcStatus.Submitted => CardChargeState.Submitted,
            OdcStatus.Approved => CardChargeState.Approved,
            _ => CardChargeState.OnDraftReport,
        };

    /// <summary>Unassigned and draft-report charges are "unsubmitted"; credits aren't chased.</summary>
    public static bool IsUnsubmitted(CardChargeState state) => state is CardChargeState.Unassigned or CardChargeState.OnDraftReport;

    public static async Task<IReadOnlyList<CardTransactionDto>> ToDtosAsync(IWorkbenchDbContext db, IReadOnlyList<CardTransaction> rows, DateOnly today, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var programIds = rows.Select(r => r.ProgramId).Distinct().ToList();
        var employeeIds = rows.Where(r => r.EmployeeId != null).Select(r => r.EmployeeId!.Value).Distinct().ToList();
        var odcIds = rows.Where(r => r.OdcEntryId != null).Select(r => r.OdcEntryId!.Value).ToList();
        var programs = await db.Set<CardProgram>().AsNoTracking().Where(p => programIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);
        var names = await db.Set<Employee>().AsNoTracking().Where(e => employeeIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, e => e.DisplayName, ct);
        var lines = await db.Set<OdcEntry>().AsNoTracking().Where(o => odcIds.Contains(o.Id))
            .Select(o => new { o.Id, o.Status, o.ExpenseReportId }).ToDictionaryAsync(o => o.Id, ct);
        var reportIds = lines.Values.Where(l => l.ExpenseReportId != null).Select(l => l.ExpenseReportId!.Value).Distinct().ToList();
        var reportTitles = await db.Set<ExpenseReport>().AsNoTracking().Where(r => reportIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, r => r.Title, ct);
        return rows.Select(t =>
        {
            var program = programs[t.ProgramId];
            var line = t.OdcEntryId is { } o ? lines.GetValueOrDefault(o) : null;
            var state = State(t, line?.Status);
            var age = Math.Max(0, today.DayNumber - t.TransactionDate.DayNumber);
            return new CardTransactionDto(t.Id, t.ProgramId, program.Name, (CardPaymentKind)(int)program.PaymentResponsibility, t.CardId, t.CardLast4,
                t.EmployeeId, t.EmployeeId is { } e ? names.GetValueOrDefault(e) : null, t.TransactionDate, t.PostedDate, t.Amount, t.Merchant, t.MerchantCategory,
                TravelCategories.Guess(t.Merchant, t.MerchantCategory), state, line?.Id, line?.ExpenseReportId,
                line?.ExpenseReportId is { } r ? reportTitles.GetValueOrDefault(r) : null, age,
                IsUnsubmitted(state) && t.Amount > 0 && age > program.UnsubmittedAlertDays, t.ExcludedReason);
        }).ToList();
    }

    /// <summary>The program's liability account, or 2030 Corporate card payable (created under 2000 when the chart lacks it).</summary>
    public static async Task<Account> EnsureLiabilityAccountAsync(IWorkbenchDbContext db, DateTime utcNow, CancellationToken ct)
    {
        var existing = await db.Set<Account>().FirstOrDefaultAsync(a => a.Number == LiabilityAccountNumber, ct);
        if (existing is not null)
        {
            return existing.IsActive && existing.Type == AccountType.Liability
                ? existing
                : throw new ArgumentException($"Account {existing.Number} {existing.Name} isn't an active liability account; choose the program's liability account.");
        }

        var parent = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Number == "2000" && a.Type == AccountType.Liability, ct);
        var account = new Account
        {
            Number = LiabilityAccountNumber,
            Name = LiabilityAccountName,
            Type = AccountType.Liability,
            NormalBalance = NormalBalance.Credit,
            ParentId = parent?.Id,
            Description = "Company-paid corporate card charges owed to the card issuer",
            CreatedAtUtc = utcNow,
        };
        db.Set<Account>().Add(account);
        return account;
    }

    /// <summary>
    /// For posting: the liability account each company-paid card charge's expense line credits (instead of accrued
    /// employee reimbursements), keyed by the line (<c>OdcEntry</c>) id.
    /// </summary>
    public static async Task<Dictionary<Guid, Guid>> CompanyPaidLiabilitiesAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var rows = await db.Set<CardTransaction>().AsNoTracking()
            .Where(t => t.OdcEntryId != null)
            .Join(db.Set<CardProgram>().Where(p => p.PaymentResponsibility == CardPaymentResponsibility.CompanyPaid && p.LiabilityAccountId != null),
                t => t.ProgramId, p => p.Id, (t, p) => new { OdcId = t.OdcEntryId!.Value, AccountId = p.LiabilityAccountId!.Value })
            .ToListAsync(ct);
        return rows.GroupBy(r => r.OdcId).ToDictionary(g => g.Key, g => g.First().AccountId);
    }
}

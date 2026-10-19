using Microsoft.EntityFrameworkCore;
using Workbench.Contracts.Planning;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

/// <summary>Spreading annual amounts over periods, and the shared lookups budgets and submissions use.</summary>
public static class BudgetMath
{
    /// <summary>
    /// Splits <paramref name="annual"/> over <paramref name="periods"/> periods in whole cents that add up exactly:
    /// evenly, by the prior year's pattern (<paramref name="priorYear"/>, one amount per period), or by a percentage
    /// profile (one percentage per period, adding up to 100).
    /// </summary>
    public static IReadOnlyList<decimal> Spread(decimal annual, int periods, SpreadMethod method, IReadOnlyList<decimal>? priorYear = null, IReadOnlyList<decimal>? profile = null)
    {
        PlanningMapping.RequireCents(annual, "Annual amount");
        if (periods <= 0)
        {
            throw new ArgumentException("The fiscal year has no periods.");
        }

        switch (method)
        {
            case SpreadMethod.Evenly:
                return Allocation.Split(annual, Enumerable.Repeat(1m, periods).ToList());

            case SpreadMethod.PriorYearPattern:
                var weights = Enumerable.Range(0, periods).Select(i => Math.Max(0m, priorYear is not null && i < priorYear.Count ? priorYear[i] : 0m)).ToList();
                if (weights.Sum() == 0)
                {
                    throw new ArgumentException("There are no prior-year actuals on this account and org unit to follow. Spread it evenly or by a profile instead.");
                }

                return Allocation.Split(annual, weights);

            case SpreadMethod.Profile:
                if (profile is null || profile.Count != periods)
                {
                    throw new ArgumentException($"Enter {periods} percentages, one per period.");
                }

                if (profile.Any(p => p < 0))
                {
                    throw new ArgumentException("Profile percentages can't be negative.");
                }

                var total = profile.Sum();
                if (Math.Abs(total - 100m) > 0.01m)
                {
                    throw new ArgumentException($"The profile adds up to {total:0.##}%; it must add up to 100%.");
                }

                return Allocation.Split(annual, profile);

            default:
                throw new ArgumentException("Choose how to spread the amount.");
        }
    }

    /// <summary>The fiscal year with that number, and its periods in order.</summary>
    public static async Task<(FiscalYear Year, List<FiscalPeriod> Periods)> YearAsync(IWorkbenchDbContext db, int fiscalYear, CancellationToken ct)
    {
        var year = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Year == fiscalYear, ct)
            ?? throw new ArgumentException($"FY{fiscalYear} isn't in the fiscal calendar. Add it in Setup › Fiscal calendar first.");
        return (year, await PeriodsAsync(db, year.Id, ct));
    }

    public static async Task<List<FiscalPeriod>> PeriodsAsync(IWorkbenchDbContext db, Guid fiscalYearId, CancellationToken ct) =>
        (await db.Set<FiscalPeriod>().AsNoTracking().Where(p => p.FiscalYearId == fiscalYearId).ToListAsync(ct)).OrderBy(p => p.Number).ToList();

    /// <summary>
    /// The prior fiscal year's posted actuals on <paramref name="accountId"/> (and <paramref name="orgUnitId"/> when given),
    /// in the account's natural sign, by period number (1-based index).
    /// </summary>
    public static async Task<IReadOnlyList<decimal>> PriorYearPatternAsync(IWorkbenchDbContext db, FiscalYear year, int periods, Guid accountId, Guid? orgUnitId, CancellationToken ct)
    {
        var result = new decimal[periods];
        var prior = await db.Set<FiscalYear>().AsNoTracking().FirstOrDefaultAsync(y => y.Year == year.Year - 1, ct);
        var account = await db.Set<Account>().AsNoTracking().FirstOrDefaultAsync(a => a.Id == accountId, ct);
        if (prior is null || account is null)
        {
            return result;
        }

        var numbers = (await PeriodsAsync(db, prior.Id, ct)).ToDictionary(p => p.Id, p => p.Number);
        var ids = numbers.Keys.ToList();
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.Status != JournalStatus.Draft && ids.Contains(e.FiscalPeriodId) && l.AccountId == accountId && (orgUnitId == null || l.OrgUnitId == orgUnitId)
                select new { e.FiscalPeriodId, l.Debit, l.Credit })
            .ToListAsync(ct);
        foreach (var l in lines)
        {
            var n = numbers[l.FiscalPeriodId];
            if (n >= 1 && n <= periods)
            {
                result[n - 1] += account.Natural(l.Debit - l.Credit);
            }
        }

        return result;
    }

    /// <summary>Accounts a budget may use: active income statement (revenue and expense) detail accounts, except applied-burden accounts.</summary>
    public static async Task<List<Account>> BudgetableAccountsAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var accounts = await db.Set<Account>().AsNoTracking().ToListAsync(ct);
        var parents = accounts.Where(a => a.ParentId != null).Select(a => a.ParentId!.Value).ToHashSet();
        return accounts.Where(a => a.IsActive && !parents.Contains(a.Id) && a.Type is AccountType.Revenue or AccountType.Expense && a.CostClass != CostClassification.AppliedBurden)
            .OrderBy(a => a.Number, StringComparer.Ordinal).ToList();
    }

    public static Account RequireBudgetable(IReadOnlyDictionary<Guid, Account> budgetable, Guid accountId, string where)
    {
        if (!budgetable.TryGetValue(accountId, out var account))
        {
            throw new ArgumentException($"{where}: budgets are for active revenue and expense detail accounts.");
        }

        return account;
    }
}

using Microsoft.EntityFrameworkCore;
using Workbench.Application.Reports;
using Workbench.Contracts.Reports;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

/// <summary>
/// Report writer source: corporate budget lines (account × org unit × period, every version) with the posted actual for
/// the same account, org unit and period and the variance. Finance and Admin only, like the budgets.
/// </summary>
public sealed class GlBudgetReportSource : ReportSource
{
    public override string Key => ReportSourceKeys.GlBudgets;
    public override string Name => "GL budget lines";
    public override string Description => "Corporate budget amounts by version, account, org unit and fiscal period, with the posted actual and the variance.";
    public override string? DateField => "period_start";

    public override IReadOnlyList<SourceField> Fields { get; } =
    [
        new("version", "Budget version", ReportColumnType.Text),
        new("version_kind", "Version kind", ReportColumnType.Text),
        new("version_status", "Version status", ReportColumnType.Text),
        new("fiscal_year", "Fiscal year", ReportColumnType.Number),
        new("period", "Fiscal period", ReportColumnType.Text),
        new("period_start", "Period start", ReportColumnType.Date),
        new("account", "Account", ReportColumnType.Text),
        new("account_name", "Account name", ReportColumnType.Text),
        new("account_type", "Account type", ReportColumnType.Text),
        new("org", "Org", ReportColumnType.Text),
        new("budget", "Budget", ReportColumnType.Money),
        new("actual", "Actual", ReportColumnType.Money, Description: "Posted GL on the same account, org unit and period, in the account's natural sign."),
        new("variance", "Variance (favourable +)", ReportColumnType.Money),
    ];

    public override string? Unavailable(ReportAccessContext access) =>
        access.IsFinance ? null : "Budgets are for Finance and administrators.";

    public override async Task<SourceRows> LoadAsync(IWorkbenchDbContext db, ReportAccessContext access, DateOnly? from, DateOnly? to, DateOnly today, CancellationToken ct)
    {
        var periods = await db.Set<FiscalPeriod>().AsNoTracking()
            .Where(p => (from == null || p.Start >= from) && (to == null || p.Start <= to)).ToDictionaryAsync(p => p.Id, ct);
        var ids = periods.Keys.ToList();
        var lines = await db.Set<GlBudgetLine>().AsNoTracking().Where(l => ids.Contains(l.FiscalPeriodId)).Take(RowLimit + 1)
            .Select(l => new { l.VersionId, l.AccountId, l.OrgUnitId, l.FiscalPeriodId, l.Amount }).ToListAsync(ct);
        var versions = await db.Set<GlBudgetVersion>().AsNoTracking().ToDictionaryAsync(v => v.Id, ct);
        var years = await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, y => y.Year, ct);
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var orgs = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, o => o.Code, ct);
        var usedPeriods = lines.Select(l => l.FiscalPeriodId).Distinct().ToList();
        var actual = (await (
                    from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                    where e.Status != JournalStatus.Draft && usedPeriods.Contains(e.FiscalPeriodId) && l.OrgUnitId != null
                    select new { e.FiscalPeriodId, l.AccountId, l.OrgUnitId, l.Debit, l.Credit })
                .ToListAsync(ct))
            .GroupBy(l => (l.AccountId, l.OrgUnitId!.Value, l.FiscalPeriodId)).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));
        var ordered = lines.OrderBy(l => periods[l.FiscalPeriodId].Start).ThenBy(l => versions[l.VersionId].Name, StringComparer.Ordinal)
            .ThenBy(l => accounts[l.AccountId].Number, StringComparer.Ordinal).ThenBy(l => orgs.GetValueOrDefault(l.OrgUnitId), StringComparer.Ordinal).ToList();
        return Rows(ordered, l =>
        {
            var v = versions[l.VersionId];
            var p = periods[l.FiscalPeriodId];
            var a = accounts[l.AccountId];
            var act = a.Natural(actual.GetValueOrDefault((l.AccountId, l.OrgUnitId, l.FiscalPeriodId)));
            var variance = a.Type == AccountType.Revenue ? act - l.Amount : l.Amount - act;
            return [v.Name, v.Kind.ToString(), v.Status.ToString(), years.GetValueOrDefault(v.FiscalYearId), p.Name, p.Start, a.Number, a.Name, a.Type.ToString(),
                orgs.GetValueOrDefault(l.OrgUnitId), l.Amount, act, variance];
        });
    }
}

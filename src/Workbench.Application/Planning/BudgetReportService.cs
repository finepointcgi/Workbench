using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.IndirectRates;
using Workbench.Application.Security;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Contracts.Planning;
using Workbench.Contracts.Security;
using Workbench.Domain.Accounting;
using Workbench.Domain.Planning;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Planning;

/// <summary>
/// Budget against actual: the income statement, org units and indirect pools for a period and the year to date, and
/// budgeted indirect rates (a version's pool budgets over its base budgets, through the same cascade the actual rates use).
/// Actuals are posted GL lines; budget and actual are both in the account's natural sign.
/// </summary>
public sealed class BudgetReportService(IWorkbenchDbContextFactory dbFactory, TimeProvider clock, SecurityEventLog? securityLog = null) : IBudgetReportService
{
    private sealed record Amount(Guid PeriodId, Guid AccountId, Guid? OrgUnitId, decimal Value);

    /// <summary>Budget and actual amounts for one version and period: the period, the year to date and the whole year (budget only).</summary>
    private sealed class Data
    {
        public required GlBudgetVersion Version { get; init; }
        public required FiscalPeriod Period { get; init; }
        public required List<FiscalPeriod> YearPeriods { get; init; }
        public required Dictionary<Guid, Account> Accounts { get; init; }
        public required Dictionary<Guid, OrgUnit> OrgUnits { get; init; }
        public required List<Amount> Budget { get; init; }
        public required List<Amount> Actual { get; init; }
        public HashSet<Guid> Ytd { get; init; } = [];

        public BudgetVsActualFigures Figures(Func<Amount, bool> match, bool isRevenue) => new(
            Budget.Where(a => a.PeriodId == Period.Id && match(a)).Sum(a => a.Value),
            Actual.Where(a => a.PeriodId == Period.Id && match(a)).Sum(a => a.Value),
            Budget.Where(a => Ytd.Contains(a.PeriodId) && match(a)).Sum(a => a.Value),
            Actual.Where(a => Ytd.Contains(a.PeriodId) && match(a)).Sum(a => a.Value),
            Budget.Where(match).Sum(a => a.Value),
            isRevenue);
    }

    private async Task<Data> LoadAsync(IWorkbenchDbContext db, BudgetVsActualQuery query, CancellationToken ct)
    {
        var version = await db.Set<GlBudgetVersion>().AsNoTracking().FirstOrDefaultAsync(v => v.Id == query.VersionId, ct) ?? throw WorkbenchNotFoundException.For("Budget version", query.VersionId);
        var periods = await BudgetMath.PeriodsAsync(db, version.FiscalYearId, ct);
        var period = periods.FirstOrDefault(p => p.Id == query.FiscalPeriodId)
            ?? throw new ArgumentException($"Pick a period in {version.Name}'s fiscal year.");
        var ytd = periods.Where(p => p.Number <= period.Number).Select(p => p.Id).ToHashSet();
        var accounts = await db.Set<Account>().AsNoTracking().ToDictionaryAsync(a => a.Id, ct);
        var org = query.OrgUnitId;
        var budget = await db.Set<GlBudgetLine>().AsNoTracking().Where(l => l.VersionId == version.Id && (org == null || l.OrgUnitId == org))
            .Select(l => new { l.FiscalPeriodId, l.AccountId, l.OrgUnitId, l.Amount }).ToListAsync(ct);
        var ytdIds = ytd.ToList();
        var actual = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.Status != JournalStatus.Draft && ytdIds.Contains(e.FiscalPeriodId) && (org == null || l.OrgUnitId == org)
                select new { e.FiscalPeriodId, l.AccountId, l.OrgUnitId, l.Debit, l.Credit })
            .ToListAsync(ct);
        return new Data
        {
            Version = version,
            Period = period,
            YearPeriods = periods,
            Accounts = accounts,
            OrgUnits = await db.Set<OrgUnit>().AsNoTracking().ToDictionaryAsync(o => o.Id, ct),
            Budget = budget.Select(b => new Amount(b.FiscalPeriodId, b.AccountId, b.OrgUnitId, b.Amount)).ToList(),
            Actual = actual.Where(a => accounts.ContainsKey(a.AccountId) && IsIncomeStatement(accounts[a.AccountId]))
                .Select(a => new Amount(a.FiscalPeriodId, a.AccountId, a.OrgUnitId, accounts[a.AccountId].Natural(a.Debit - a.Credit))).ToList(),
            Ytd = ytd,
        };
    }

    private static bool IsIncomeStatement(Account a) => a.Type is AccountType.Revenue or AccountType.Expense && a.CostClass != CostClassification.AppliedBurden;

    private BudgetVsActualDto Result(string title, Data d, Guid? orgUnitId, List<BudgetVsActualLineDto> lines, List<string> notes)
    {
        if (d.Budget.Count == 0)
        {
            notes.Add($"{d.Version.Name} has no budget lines{(orgUnitId is null ? "" : " for this org unit")} yet.");
        }

        var yearStart = d.YearPeriods.Min(p => p.Start);
        return new BudgetVsActualDto(title, d.Version.Id, d.Version.Name, d.Period.Id, d.Period.Name, yearStart, d.Period.End, orgUnitId,
            orgUnitId is { } o && d.OrgUnits.TryGetValue(o, out var unit) ? unit.Code : null, lines, notes);
    }

    public async Task<BudgetVsActualDto> GetIncomeStatementAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var d = await LoadAsync(db, query, cancellationToken);
        var used = d.Budget.Select(b => b.AccountId).Concat(d.Actual.Select(a => a.AccountId)).ToHashSet();
        var accounts = d.Accounts.Values.Where(a => used.Contains(a.Id) && IsIncomeStatement(a)).OrderBy(a => a.Number, StringComparer.Ordinal).ToList();
        var lines = new List<BudgetVsActualLineDto>();
        var totals = new Dictionary<string, BudgetVsActualFigures>();

        void Section(string section, IEnumerable<Account> members, string totalLabel)
        {
            var ids = members.Select(a => a.Id).ToHashSet();
            var revenue = section == "Revenue";
            foreach (var a in members)
            {
                lines.Add(new BudgetVsActualLineDto(section, a.Number, a.Name, d.Figures(x => x.AccountId == a.Id, revenue), Id: a.Id));
            }

            var total = d.Figures(x => ids.Contains(x.AccountId), revenue);
            totals[section] = total;
            lines.Add(new BudgetVsActualLineDto(section, null, totalLabel, total, true));
        }

        // The same sections as the income statement.
        Section("Revenue", accounts.Where(a => a.Type == AccountType.Revenue), "Total revenue");
        Section("Direct costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.DirectLabor or CostClassification.DirectOdc), "Total direct costs");
        Section("Indirect costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.IndirectLabor or CostClassification.Fringe
            or CostClassification.Overhead or CostClassification.GeneralAndAdministrative), "Total indirect costs");
        Section("Unallowable costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass == CostClassification.Unallowable), "Total unallowable costs");
        Section("Other expenses", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.None or CostClassification.Other), "Total other expenses");

        var expenses = totals.Where(t => t.Key != "Revenue").Select(t => t.Value).ToList();
        var exp = Sum(expenses, false);
        lines.Add(new BudgetVsActualLineDto("Summary", null, "Total expenses", exp, true));
        lines.Add(new BudgetVsActualLineDto("Summary", null, "Net income", Net(totals["Revenue"], exp), true));
        return Result("Income statement: budget vs actual", d, query.OrgUnitId, lines, []);
    }

    public async Task<BudgetVsActualDto> GetByOrgUnitAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var d = await LoadAsync(db, query, cancellationToken);
        bool Revenue(Amount a) => d.Accounts[a.AccountId].Type == AccountType.Revenue;
        bool Expense(Amount a) => d.Accounts[a.AccountId].Type == AccountType.Expense;
        var units = d.Budget.Select(b => b.OrgUnitId).Concat(d.Actual.Select(a => a.OrgUnitId)).Distinct()
            .OrderBy(o => o is null ? "~" : d.OrgUnits.GetValueOrDefault(o.Value)?.Code ?? "?", StringComparer.Ordinal).ToList();
        var lines = new List<BudgetVsActualLineDto>();
        var notes = new List<string>();
        foreach (var unit in units)
        {
            var label = unit is { } u && d.OrgUnits.TryGetValue(u, out var o) ? $"{o.Code} {o.Name}" : "No org unit";
            var revenue = d.Figures(a => a.OrgUnitId == unit && Revenue(a), true);
            var expense = d.Figures(a => a.OrgUnitId == unit && Expense(a), false);
            var code = unit is { } id && d.OrgUnits.TryGetValue(id, out var ou) ? ou.Code : null;
            lines.Add(new BudgetVsActualLineDto(label, code, "Revenue", revenue, Id: unit));
            lines.Add(new BudgetVsActualLineDto(label, code, "Expenses", expense, Id: unit));
            lines.Add(new BudgetVsActualLineDto(label, code, "Net", Net(revenue, expense), true, unit));
            if (unit is null)
            {
                notes.Add("\"No org unit\" is GL activity posted without an org unit; budgets always have one.");
            }
        }

        var allRevenue = d.Figures(Revenue, true);
        var allExpense = d.Figures(Expense, false);
        lines.Add(new BudgetVsActualLineDto("All org units", null, "Revenue", allRevenue, true));
        lines.Add(new BudgetVsActualLineDto("All org units", null, "Expenses", allExpense, true));
        lines.Add(new BudgetVsActualLineDto("All org units", null, "Net", Net(allRevenue, allExpense), true));
        return Result("Budget vs actual by org unit", d, query.OrgUnitId, lines, notes);
    }

    public async Task<BudgetVsActualDto> GetByPoolAsync(BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var d = await LoadAsync(db, query, cancellationToken);
        var book = await RateBook.LoadAsync(db, cancellationToken);
        var map = await PoolAccountMap.LoadAsync(db, book.Setups, cancellationToken);
        var lines = new List<BudgetVsActualLineDto>();
        var notes = new List<string>();
        if (book.Setups.Count == 0)
        {
            notes.Add("No indirect pools are set up (Finance › Indirect rates › Pools).");
        }

        foreach (var setup in book.Setups)
        {
            var ids = map.Pools.GetValueOrDefault(setup.Pool) ?? [];
            foreach (var id in ids.Where(id => d.Budget.Any(b => b.AccountId == id) || d.Actual.Any(a => a.AccountId == id)).OrderBy(id => d.Accounts[id].Number, StringComparer.Ordinal))
            {
                lines.Add(new BudgetVsActualLineDto(setup.Name, d.Accounts[id].Number, d.Accounts[id].Name, d.Figures(a => a.AccountId == id, false), Id: id));
            }

            lines.Add(new BudgetVsActualLineDto(setup.Name, null, $"Total {setup.Name} pool", d.Figures(a => ids.Contains(a.AccountId), false), true));
        }

        lines.Add(new BudgetVsActualLineDto("Allocation bases", null, "Direct labor", d.Figures(a => map.DirectLabor.Contains(a.AccountId), false)));
        lines.Add(new BudgetVsActualLineDto("Allocation bases", null, "Direct ODCs", d.Figures(a => map.DirectOdc.Contains(a.AccountId), false)));
        notes.Add("Rates built from these budgets are under Finance › Indirect rates › Budgeted rates.");
        return Result("Budget vs actual by indirect pool", d, query.OrgUnitId, lines, notes);
    }

    public async Task<BudgetedRatesDto> GetBudgetedRatesAsync(int fiscalYear, Guid? versionId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var (year, periods) = await BudgetMath.YearAsync(db, fiscalYear, cancellationToken);
        var versions = await db.Set<GlBudgetVersion>().AsNoTracking().Where(v => v.FiscalYearId == year.Id).ToListAsync(cancellationToken);
        var version = versionId is { } vid
            ? versions.FirstOrDefault(v => v.Id == vid) ?? throw new ArgumentException($"That budget version isn't for FY{fiscalYear}.")
            : versions.Where(v => v.Status == GlBudgetStatus.Adopted && v.Kind != GlBudgetKind.Forecast).MaxBy(v => v.AdoptedAtUtc)
                ?? versions.MaxBy(v => v.CreatedAtUtc);
        var book = await RateBook.LoadAsync(db, cancellationToken);
        var map = await PoolAccountMap.LoadAsync(db, book.Setups, cancellationToken);
        var notes = new List<string>();
        if (book.Steps.Count == 0)
        {
            notes.Add("No indirect pools are set up.");
        }

        // Budget: every period of the version, all org units. Expense budgets are debit-positive, like the GL.
        var budget = version is null
            ? new Dictionary<Guid, decimal>()
            : (await db.Set<GlBudgetLine>().AsNoTracking().Where(l => l.VersionId == version.Id).Select(l => new { l.AccountId, l.Amount }).ToListAsync(cancellationToken))
                .GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Amount));
        if (version is null)
        {
            notes.Add($"FY{fiscalYear} has no budget version yet (Finance › Planning › Budgets).");
        }

        // Actual: posted GL in the year's periods that have started.
        var today = DateOnly.FromDateTime(clock.GetLocalNow().DateTime);
        var started = periods.Where(p => p.Start <= today).ToList();
        var startedIds = started.Select(p => p.Id).ToList();
        var actual = (await (
                    from l in db.Set<JournalLine>().AsNoTracking()
                    join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                    where e.Status != JournalStatus.Draft && startedIds.Contains(e.FiscalPeriodId)
                    select new { l.AccountId, l.Debit, l.Credit })
                .ToListAsync(cancellationToken))
            .GroupBy(l => l.AccountId).ToDictionary(g => g.Key, g => g.Sum(l => l.Debit - l.Credit));

        static decimal Sum(Dictionary<Guid, decimal> amounts, IEnumerable<Guid> ids) => ids.Sum(id => amounts.GetValueOrDefault(id));
        decimal BudgetPool(IndirectPool p) => Sum(budget, map.Pools.GetValueOrDefault(p) ?? []);
        decimal ActualPool(IndirectPool p) => Sum(actual, map.Pools.GetValueOrDefault(p) ?? []);
        var budgeted = BurdenCascade.ActualRates(book.Steps, Sum(budget, map.DirectLabor), Sum(budget, map.DirectOdc), BudgetPool).ToDictionary(r => r.Pool);
        var actuals = BurdenCascade.ActualRates(book.Steps, Sum(actual, map.DirectLabor), Sum(actual, map.DirectOdc), ActualPool).ToDictionary(r => r.Pool);
        var pools = book.Setups.Select(s => new BudgetedRateDto(
                (CostPool)(int)s.Pool, s.Name, BudgetPool(s.Pool), budgeted[s.Pool].Base, budgeted[s.Pool].Rate,
                book.Rate(s.Pool, fiscalYear, IndirectRateBasis.Provisional), book.Rate(s.Pool, fiscalYear, IndirectRateBasis.Target),
                ActualPool(s.Pool), actuals[s.Pool].Base, actuals[s.Pool].Rate))
            .ToList();
        if (version is not null && pools.Any(p => p.BudgetedRate is null))
        {
            notes.Add("A pool without a budgeted base (direct labor, for example) has no budgeted rate.");
        }

        return new BudgetedRatesDto(fiscalYear, version?.Id, version?.Name, started.Count == 0 ? "no periods yet" : started.MaxBy(p => p.Start)!.Name, pools, notes);
    }

    public async Task<AccountingExportFile> ExportCsvAsync(BudgetReport report, BudgetVsActualQuery query, CancellationToken cancellationToken = default)
    {
        var result = report switch
        {
            BudgetReport.ByOrgUnit => await GetByOrgUnitAsync(query, cancellationToken),
            BudgetReport.ByPool => await GetByPoolAsync(query, cancellationToken),
            _ => await GetIncomeStatementAsync(query, cancellationToken),
        };
        var csv = new StringBuilder();
        csv.AppendLine("Section,Code,Line,Period budget,Period actual,Period variance,Period variance %,YTD budget,YTD actual,YTD variance,YTD variance %,Annual budget");
        foreach (var l in result.Lines)
        {
            var f = l.Figures;
            csv.AppendLine(string.Join(',', Quote(l.Section), Quote(l.Code ?? ""), Quote(l.Label), Num(f.PeriodBudget), Num(f.PeriodActual), Num(f.PeriodVariance),
                f.PeriodVariancePercent?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", Num(f.YtdBudget), Num(f.YtdActual), Num(f.YtdVariance),
                f.YtdVariancePercent?.ToString("0.0", CultureInfo.InvariantCulture) ?? "", Num(f.AnnualBudget)));
        }

        var name = $"budget-vs-actual-{report.ToString().ToLowerInvariant()}-{result.FiscalPeriodName}.csv";
        if (securityLog is not null)
        {
            await securityLog.RecordAsync(SecurityEventTypes.ExportFinancialCsv, name, detail: $"Budget vs actual ({report}), {result.VersionName}", cancellationToken: cancellationToken);
        }

        return new AccountingExportFile(name, "text/csv", csv.ToString());
    }

    private static string Num(decimal v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Quote(string v) => v.IndexOfAny([',', '"', '\n']) >= 0 ? $"\"{v.Replace("\"", "\"\"")}\"" : v;

    private static BudgetVsActualFigures Sum(IReadOnlyList<BudgetVsActualFigures> parts, bool isRevenue) => new(
        parts.Sum(p => p.PeriodBudget), parts.Sum(p => p.PeriodActual), parts.Sum(p => p.YtdBudget), parts.Sum(p => p.YtdActual), parts.Sum(p => p.AnnualBudget), isRevenue);

    /// <summary>Revenue less expenses; a higher net is favourable.</summary>
    private static BudgetVsActualFigures Net(BudgetVsActualFigures revenue, BudgetVsActualFigures expense) => new(
        revenue.PeriodBudget - expense.PeriodBudget, revenue.PeriodActual - expense.PeriodActual, revenue.YtdBudget - expense.YtdBudget,
        revenue.YtdActual - expense.YtdActual, revenue.AnnualBudget - expense.AnnualBudget, true);
}

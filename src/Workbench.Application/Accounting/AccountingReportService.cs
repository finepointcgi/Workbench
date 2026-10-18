using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Workbench.Application.Financials;
using Workbench.Contracts;
using Workbench.Contracts.Accounting;
using Workbench.Domain.Accounting;
using Workbench.Domain.ProjectAccounting;

namespace Workbench.Application.Accounting;

/// <summary>
/// Ledger reports built from posted lines (posted and reversed entries both count; a reversal nets its original out).
/// Balance-sheet accounts accumulate since inception; income-statement accounts reset each fiscal year, with prior
/// years' net income shown in retained earnings.
/// </summary>
public sealed class AccountingReportService(IWorkbenchDbContextFactory dbFactory) : IAccountingReportService
{
    private sealed record PostedLine(
        Guid EntryId, string Number, int Sequence, DateOnly Date, Guid PeriodId, JournalSource Source, string Description,
        int LineNumber, Guid AccountId, Guid? OrgUnitId, Guid? ProjectId, Guid? WbsElementId, decimal Debit, decimal Credit, string? Memo)
    {
        public decimal Net => Debit - Credit;
    }

    private sealed record Ledger(LedgerLookups Lookups, List<PostedLine> Lines, Dictionary<Guid, FiscalYear> Years)
    {
        public FiscalPeriod Period(Guid id) => Lookups.Periods[id];

        public DateOnly YearStart(FiscalPeriod p) => Years.TryGetValue(p.FiscalYearId, out var y) ? y.Start : p.Start;

        public static bool IsIncomeStatement(Account a) => a.Type is AccountType.Revenue or AccountType.Expense;
    }

    private static async Task<Ledger> LoadAsync(IWorkbenchDbContext db, CancellationToken ct)
    {
        var lines = await (
                from l in db.Set<JournalLine>().AsNoTracking()
                join e in db.Set<JournalEntry>().AsNoTracking() on l.JournalEntryId equals e.Id
                where e.Status != JournalStatus.Draft
                select new PostedLine(e.Id, e.Number, e.Sequence, e.Date, e.FiscalPeriodId, e.Source, e.Description,
                    l.LineNumber, l.AccountId, l.OrgUnitId, l.ProjectId, l.WbsElementId, l.Debit, l.Credit, l.Memo))
            .ToListAsync(ct);
        return new Ledger(await LedgerLookups.LoadAsync(db, ct), lines, await db.Set<FiscalYear>().AsNoTracking().ToDictionaryAsync(y => y.Id, ct));
    }

    private static FiscalPeriod RequirePeriod(Ledger ledger, Guid id) =>
        ledger.Lookups.Periods.TryGetValue(id, out var p) ? p : throw WorkbenchNotFoundException.For("Fiscal period", id);

    // ---- Trial balance ----

    public async Task<TrialBalanceDto> GetTrialBalanceAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return TrialBalance(await LoadAsync(db, cancellationToken), fiscalPeriodId);
    }

    private static TrialBalanceDto TrialBalance(Ledger ledger, Guid periodId)
    {
        var period = RequirePeriod(ledger, periodId);
        var yearStart = ledger.YearStart(period);
        var accounts = ledger.Lookups.Accounts;
        var opening = new Dictionary<Guid, decimal>();
        var debit = new Dictionary<Guid, decimal>();
        var credit = new Dictionary<Guid, decimal>();
        var priorYearsNet = 0m;
        foreach (var line in ledger.Lines)
        {
            var lp = ledger.Period(line.PeriodId);
            var account = accounts[line.AccountId];
            if (lp.Id == period.Id)
            {
                debit[line.AccountId] = debit.GetValueOrDefault(line.AccountId) + line.Debit;
                credit[line.AccountId] = credit.GetValueOrDefault(line.AccountId) + line.Credit;
            }
            else if (lp.Start < period.Start)
            {
                if (Ledger.IsIncomeStatement(account) && lp.Start < yearStart)
                {
                    priorYearsNet += line.Net; // closed into retained earnings
                }
                else
                {
                    opening[line.AccountId] = opening.GetValueOrDefault(line.AccountId) + line.Net;
                }
            }
        }

        var rows = new List<TrialBalanceRowDto>();
        var retained = accounts.Values.Where(a => a.Type == AccountType.Equity && a.Name.Contains("Retained", StringComparison.OrdinalIgnoreCase))
            .OrderBy(a => a.Number, StringComparer.Ordinal).FirstOrDefault();
        if (priorYearsNet != 0)
        {
            if (retained is not null)
            {
                opening[retained.Id] = opening.GetValueOrDefault(retained.Id) + priorYearsNet;
            }
            else
            {
                rows.Add(new TrialBalanceRowDto(Guid.Empty, "", "Prior years' net income", Contracts.Accounting.AccountKind.Equity, priorYearsNet, 0, 0, priorYearsNet));
            }
        }

        foreach (var account in accounts.Values.OrderBy(a => a.Number, StringComparer.Ordinal))
        {
            var o = opening.GetValueOrDefault(account.Id);
            var d = debit.GetValueOrDefault(account.Id);
            var c = credit.GetValueOrDefault(account.Id);
            if (o == 0 && d == 0 && c == 0)
            {
                continue;
            }

            rows.Add(new TrialBalanceRowDto(account.Id, account.Number, account.Name, account.Type.ToContract(), o, d, c, o + d - c));
        }

        return new TrialBalanceDto(period.Id, period.Name, rows, rows.Sum(r => r.PeriodDebit), rows.Sum(r => r.PeriodCredit),
            rows.Sum(r => r.EndingDebit), rows.Sum(r => r.EndingCredit));
    }

    // ---- General ledger detail ----

    public async Task<LedgerDetailDto> GetLedgerDetailAsync(LedgerQuery query, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return LedgerDetail(await LoadAsync(db, cancellationToken), query);
    }

    private static LedgerDetailDto LedgerDetail(Ledger ledger, LedgerQuery query)
    {
        var accounts = ledger.Lookups.Accounts;
        HashSet<Guid>? accountIds = null;
        if (query.AccountId is { } aid)
        {
            if (!accounts.ContainsKey(aid))
            {
                throw WorkbenchNotFoundException.For("Account", aid);
            }

            // A summary account shows the detail of everything that rolls up into it.
            accountIds = [aid];
            for (var added = true; added;)
            {
                var children = accounts.Values.Where(a => a.ParentId is { } p && accountIds.Contains(p) && !accountIds.Contains(a.Id)).Select(a => a.Id).ToList();
                accountIds.UnionWith(children);
                added = children.Count > 0;
            }
        }

        var matching = ledger.Lines.Where(l =>
                (accountIds is null || accountIds.Contains(l.AccountId))
                && (query.OrgUnitId is null || l.OrgUnitId == query.OrgUnitId)
                && (query.ProjectId is null || l.ProjectId == query.ProjectId)
                && (query.WbsElementId is null || l.WbsElementId == query.WbsElementId))
            .ToList();
        var opening = query.From is { } from ? matching.Where(l => l.Date < from).Sum(l => l.Net) : 0m;
        var inRange = matching.Where(l => (query.From is null || l.Date >= query.From) && (query.To is null || l.Date <= query.To))
            .OrderBy(l => l.Date).ThenBy(l => l.Sequence).ThenBy(l => l.LineNumber)
            .Take(Math.Clamp(query.Take, 1, 20000))
            .ToList();

        var running = opening;
        var result = new List<LedgerLineDto>(inRange.Count);
        foreach (var l in inRange)
        {
            running += l.Net;
            var account = accounts[l.AccountId];
            result.Add(new LedgerLineDto(l.EntryId, l.Number, l.Date, ledger.Lookups.PeriodName(l.PeriodId), l.Source.ToContract(), l.Description,
                account.Number, account.Name,
                l.OrgUnitId is { } o ? ledger.Lookups.OrgUnits.GetValueOrDefault(o) : null,
                l.ProjectId is { } p ? ledger.Lookups.Projects.GetValueOrDefault(p) : null,
                l.WbsElementId is { } w && ledger.Lookups.Wbs.TryGetValue(w, out var wbs) ? wbs.Code : null,
                l.Debit, l.Credit, running, l.Memo));
        }

        return new LedgerDetailDto(query, opening, result, result.Sum(r => r.Debit), result.Sum(r => r.Credit), running);
    }

    // ---- Income statement and balance sheet ----

    public async Task<FinancialStatementDto> GetIncomeStatementAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return IncomeStatement(await LoadAsync(db, cancellationToken), fiscalPeriodId);
    }

    private static (Dictionary<Guid, decimal> Period, Dictionary<Guid, decimal> Ytd) PeriodAndYtd(Ledger ledger, FiscalPeriod period)
    {
        var yearStart = ledger.YearStart(period);
        var inPeriod = new Dictionary<Guid, decimal>();
        var ytd = new Dictionary<Guid, decimal>();
        foreach (var line in ledger.Lines)
        {
            var lp = ledger.Period(line.PeriodId);
            if (lp.Start >= yearStart && lp.Start <= period.Start)
            {
                ytd[line.AccountId] = ytd.GetValueOrDefault(line.AccountId) + line.Net;
                if (lp.Id == period.Id)
                {
                    inPeriod[line.AccountId] = inPeriod.GetValueOrDefault(line.AccountId) + line.Net;
                }
            }
        }

        return (inPeriod, ytd);
    }

    private static FinancialStatementDto IncomeStatement(Ledger ledger, Guid periodId)
    {
        var period = RequirePeriod(ledger, periodId);
        var (inPeriod, ytd) = PeriodAndYtd(ledger, period);
        var accounts = ledger.Lookups.Accounts.Values.Where(Ledger.IsIncomeStatement).OrderBy(a => a.Number, StringComparer.Ordinal).ToList();
        var lines = new List<StatementLineDto>();

        void Section(string section, IEnumerable<Account> members, int sign, string totalLabel)
        {
            var (p, y) = (0m, 0m);
            foreach (var a in members)
            {
                var (ap, ay) = (sign * inPeriod.GetValueOrDefault(a.Id), sign * ytd.GetValueOrDefault(a.Id));
                if (ap == 0 && ay == 0)
                {
                    continue;
                }

                lines.Add(new StatementLineDto(section, a.Number, a.Name, ap, ay));
                (p, y) = (p + ap, y + ay);
            }

            lines.Add(new StatementLineDto(section, null, totalLabel, p, y, true));
        }

        // Revenue is credit-positive, expenses debit-positive.
        Section("Revenue", accounts.Where(a => a.Type == AccountType.Revenue), -1, "Total revenue");
        Section("Direct costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.DirectLabor or CostClassification.DirectOdc), 1, "Total direct costs");
        Section("Indirect costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.IndirectLabor or CostClassification.Fringe
            or CostClassification.Overhead or CostClassification.GeneralAndAdministrative), 1, "Total indirect costs");
        Section("Unallowable costs", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass == CostClassification.Unallowable), 1, "Total unallowable costs");
        Section("Other expenses", accounts.Where(a => a.Type == AccountType.Expense && a.CostClass is CostClassification.None or CostClassification.Other), 1, "Total other expenses");

        var totals = lines.Where(l => l.IsTotal).ToDictionary(l => l.Section);
        var expenses = totals.Where(t => t.Key != "Revenue").Select(t => t.Value).ToList();
        var (expP, expY) = (expenses.Sum(e => e.Period), expenses.Sum(e => e.YearToDate));
        lines.Add(new StatementLineDto("Summary", null, "Total expenses", expP, expY, true));
        lines.Add(new StatementLineDto("Summary", null, "Net income", totals["Revenue"].Period - expP, totals["Revenue"].YearToDate - expY, true));
        return new FinancialStatementDto("Income statement", period.Id, period.Name, ledger.YearStart(period), period.End, "Period", "Year to date", lines);
    }

    public async Task<FinancialStatementDto> GetBalanceSheetAsync(Guid fiscalPeriodId, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return BalanceSheet(await LoadAsync(db, cancellationToken), fiscalPeriodId);
    }

    private static FinancialStatementDto BalanceSheet(Ledger ledger, Guid periodId)
    {
        var period = RequirePeriod(ledger, periodId);
        var trial = TrialBalance(ledger, periodId);
        var (inPeriod, ytd) = PeriodAndYtd(ledger, period);
        var byAccount = trial.Rows.Where(r => r.AccountId != Guid.Empty).ToDictionary(r => r.AccountId);
        var lines = new List<StatementLineDto>();

        (decimal Change, decimal Balance) Section(string section, AccountType type, int sign, string totalLabel, params StatementLineDto[] extra)
        {
            var (c, b) = (0m, 0m);
            foreach (var a in ledger.Lookups.Accounts.Values.Where(a => a.Type == type).OrderBy(a => a.Number, StringComparer.Ordinal))
            {
                if (!byAccount.TryGetValue(a.Id, out var row))
                {
                    continue;
                }

                var (change, balance) = (sign * (row.PeriodDebit - row.PeriodCredit), sign * row.Ending);
                lines.Add(new StatementLineDto(section, a.Number, a.Name, change, balance));
                (c, b) = (c + change, b + balance);
            }

            foreach (var line in extra)
            {
                lines.Add(line);
                (c, b) = (c + line.Period, b + line.YearToDate);
            }

            lines.Add(new StatementLineDto(section, null, totalLabel, c, b, true));
            return (c, b);
        }

        // Net income (credit-positive) not yet closed to retained earnings: this fiscal year's income-statement activity.
        var incomeAccounts = ledger.Lookups.Accounts.Values.Where(Ledger.IsIncomeStatement).Select(a => a.Id).ToHashSet();
        var netPeriod = -inPeriod.Where(kv => incomeAccounts.Contains(kv.Key)).Sum(kv => kv.Value);
        var netYtd = -ytd.Where(kv => incomeAccounts.Contains(kv.Key)).Sum(kv => kv.Value);
        var priorYears = trial.Rows.Where(r => r.AccountId == Guid.Empty).Select(r => new StatementLineDto("Equity", null, r.AccountName, 0, -r.Ending)).ToArray();

        Section("Assets", AccountType.Asset, 1, "Total assets");
        var liabilities = Section("Liabilities", AccountType.Liability, -1, "Total liabilities");
        var equity = Section("Equity", AccountType.Equity, -1, "Total equity",
            [.. priorYears, new StatementLineDto("Equity", null, "Net income, current year", netPeriod, netYtd)]);
        lines.Add(new StatementLineDto("Summary", null, "Total liabilities and equity", liabilities.Change + equity.Change, liabilities.Balance + equity.Balance, true));
        return new FinancialStatementDto("Balance sheet", period.Id, period.Name, ledger.YearStart(period), period.End, "Change in period", "Balance at period end", lines);
    }

    // ---- Project cost ledger and reconciliation ----

    public async Task<IReadOnlyList<ProjectCostRowDto>> GetProjectCostLedgerAsync(Guid? projectId = null, Guid? fiscalPeriodId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        return ProjectCost(await LoadAsync(db, cancellationToken), projectId, fiscalPeriodId);
    }

    private static IReadOnlyList<ProjectCostRowDto> ProjectCost(Ledger ledger, Guid? projectId, Guid? periodId)
    {
        if (periodId is { } pid)
        {
            RequirePeriod(ledger, pid);
        }

        var accounts = ledger.Lookups.Accounts;
        var rows = ledger.Lines
            .Where(l => l.ProjectId is not null && accounts[l.AccountId].Type == AccountType.Expense
                && (projectId is null || l.ProjectId == projectId) && (periodId is null || l.PeriodId == periodId))
            .GroupBy(l => (Project: l.ProjectId!.Value, l.WbsElementId))
            .Select(g =>
            {
                decimal Sum(params CostClassification[] classes) => g.Where(l => classes.Contains(accounts[l.AccountId].CostClass)).Sum(l => l.Net);
                var wbs = g.Key.WbsElementId is { } w ? ledger.Lookups.Wbs.GetValueOrDefault(w) : null;
                return new ProjectCostRowDto(g.Key.Project, ledger.Lookups.Projects.GetValueOrDefault(g.Key.Project) ?? "?", g.Key.WbsElementId, wbs?.Code, wbs?.Name,
                    Sum(CostClassification.DirectLabor),
                    Sum(CostClassification.DirectOdc),
                    Sum(CostClassification.IndirectLabor, CostClassification.Fringe, CostClassification.Overhead, CostClassification.GeneralAndAdministrative),
                    Sum(CostClassification.Unallowable),
                    Sum(CostClassification.None, CostClassification.Other));
            })
            .Where(r => r.Total != 0 || r.Unallowable != 0)
            .OrderBy(r => r.ProjectCode, StringComparer.Ordinal).ThenBy(r => r.WbsCode, StringComparer.Ordinal)
            .ToList();
        return rows;
    }

    public async Task<ReconciliationDto> GetReconciliationAsync(Guid? projectId = null, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ledger = await LoadAsync(db, cancellationToken);
        var projectIds = projectId is { } only ? [only] : ledger.Lookups.Projects.Keys.ToList();
        var notes = new List<string>();
        var (labor, odcs) = await FinancialsLoader.LoadActualsAsync(db, projectIds, null, notes, cancellationToken);

        // GL side: allowable cost on project WBS lines. Direct ODC accounts are ODCs; every other allowable expense
        // account (direct and indirect labor, leave) is labor. Unallowable accounts are excluded, like the financials.
        var accounts = ledger.Lookups.Accounts;
        // Applied burden (indirect rates' allocations) is reported beside, not inside, the direct cost.
        var gl = ledger.Lines
            .Where(l => l.WbsElementId is not null && accounts[l.AccountId].Type == AccountType.Expense
                && accounts[l.AccountId].CostClass != CostClassification.Unallowable
                && (projectId is null || l.ProjectId == projectId))
            .GroupBy(l => l.WbsElementId!.Value)
            .ToDictionary(g => g.Key, g => (
                Labor: g.Where(l => accounts[l.AccountId].CostClass is not (CostClassification.DirectOdc or CostClassification.AppliedBurden)).Sum(l => l.Net),
                Odc: g.Where(l => accounts[l.AccountId].CostClass == CostClassification.DirectOdc).Sum(l => l.Net),
                Burden: g.Where(l => accounts[l.AccountId].CostClass == CostClassification.AppliedBurden).Sum(l => l.Net)));
        var finLabor = labor.GroupBy(l => l.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(l => l.Cost));
        var finOdc = odcs.GroupBy(o => o.WbsElementId).ToDictionary(g => g.Key, g => g.Sum(o => o.Amount));

        var rows = gl.Keys.Concat(finLabor.Keys).Concat(finOdc.Keys).Distinct()
            .Select(id =>
            {
                var wbs = ledger.Lookups.Wbs.GetValueOrDefault(id);
                var g = gl.GetValueOrDefault(id);
                return new ReconciliationRowDto(wbs?.ProjectId ?? Guid.Empty, wbs is null ? "?" : ledger.Lookups.Projects.GetValueOrDefault(wbs.ProjectId) ?? "?",
                    id, wbs?.Code ?? "?", finLabor.GetValueOrDefault(id), finOdc.GetValueOrDefault(id), g.Labor, g.Odc, g.Burden);
            })
            .Where(r => r.FinancialsTotal != 0 || r.GlTotal != 0 || r.GlAppliedBurden != 0)
            .OrderBy(r => r.ProjectCode, StringComparer.Ordinal).ThenBy(r => r.WbsCode, StringComparer.Ordinal)
            .ToList();
        var unreconciled = rows.Count(r => !r.IsReconciled);
        if (unreconciled > 0)
        {
            notes.Add($"{unreconciled} WBS element(s) differ. Common causes: approved items not yet posted (see Period close), manual entries with project segments, or hours without a cost rate.");
        }

        return new ReconciliationDto(rows, notes);
    }

    // ---- CSV ----

    public async Task<AccountingExportFile> ExportCsvAsync(AccountingExportRequest request, CancellationToken cancellationToken = default)
    {
        await using var db = await dbFactory.CreateAsync(cancellationToken);
        var ledger = await LoadAsync(db, cancellationToken);
        var csv = new Csv();
        string PeriodSuffix() => request.FiscalPeriodId is { } p ? "-" + RequirePeriod(ledger, p).Name : "";
        Guid RequirePeriodId() => request.FiscalPeriodId ?? throw new ArgumentException("Choose a fiscal period.");
        string name;
        switch (request.Report)
        {
            case AccountingReport.TrialBalance:
                var tb = TrialBalance(ledger, RequirePeriodId());
                csv.Row("Account", "Name", "Type", "Opening", "Period debit", "Period credit", "Ending debit", "Ending credit");
                foreach (var r in tb.Rows)
                {
                    csv.Row(r.AccountNumber, r.AccountName, r.Type, r.Opening, r.PeriodDebit, r.PeriodCredit, r.EndingDebit, r.EndingCredit);
                }

                csv.Row("Total", "", "", "", tb.TotalPeriodDebit, tb.TotalPeriodCredit, tb.TotalEndingDebit, tb.TotalEndingCredit);
                name = "trial-balance" + PeriodSuffix();
                break;
            case AccountingReport.LedgerDetail:
                var detail = LedgerDetail(ledger, request.Ledger ?? new LedgerQuery());
                csv.Row("Entry", "Date", "Period", "Source", "Description", "Account", "Account name", "Org unit", "Project", "WBS", "Debit", "Credit", "Balance", "Memo");
                csv.Row("", "", "", "", "Opening balance", "", "", "", "", "", "", "", detail.OpeningBalance, "");
                foreach (var l in detail.Lines)
                {
                    csv.Row(l.EntryNumber, l.Date, l.PeriodName, l.Source, l.Description, l.AccountNumber, l.AccountName, l.OrgUnitCode, l.ProjectCode, l.WbsCode,
                        l.Debit, l.Credit, l.RunningBalance, l.Memo);
                }

                name = "general-ledger";
                break;
            case AccountingReport.IncomeStatement:
            case AccountingReport.BalanceSheet:
                var statement = request.Report == AccountingReport.IncomeStatement ? IncomeStatement(ledger, RequirePeriodId()) : BalanceSheet(ledger, RequirePeriodId());
                csv.Row("Section", "Account", "Line", statement.PeriodLabel, statement.YearToDateLabel);
                foreach (var l in statement.Lines)
                {
                    csv.Row(l.Section, l.AccountNumber, l.Label, l.Period, l.YearToDate);
                }

                name = (request.Report == AccountingReport.IncomeStatement ? "income-statement" : "balance-sheet") + PeriodSuffix();
                break;
            case AccountingReport.ProjectCost:
                csv.Row("Project", "WBS", "WBS name", "Direct labor", "Direct ODC", "Indirect and leave", "Other", "Allowable", "Unallowable", "Total");
                foreach (var r in ProjectCost(ledger, request.ProjectId, request.FiscalPeriodId))
                {
                    csv.Row(r.ProjectCode, r.WbsCode, r.WbsName, r.DirectLabor, r.DirectOdc, r.Indirect, r.Other, r.Allowable, r.Unallowable, r.Total);
                }

                name = "project-cost-ledger" + PeriodSuffix();
                break;
            case AccountingReport.Reconciliation:
                var rec = await GetReconciliationAsync(request.ProjectId, cancellationToken);
                csv.Row("Project", "WBS", "Financials labor", "Financials ODC", "Financials total", "GL labor", "GL ODC", "GL total", "Difference", "Status");
                foreach (var r in rec.Rows)
                {
                    csv.Row(r.ProjectCode, r.WbsCode, r.FinancialsLabor, r.FinancialsOdc, r.FinancialsTotal, r.GlLabor, r.GlOdc, r.GlTotal, r.Difference,
                        r.IsReconciled ? "Reconciled" : "DIFFERENCE");
                }

                name = "gl-reconciliation";
                break;
            case AccountingReport.JournalEntries:
                csv.Row("Entry", "Date", "Period", "Source", "Status", "Description", "Line", "Account", "Account name", "Org unit", "Project", "WBS", "Debit", "Credit", "Memo");
                var entries = await db.Set<JournalEntry>().AsNoTracking().Include(e => e.Lines)
                    .Where(e => request.FiscalPeriodId == null || e.FiscalPeriodId == request.FiscalPeriodId)
                    .ToListAsync(cancellationToken);
                foreach (var e in entries.OrderBy(e => e.Date).ThenBy(e => e.Sequence))
                {
                    foreach (var l in e.Lines.OrderBy(l => l.LineNumber).Select(ledger.Lookups.Line))
                    {
                        csv.Row(e.Number, e.Date, ledger.Lookups.PeriodName(e.FiscalPeriodId), e.Source, e.Status, e.Description, l.LineNumber, l.AccountNumber,
                            l.AccountName, l.OrgUnitCode, l.ProjectCode, l.WbsCode, l.Debit, l.Credit, l.Memo);
                    }
                }

                name = "journal-entries" + PeriodSuffix();
                break;
            default:
                throw new ArgumentException($"Unknown report {request.Report}.");
        }

        return new AccountingExportFile(name + ".csv", "text/csv", csv.ToString());
    }

    private sealed class Csv
    {
        private readonly StringBuilder _sb = new();

        public void Row(params object?[] values)
        {
            _sb.AppendJoin(',', values.Select(Format));
            _sb.Append("\r\n");
        }

        private static string Format(object? value)
        {
            var text = value switch
            {
                null => "",
                decimal d => d.ToString("0.00", CultureInfo.InvariantCulture),
                DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
                _ => value.ToString() ?? "",
            };

            // Quote when needed, and defuse spreadsheet formulas in text cells.
            if (value is string && text.Length > 0 && "=+-@".Contains(text[0]))
            {
                text = "'" + text;
            }

            return text.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
        }

        public override string ToString() => _sb.ToString();
    }
}
